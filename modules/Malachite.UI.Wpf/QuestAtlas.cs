using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public sealed record QuestPoint(string Id, string Quest, string Key, string Context, string Map, string? Hash,
    string Script, int Offset, int Line, string Action, string Conditions, bool Write, RecordRef? Record = null)
{
    public double? WrittenValue { get; init; }
    public bool Maximum { get; init; }
    public QuestPredicate? Predicate { get; init; }
    public string? CallTarget { get; init; }
}
public sealed record QuestConnection(string From, string To, string Reason, bool Certain);
public sealed record ScriptConnection(string From, string To, int Offset, int Line);
public sealed record QuestDependency(QuestPoint Check, QuestPoint Action, string Branch, ScriptConnection[] Calls);
public sealed record BriefingUse(string Name, string Map, string Script, int Offset, int Line, string Key, string Context, string Action);
public sealed record QuestMap(string Path, string? Hash, QuestPoint[] Points, ScriptConnection[] Calls, BriefingUse[] Briefings)
{
    public string Text { get; init; } = "";
    public MobObject[] Objects { get; init; } = [];
    public string[] Gaps { get; init; } = [];
    public QuestDependency[] Dependencies { get; init; } = [];
}
public sealed record QuestRoute(QuestPoint[] Points, QuestConnection[] Connections);

public static class QuestAtlas
{
    public static string? Literal(Analysis a, Call c, int index)
    {
        if (index >= c.Arguments.Count) return null;
        var value = a.Slice(c.Arguments[index]).Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' && !value[1..^1].Contains('"') && !value.Contains('\\') ? value[1..^1] : null;
    }
    public static string? QuestKey(string key)
    {
        var parts = key.Split('.');
        return parts.Length is 3 or 4 && parts[0] == "q" && parts.All(p => p.Length > 0 && !p.Any(char.IsWhiteSpace)) ? parts[2] : null;
    }
    // A quest state is identified by its entire root, never by the DB name alone.
    public static string? QuestRoot(string key) => QuestKey(key) != null ? string.Join('.', key.Split('.').Take(3)) : null;
    public static string Part(string key) => key.Split('.') is { Length: 4 } parts ? "Подпункт " + parts[3] : "Сам квест";

    public static RecordDto? QuestRecord(string root, WorkspaceSnapshot snapshot)
    {
        var name = QuestKey(root) ?? root;
        var records = snapshot.Records.Where(r => r.Reference.TableKey == "quests.quests" && r.Name == name).ToArray();
        if (records.Length != 1) return null;
        if (QuestRoot(root) == null) return records[0]; // Explicit QuestComplete DB identifier.
        var zones = records[0].Fields.Where(f => f.FieldId == 3 && f.NestedPath.IsEmpty).ToArray();
        if (zones.Length != 1) return null;
        uint zone;
        if (zones[0].Value.Kind == "Hex")
        {
            var hex = zones[0].Value.ExactValue;
            if (hex.Length != 8 || !hex.All(Uri.IsHexDigit)) return null;
            zone = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(hex));
        }
        else if (!uint.TryParse(zones[0].Value.ExactValue, out zone)) return null;
        // QDB zone 0 is used by movable hunting objectives (vo*). Otherwise
        // reject the wrong zone, e.g. q.gz1g.q1g versus QDB q1g, zone 3.
        var code = root.Split('.')[1];
        var match = System.Text.RegularExpressions.Regex.Match(code, @"^gz([0-9]+)[a-z]+$");
        return zone == 0 || match.Success && uint.TryParse(match.Groups[1].Value, out uint number) && number == zone ? records[0] : null;
    }
    public static QuestMap Read(Analysis a, string path, string? hash)
    {
        List<QuestPoint> points = []; List<BriefingUse> briefings = [];
        var guards = BuildGuards(a);
        string Conditions(Call call) => string.Join("\n", guards.Where(g => call.Start >= g.Start && call.End <= g.End).Select(g => g.Text)) is { Length: > 0 } value ? value : "Без найденной обрамляющей проверки; порядок смотрите в исходном скрипте.";
        foreach (var c in a.Calls)
        {
            bool gs = QuestStateReference.Supports(c.Name);
            string? key = Literal(a, c, 1);
            string? quest = gs && key != null ? QuestRoot(key) : c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase) ? key : null;
            var context = c.Arguments.Count > 0 ? a.Slice(c.Arguments[0]).Trim() : "";
            bool write = !c.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase);
            string action = c.Name + "(" + string.Join(", ", c.Arguments.Select(a.Slice)) + ")";
            if (quest != null || gs && key != null) points.Add(new(path + "@" + c.Start, quest ?? "", gs ? key! : "", context, path, hash, c.Owner,
                c.Start, c.Line, action, Conditions(c), write)
            {
                WrittenValue = gs && write ? (c.Name.Equals("GSDelVar", StringComparison.OrdinalIgnoreCase) ? 0 : c.Arguments.Count == 3 ? QuestPredicate.Number(a.Slice(c.Arguments[2])) : null) : null,
                Maximum = c.Name.Equals("GSSetVarMax", StringComparison.OrdinalIgnoreCase),
                Predicate = !write ? QuestPredicate.Read(a, c) : null
            });
            if (c.Name.Equals("LeaveToZone", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("AddMob", StringComparison.OrdinalIgnoreCase))
                points.Add(new(path + "@" + c.Start, "", "", context, path, hash, c.Owner, c.Start, c.Line, action,
                    Conditions(c) + "\nИмя зоны не приравнивается к имени MOB. Переход по файлу ищите среди использований состояния на других картах.", false));
            if (gs && key != null)
            {
                var parts = key.Split('.');
                if (parts.Length == 3 && parts[0] == "b") briefings.Add(new(parts[2], path, c.Owner, c.Start, c.Line, key, context, action));
            }
        }
        var scriptNames = a.Blocks.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calls = a.Calls.Where(c => scriptNames.Contains(c.Name)).Select(c => new ScriptConnection(c.Owner, c.Name, c.Start, c.Line)).Distinct().ToArray();
        var gaps = a.Calls.Where(c => (QuestStateReference.Supports(c.Name) || c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase)) && Literal(a, c, 1) == null)
            .Select(c => c.Owner + ":" + c.Line + " · вычисляемый ключ " + c.Name + "; связь не определена").ToList();
        gaps.AddRange(a.Issues.Select(i => "Строка " + i.Line + " · " + i.Message));
        var found = points.ToArray();
        return new(path, hash, found, calls, briefings.ToArray()) { Text = a.Text, Gaps = gaps.ToArray(), Dependencies = Dependencies(a, found, calls) };
    }
    static QuestDependency[] Dependencies(Analysis a, QuestPoint[] points, ScriptConnection[] calls)
    {
        var rules = new List<(int Start, int End, int YesStart, int YesEnd, int NoStart, int NoEnd)>();
        var t = a.Tokens;
        for (int i = 0; i + 2 < t.Count; i++)
        {
            if (!t[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase) || t[i + 1].Value != "(" ||
                !a.Pairs.TryGetValue(i + 1, out int end) || end + 2 >= t.Count ||
                !t[end + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) || t[end + 2].Value != "(" ||
                !a.Pairs.TryGetValue(end + 2, out int yes)) continue;
            int noStart = -1, noEnd = -1;
            if (yes + 2 < t.Count && t[yes + 1].Value.Equals("else", StringComparison.OrdinalIgnoreCase) &&
                t[yes + 2].Value == "(" && a.Pairs.TryGetValue(yes + 2, out int no))
            { noStart = t[yes + 2].End; noEnd = t[no].Start; }
            rules.Add((t[i + 1].End, t[end].Start, t[end + 2].End, t[yes].Start, noStart, noEnd));
        }
        bool Guarded(int offset) => rules.Any(r => offset >= r.Start && offset < r.End ||
            offset >= r.YesStart && offset < r.YesEnd || offset >= r.NoStart && offset < r.NoEnd);
        var result = new List<QuestDependency>();
        foreach (var rule in rules)
        foreach (var check in points.Where(p => !p.Write && QuestRoot(p.Key) != null && p.Offset >= rule.Start && p.Offset < rule.End))
        foreach (var branch in new[] { (rule.YesStart, rule.YesEnd, "ТОГДА"), (rule.NoStart, rule.NoEnd, "ИНАЧЕ") })
        {
            bool Inside(int offset) => offset >= branch.Item1 && offset < branch.Item2;
            void Add(QuestPoint action, ScriptConnection[] chain)
            {
                if (action.Write && QuestRoot(action.Key) != null && action.Quest != check.Quest && action.Context == check.Context &&
                    (chain.Length == 0 || QuestPredicate.Number(check.Context) != null))
                    result.Add(new(check, action, branch.Item3, chain));
            }
            foreach (var action in points.Where(p => Inside(p.Offset))) Add(action, []);
            // Only unconditional bodies of directly called Script are followed.
            // A second condition or a cycle is not guessed to be a causal chain.
            void Follow(ScriptConnection link, ScriptConnection[] chain, HashSet<string> seen)
            {
                if (!seen.Add(link.To) || a.Blocks.Count(b => b.Name.Equals(link.To, StringComparison.OrdinalIgnoreCase)) != 1) return;
                var next = chain.Append(link).ToArray();
                foreach (var action in points.Where(p => p.Script.Equals(link.To, StringComparison.OrdinalIgnoreCase) && !Guarded(p.Offset))) Add(action, next);
                foreach (var child in calls.Where(c => c.From.Equals(link.To, StringComparison.OrdinalIgnoreCase) && !Guarded(c.Offset)))
                    Follow(child, next, new(seen, StringComparer.OrdinalIgnoreCase));
            }
            foreach (var link in calls.Where(c => Inside(c.Offset))) Follow(link, [], new(StringComparer.OrdinalIgnoreCase));
        }
        return result.DistinctBy(d => (d.Check.Id, d.Action.Id, d.Branch, string.Join(',', d.Calls.Select(c => c.Offset)))).ToArray();
    }
    static List<(int Start, int End, string Text)> BuildGuards(Analysis a)
    {
        List<(int Start, int End, string Text)> conditions = [];
        for (int i = 0; i + 2 < a.Tokens.Count; i++)
        {
            var t = a.Tokens;
            if (!t[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase) || t[i + 1].Value != "(" || !a.Pairs.TryGetValue(i + 1, out int end)) continue;
            if (end + 2 >= t.Count || !t[end + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) || !a.Pairs.TryGetValue(end + 2, out int yes)) continue;
            string expression = a.Text[t[i + 1].End..t[end].Start].Trim();
            conditions.Add((t[i].Start, t[end].End, "Проверка: " + expression));
            conditions.Add((t[end + 2].End, t[yes].Start, "ТОГДА: " + expression));
            if (yes + 2 < t.Count && t[yes + 1].Value.Equals("else", StringComparison.OrdinalIgnoreCase) && a.Pairs.TryGetValue(yes + 2, out int no))
                conditions.Add((t[yes + 2].End, t[no].Start, "ИНАЧЕ: " + expression));
        }
        return conditions;
    }
    public static QuestRoute Build(string quest, IEnumerable<QuestMap> maps, WorkspaceSnapshot snapshot, bool includeBriefings = false)
    {
        var sources = maps.ToArray(); var points = sources.SelectMany(m => m.Points).Where(p => p.Quest == quest).ToList();
        List<QuestConnection> edges = [];
        foreach (var record in snapshot.Records.Where(r => includeBriefings && r.Reference.TableKey == "quests.briefings"))
        foreach (var field in record.Fields.Where(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList"))
        foreach (var key in field.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(k => QuestRoot(k) == quest))
        {
            string id = "brief:" + record.Reference.RecordId + ":" + field.FieldId + ":" + key;
            string action = field.FieldId == 6 ? "Выдача: повысить состояние минимум до 1" : "Завершение: установить состояние 2";
            var title = record.Texts.Length == 1 ? record.Texts[0].Title : record.Label + " [название текста отсутствует или неоднозначно]";
            points.Add(new(id, quest, key, "контекст брифинга", "БД · брифинги", null, record.Name, 0, 0,
                action + "\n" + title + "\n" + string.Join("\n", record.Texts.Select(t => t.Description)), "Действие после прохождения брифинга (поле QDB " + field.FieldId + ").", true, record.Reference)
                { WrittenValue = field.FieldId == 6 ? 1 : 2, Maximum = field.FieldId == 6 });
            foreach (var use in sources.SelectMany(m => m.Briefings).Where(b => b.Name == record.Name))
            {
                var source = sources.First(m => m.Path == use.Map);
                string useId = use.Map + "@" + use.Offset;
                if (!points.Any(p => p.Id == useId)) points.Add(new(useId, quest, use.Key, use.Context, use.Map, source.Hash, use.Script, use.Offset, use.Line, use.Action, "Брифинг «" + record.Name + "». Использование его состояния не доказывает прохождение.", !use.Action.StartsWith("GSGetVar", StringComparison.OrdinalIgnoreCase)));
                edges.Add(new(useId, id, "Состояние брифинга → запись БД; прохождение зависит от игрока", false));
            }
        }
        foreach (var map in sources)
        {
            var scripts = points.Where(p => p.Map == map.Path).Select(p => p.Script).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var forward = new HashSet<string>(scripts, StringComparer.OrdinalIgnoreCase);
            bool changed;
            do { changed = false; foreach (var link in map.Calls) if (forward.Contains(link.From)) changed |= forward.Add(link.To); } while (changed);
            do { changed = false; foreach (var link in map.Calls) if (scripts.Contains(link.To)) changed |= scripts.Add(link.From); } while (changed);
            scripts.IntersectWith(forward);
            foreach (var link in map.Calls.Where(c => scripts.Contains(c.From) && scripts.Contains(c.To)))
            {
                string id = map.Path + "@call" + link.Offset;
                points.Add(new(id, quest, "", "", map.Path, map.Hash, link.From, link.Offset, link.Line,
                    "Запустить Script «" + link.To + "»", "Прямой вызов в исходнике; условия выполнения смотрите в коде. Запуск не ждёт завершения Script.", false) { CallTarget = link.To });
            }
        }
        // Match only relevant keys / Script targets instead of comparing every point pair.
        var scriptPoints = points.Where(p => p.Record == null).GroupBy(p => p.Map, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToLookup(p => p.Script, StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        foreach (var source in points)
        {
            if (source.CallTarget != null && scriptPoints.TryGetValue(source.Map, out var targets))
                foreach (var target in targets[source.CallTarget])
                    if (source.Id != target.Id)
                        edges.Add(new(source.Id, target.Id, "Этот вызов запускает Script «" + target.Script + "»; его условия проверяются отдельно", true));
        }
        return new(points.DistinctBy(p => p.Id).ToArray(), edges.Distinct().ToArray());
    }
}
