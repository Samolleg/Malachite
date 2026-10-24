using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

// Shared read-only explanations for commands and stage summaries in the inspector.
// Bindings describe the source WorldScript, never the current state of a game.
public sealed class ScenarioExplanation
{
    readonly Analysis analysis;
    readonly HelpService help;
    readonly WorkspaceSnapshot? snapshot;
    readonly Dictionary<uint, MobObject[]> objects;
    readonly Dictionary<string, (uint Id, int Line)> bindings = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> assigned = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Call> calls;

    public ScenarioExplanation(Analysis analysis, HelpService help, IReadOnlyList<MobObject>? objects = null,
        WorkspaceSnapshot? snapshot = null, CommandRegistry? registry = null)
    {
        this.analysis = analysis; this.help = help; this.snapshot = snapshot;
        this.objects = (objects ?? []).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.ToArray());
        calls = analysis.Calls.GroupBy(c => c.Start).ToDictionary(g => g.Key, g => g.First());
        var tokens = analysis.Tokens;
        var writes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i].Kind == "id" && tokens[i + 1].Value == "=")
            {
                assigned.Add(tokens[i].Value);
                if (!writes.TryGetValue(tokens[i].Value, out var entries)) writes[tokens[i].Value] = entries = [];
                entries.Add(i);
            }
        // Commands taking a global object by reference can replace its binding.
        var indirect = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (registry != null)
            foreach (var call in analysis.Calls)
                if (registry.Commands.TryGetValue(call.Name, out var command) && command.Parameters.Length > 0)
                    for (int n = 0; n < call.Arguments.Count; n++)
                        if (command.Argument(n) is { Global: true, Type: "object" }) indirect.Add(analysis.Slice(call.Arguments[n]).Trim());
        foreach (var (name, entries) in writes)
        {
            if (entries.Count != 1 || indirect.Contains(name) || analysis.Globals.GetValueOrDefault(name) != "object") continue;
            int at = entries[0];
            var world = analysis.BlockAt(tokens[at].Start);
            if (world?.World != true || at + 2 >= tokens.Count || !calls.TryGetValue(tokens[at + 2].Start, out var call) ||
                !TryObjectId(call, out uint id)) continue;
            // Only an immediate statement in WorldScript, not an if/loop/body argument.
            if (analysis.Pairs.Any(p => p.Key < p.Value && tokens[p.Key].Start >= world.Body.Start &&
                tokens[p.Key].Start < tokens[at].Start && tokens[p.Value].Start > tokens[at].Start)) continue;
            var next = tokens.FirstOrDefault(t => t.Start >= call.End);
            int nextIndex = next == null ? -1 : tokens.IndexOf(next);
            if (next != null && next.Value != ")" && !calls.ContainsKey(next.Start) &&
                !(nextIndex + 1 < tokens.Count && tokens[nextIndex + 1].Value == "=")) continue;
            bindings[name] = (id, tokens[at].Line);
        }
    }

    bool TryObjectId(Call call, out uint id)
    {
        id = 0;
        if (!call.Name.Equals("GetObjectByID", StringComparison.OrdinalIgnoreCase) || call.Arguments.Count != 1) return false;
        var raw = analysis.Slice(call.Arguments[0]).Trim();
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' &&
            uint.TryParse(raw[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    string Object(uint id)
    {
        if (!objects.TryGetValue(id, out var matches)) return "ID " + id + " [объект не найден в открытой MOB]";
        if (matches.Length != 1) return "ID " + id + " [несколько объектов в MOB; имя неоднозначно]";
        return (matches[0].Name.Length == 0 ? "Без имени" : matches[0].Name) + " · ID " + id + " [имя из MOB]";
    }

    string Argument(Call call, int index, int depth)
    {
        var range = call.Arguments[index];
        var nested = analysis.Calls.FirstOrDefault(c => c.Start >= range.Start && c.End <= range.End &&
            analysis.Text[range.Start..c.Start].Trim().Length == 0 && analysis.Text[c.End..range.End].Trim().Length == 0);
        if (nested != null) return depth < 16 ? "(" + Detail(nested, false, depth + 1) + ")" : Unknown(nested);
        var raw = analysis.Slice(range).Trim();
        // A Script parameter shadows the global with the same spelling.
        bool shadowed = analysis.Declarations.GetValueOrDefault(call.Owner)?.Any(p => p.Name.Equals(raw, StringComparison.OrdinalIgnoreCase)) == true;
        if (!shadowed && bindings.TryGetValue(raw, out var binding))
            return raw + " → " + Object(binding.Id) + " [WorldScript, строка " + binding.Line + "; исходная привязка]";
        if (!shadowed && assigned.Contains(raw) && analysis.Globals.GetValueOrDefault(raw) == "object")
            return raw + " [однозначная прямая привязка к объекту не установлена]";
        return raw;
    }

    string StateTarget(Call call)
    {
        var context = snapshot == null ? null : QuestStateReference.Resolve(analysis, call, snapshot);
        string key = analysis.Slice(call.Arguments[1]).Trim();
        if (context == null) return "переменной " + key;
        var warnings = (context.FocusText ?? "").Split('\n').Where(t =>
            t.StartsWith("Подпункт #subobj ", StringComparison.Ordinal) || t.StartsWith("В тексте несколько #subobj ", StringComparison.Ordinal) ||
            t.StartsWith("Текст QUEST ", StringComparison.Ordinal) || t.StartsWith("Квест не найден ", StringComparison.Ordinal) ||
            t.StartsWith("Несколько записей квеста ", StringComparison.Ordinal));
        return context.Title.Replace("\n", " · ") +
            " [текущий снимок БД / связанный текст QUEST; ключ " + key + "]" +
            (snapshot!.Modified ? " [есть несохранённые изменения БД]" : "") +
            (context.FocusText?.Contains("[Текст не сохранён]", StringComparison.Ordinal) == true ? " [текст не сохранён]" : "") +
            string.Concat(warnings.Select(t => " [" + t.Trim() + "]"));
    }

    // The same short sentence is used everywhere; provenance belongs to its tooltip.
    public string Brief(Call call, bool condition = false) => Explain(call, condition);
    public string Tooltip(Call call, bool condition = false) => Detail(call, condition) + "\n\nКод · строка " + call.Line + ": " + analysis.Text[call.Start..call.End];

    static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    Call? Nested(Range range)
    {
        int start = range.Start;
        while (start < range.End && char.IsWhiteSpace(analysis.Text[start])) start++;
        return calls.TryGetValue(start, out var call) && call.End <= range.End && analysis.Text[call.End..range.End].Trim().Length == 0 ? call : null;
    }
    string ShortArgument(Call call, int index, int depth)
    {
        var nested = Nested(call.Arguments[index]);
        if (nested != null) return Explain(nested, false, depth + 1);
        string raw = analysis.Slice(call.Arguments[index]).Trim();
        if (raw.Equals("this", StringComparison.OrdinalIgnoreCase)) return "этот объект";
        bool shadowed = analysis.Declarations.GetValueOrDefault(call.Owner)?.Any(p => p.Name.Equals(raw, StringComparison.OrdinalIgnoreCase)) == true;
        if (!shadowed && bindings.TryGetValue(raw, out var binding)) return ObjectName(binding.Id, raw);
        return OneLine(QuestAtlas.Literal(analysis, call, index) ?? raw);
    }
    string ObjectName(uint id, string? fallback = null) => objects.TryGetValue(id, out var matches) && matches.Length == 1 && matches[0].Name.Length > 0
        ? OneLine(matches[0].Name) : fallback ?? "объект " + id;

    string? QuestTarget(Call call)
    {
        string? key = QuestAtlas.Literal(analysis, call, 1);
        if (key == null || QuestAtlas.QuestRoot(key) is not { } root) return null;
        var parts = key.Split('.');
        var record = snapshot == null ? null : QuestAtlas.QuestRecord(root, snapshot);
        var resources = record?.Texts.Where(t => t.ResourceKey.Equals("quest " + parts[2], StringComparison.OrdinalIgnoreCase)).ToArray();
        var resource = resources?.Length == 1 ? resources[0] : null;
        string title = "«" + OneLine(resource?.Title ?? root) + "»";
        if (parts.Length == 4)
        {
            title += ": подпункт " + parts[3];
            var sections = Regex.Matches(resource?.Description ?? "", @"(?im)^[\t ]*#subobj[\t ]+(\S+)[\t ]*\r?$").Cast<Match>()
                .Where(m => m.Groups[1].Value == parts[3]).ToArray();
            if (sections.Length == 1)
            {
                string tail = resource!.Description[(sections[0].Index + sections[0].Length)..].TrimStart('\r', '\n');
                string name = tail.Split('\r', '\n')[0].Trim();
                if (name.Length > 0 && !name.StartsWith('#')) title += " «" + OneLine(name) + "»";
            }
        }
        return title;
    }
    string Player(Call call, int depth) => analysis.Slice(call.Arguments[0]).Trim() == "0" ? "" : " для игрока " + ShortArgument(call, 0, depth);
    string Variable(Call call, int depth) => "переменная «" + ShortArgument(call, 1, depth) + "»";

    public string Explain(Call call, bool condition = false, int depth = 0)
    {
        if (depth > 16) return Unknown(call);
        string A(int n) => ShortArgument(call, n, depth);
        var name = call.Name.ToLowerInvariant(); int count = call.Arguments.Count;
        if (count == 2 && name is "isgreater" or "isless" or "isequal" or "isequalstring")
        {
            var left = Nested(call.Arguments[0]);
            string value = A(1);
            if (left?.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase) == true && left.Arguments.Count == 2 && QuestTarget(left) is { } target)
            {
                string? state = (name, value) switch
                {
                    ("isless", "2") => "ещё не выполнен", ("isequal", "0") => "ещё не выдан",
                    ("isequal", "1") => "выдан, но не выполнен", ("isequal", "2") => "выполнен", ("isequal", "3") => "провален",
                    _ => null
                };
                return (state == null ? "состояние " + target + Comparison(name) + value : target + " " + state) + Player(left, depth);
            }
            if (left?.Name.Equals("DistanceUnitPoint", StringComparison.OrdinalIgnoreCase) == true && left.Arguments.Count == 3 && name is "isless" or "isgreater")
                return ShortArgument(left, 0, depth) + (name == "isless" ? " ближе " : " дальше ") + value + " к точке (" +
                    ShortArgument(left, 1, depth) + ", " + ShortArgument(left, 2, depth) + ")";
            if (left?.Name.Equals("GetLeverState", StringComparison.OrdinalIgnoreCase) == true && left.Arguments.Count == 1 && name == "isequal")
                return "рычаг " + ShortArgument(left, 0, depth) + " в положении " + value;
            return A(0) + Comparison(name) + value;
        }
        if (count == 1 && name == "not")
        {
            var nested = Nested(call.Arguments[0]);
            return nested?.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase) == true && nested.Arguments.Count == 2 && QuestTarget(nested) is { } target
                ? target + " ещё не выдан" + Player(nested, depth) : A(0) + " равно нулю";
        }
        if (count == 1 && name == "getobjectbyid") return TryObjectId(call, out uint id) ? ObjectName(id) : "объект " + A(0);
        if (count == 1 && name == "isdead") return A(0) + " мёртв";
        if (count == 1 && name == "getleverstate") return "положение рычага " + A(0);
        if (count == 3 && name == "distanceunitpoint") return "расстояние от " + A(0) + " до точки (" + A(1) + ", " + A(2) + ")";
        if (count == 2 && name == "gsgetvar") return QuestTarget(call) is { } readTarget
            ? (condition ? readTarget + " уже выдан" : "состояние " + readTarget) + Player(call, depth)
            : Variable(call, depth) + Player(call, depth) + (condition ? " ненулевая" : "");
        if (count == 3 && name is "gssetvar" or "gssetvarmax")
        {
            if (QuestTarget(call) is { } target)
            {
                string state = A(2) switch { "0" when name == "gssetvar" => "скрыт (сброшен)", "1" => "выдан", "2" => "выполнен", "3" => "провален",
                    _ => "состояние " + (name == "gssetvarmax" ? "не ниже " : "") + A(2) };
                return target + " — " + state + Player(call, depth);
            }
            return Variable(call, depth) + (name == "gssetvarmax" ? " — не ниже " : " = ") + A(2) + Player(call, depth);
        }
        if (count == 2 && name == "gsdelvar") return (QuestTarget(call) ?? Variable(call, depth)) + " — скрыт (сброшен)" + Player(call, depth);
        if (count == 0 && name == "killscript") return "этот скрипт больше не проверяется";
        if (count == 0 && name == "getworldtime") return "текущее время игрового мира";
        if (count == 1 && name == "sleep") return "подождать " + A(0);
        if (count == 2 && name is "giveitem" or "givequestitem") return "выдать игроку" + (analysis.Slice(call.Arguments[0]).Trim() == "0" ? "" : " " + A(0)) + " " + A(1);
        if (count == 2 && name == "sendstringevent" && QuestAtlas.Literal(analysis, call, 1) is { } message && message.StartsWith("#output ", StringComparison.OrdinalIgnoreCase))
        {
            var texts = snapshot?.Records.SelectMany(r => r.Texts).Where(t => t.ResourceKey.Equals(message, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
            string text = texts?.Length == 1 ? OneLine(texts[0].Title.Length > 0 ? texts[0].Title : texts[0].Description) : message;
            return "сообщение игроку" + (analysis.Slice(call.Arguments[0]).Trim() == "0" ? "" : " " + A(0)) + ": " + text;
        }
        if (count == 2 && name == "questcomplete")
        {
            var record = snapshot == null ? null : QuestAtlas.QuestRecord(QuestAtlas.Literal(analysis, call, 1) ?? "", snapshot);
            var texts = record?.Texts.Where(t => t.ResourceKey.Equals("quest " + record.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return "«" + OneLine(texts?.Length == 1 ? texts[0].Title : A(1)) + "» — завершён" + Player(call, depth);
        }
        if (analysis.Blocks.Any(b => b.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase)))
            return "запустить этап " + call.Name;
        if (!help.Entries.TryGetValue(call.Name, out var entry)) return Unknown(call);
        string purpose = OneLine(entry.Purpose.Split(new[] { ". ", ".\n" }, StringSplitOptions.None)[0]).TrimEnd('.');
        if (purpose.StartsWith("Возвращает ", StringComparison.Ordinal)) purpose = purpose[11..];
        if (purpose.Length > 80) purpose = purpose[..77] + "…";
        return purpose + (count == 0 ? "" : ": " + string.Join(", ", Enumerable.Range(0, count).Select(n =>
            (n < entry.Arguments.Length ? entry.Arguments[n] + " " : "") + A(n)))) + (condition ? " — ненулевое значение" : "");
    }
    static string Comparison(string name) => name == "isgreater" ? " больше " : name == "isless" ? " меньше " : " равно ";

    public static string PointTitle(QuestPoint point)
    {
        string role = point.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase) ? "завершает квест" :
            !point.Write ? "проверяет состояние" : point.WrittenValue switch
            {
                1 => point.Key.Split('.').Length == 4 ? "выдаёт подпункт" : "выдаёт квест",
                2 => point.Key.Split('.').Length == 4 ? "выполняет подпункт" : "выполняет квест",
                3 => point.Key.Split('.').Length == 4 ? "проваливает подпункт" : "проваливает квест",
                _ => "меняет состояние"
            };
        return Path.GetFileNameWithoutExtension(point.Map) + " · стр. " + point.Line + " · " + point.Script + " — " + role;
    }

    string Unknown(Call call) => "не объяснено: " + OneLine(analysis.Text[call.Start..call.End]);

    string Detail(Call call, bool condition = false, int depth = 0)
    {
        string A(int n) => Argument(call, n, depth);
        var name = call.Name.ToLowerInvariant(); int count = call.Arguments.Count;
        string text;
        if (depth > 16) return Unknown(call);
        if (count == 2 && name is "isgreater" or "isless" or "isequal" or "isequalstring")
            text = A(0) + (name == "isgreater" ? " больше " : name == "isless" ? " меньше " : " равно ") + A(1);
        else if (count == 1 && name == "not") text = A(0) + " равно нулю";
        else if (count == 1 && name == "getobjectbyid") text = TryObjectId(call, out uint id) ? Object(id) : "Объект по вычисляемому ID " + A(0) + "; имя не установлено";
        else if (count == 1 && name == "isdead") text = "Существующий объект " + A(0) + " мёртв";
        else if (count == 1 && name == "getleverstate") text = "состояние рычага " + A(0);
        else if (count == 2 && name == "gsgetvar") text = "состояние " + StateTarget(call) + " для контекста " + A(0);
        else if (count == 3 && name is "gssetvar" or "gssetvarmax")
        {
            string state = snapshot == null ? "" : QuestStateReference.Resolve(analysis, call, snapshot) == null ? "" : A(2) switch
            { "0" => " (неизвестен / скрыт)", "1" => " (доступен)", "2" => " (выполнен)", "3" => " (провален)", _ => "" };
            text = name == "gssetvarmax" ? "Повысить состояние " + StateTarget(call) + " минимум до " + A(2) + state +
                "; более высокое значение не понижается" : "Установить состояние " + StateTarget(call) + " = " + A(2) + state + "; заменить текущее";
            text += "; контекст " + A(0);
        }
        else if (count == 0 && name == "killscript") text = "Снять этот экземпляр с дальнейшего обслуживания. Уже начатые действия продолжатся; следующие верхнеуровневые проверки экземпляра прекращаются";
        else if (count == 0 && name == "getworldtime") text = "текущее время игрового мира";
        else if (count == 1 && name == "sleep") text = "Подождать " + A(0) + " игровых сегментов (не секунд); другие скрипты продолжают работать";
        else if (count == 1 && name == "partydisguisematches") text = "Одежда всех живых участников партии игрока 0 соответствует профилю " + A(0) + ". У каждого достаточно разрешённой брони и нет посторонней брони";
        else if (count == 2 && name == "questcomplete")
        {
            var context = snapshot == null ? null : DatabaseReferences.Resolve(analysis, call.Start, snapshot);
            var record = context?.Records.Length == 1 ? context.Records[0] : null;
            var titles = record?.Texts.Where(t => t.ResourceKey.Equals("quest " + record.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            text = "Завершить квест " + (titles?.Length == 1 ? "«" + titles[0].Title + "» [снимок БД / текст QUEST; " + A(1) + "]" : A(1)) + " для игрока " + A(0);
        }
        else if (analysis.Blocks.Any(b => b.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase)))
            text = "Запустить экземпляр этапа «" + call.Name + "», не ожидая завершения. Аргументы: " + string.Join(", ", Enumerable.Range(0, count).Select(A));
        else if (help.Entries.TryGetValue(call.Name, out var entry))
            text = entry.Purpose + string.Concat(Enumerable.Range(0, count).Select(n => "\n• " + (n < entry.Arguments.Length ? entry.Arguments[n] : "аргумент " + (n + 1)) + ": " + A(n)));
        else return Unknown(call);
        return condition && name is not ("isdead" or "isgreater" or "isless" or "isequal" or "isequalstring" or "not" or "partydisguisematches")
            ? text + " — значение ненулевое (любое кроме 0)" : text;
    }
}
