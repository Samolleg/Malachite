using System.IO;
using System.Text;

namespace QuestStudio;

public sealed record HelpEntry(string Purpose, string[] Arguments);
public sealed record HoverHelp(string Title, string Kind, string Body, string Source)
{
    public string PlainText => $"{Title}\n{Kind}\n\n{Body}\n\n{Source}";
}

// A separate editorial catalogue: parser signatures stay independent of explanations.
public sealed partial class HelpService
{
    public Dictionary<string, HelpEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    readonly CommandRegistry registry;
    public HelpService(CommandRegistry registry)
    {
        this.registry = registry;
        foreach (var resource in new[] { "QuestStudio.Data.help.tsv", "QuestStudio.Data.addon-help.tsv" })
        {
        using var input = typeof(HelpService).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Нет справочника объяснений.");
        using var reader = new StreamReader(input, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split('|');
            if (fields.Length != 3) throw new InvalidDataException("Некорректная строка справочника: " + line);
            Entries[fields[0]] = new(fields[1], fields[2].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        }
        foreach (var c in registry.Commands.Values)
            if (!Entries.TryGetValue(c.Name, out var entry) || entry.Arguments.Length != c.Parameters.Length || entry.Purpose.Length < 20)
                throw new InvalidDataException("Неполное описание/параметры: " + c.Name);
    }
    public static string TypeName(string type) => type switch { "float" => "число", "object" => "ссылка на объект", "group" => "группа объектов", "string" => "текст", "void" => "действие без возвращаемого значения", _ => type };
    public HoverHelp Command(string name, Analysis? local = null, Call? call = null)
    {
        registry.Commands.TryGetValue(name, out var command);
        Entries.TryGetValue(name, out var entry);
        var body = new StringBuilder(entry?.Purpose ?? "Этот вызов не описан в штатном справочнике и не имеет найденного тела Script. Он может быть командой аддона или ошибкой имени. Его смысл нельзя достоверно восстановить только по названию.");
        if (command != null)
        {
            body.Append("\n\n").Append(command.Signature);
            body.Append("\nРезультат: ").Append(TypeName(command.ReturnType)).Append('.');
            if (command.Parameters.Length == 0) body.Append(" Аргументов нет.");
            for (int i = 0; i < Math.Max(command.Parameters.Length, call?.Arguments.Count ?? 0); i++)
            {
                if (command.Parameters.Length == 0) break;
                var p = command.Argument(i);
                body.Append($"\n{i + 1}. {entry!.Arguments[Math.Min(i, entry.Arguments.Length - 1)]} — {TypeName(p.Type)}");
                if (command.Arities != null && i >= command.Arities.Min()) body.Append("; необязательная группа аргументов");
                if (command.Variadic && i >= command.Parameters.Length - 1) body.Append("; можно повторять");
                if (p.Global) body.Append(", заранее объявленная глобальная переменная");
                if (call != null && local != null && i < call.Arguments.Count) body.Append("; здесь: ").Append(Compact(local.Slice(call.Arguments[i]), 130));
            }
        }
        else if (call != null && local != null)
        {
            body.Append("\n\nПереданные значения:");
            for (int i = 0; i < Math.Min(10, call.Arguments.Count); i++) body.Append($"\n{i + 1}. {(entry != null && i < entry.Arguments.Length ? entry.Arguments[i] + ": " : "")}{Compact(local.Slice(call.Arguments[i]), 110)}");
            if (call.Arguments.Count == 0) body.Append(" нет");
        }
        string provenance = command?.Native == true ? "Сигнатура: штатный реестр. Объяснение: справочная реализация движка." : command != null ? "Источник: обработчик нашего аддона, " + command.Note + ". Профиль Release JamaisVu; исполнение мира не симулируется." : "Семантика неизвестна. Показаны только факты из открытого кода.";
        string kind = command?.DebugOnly == true ? "Только Debug — недоступно в Release" : command?.Internal == true ? "Служебный переход if/else" : command?.OverridesNative == true ? "Штатная команда, расширенная аддоном" : command?.Native == true ? "Функция / команда движка" : command != null ? "Известная команда аддона" : "Расширение или неизвестный вызов";
        return new(name, kind, body.ToString(), provenance);
    }
    public HoverHelp Symbol(Analysis current, string name, Analysis? local = null, int offset = -1, string? scope = null)
    {
        var parameterHelp = ParameterHelp(current, name, local ?? current, offset, scope);
        if (parameterHelp != null) return parameterHelp;
        var block = current.Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (block != null) return WithCallArguments(Script(current, block), current, local ?? current, name, offset);
        if (current.Declarations.TryGetValue(name, out var parameters))
            return WithCallArguments(new(name, "Объявленный пользовательский Script", "Параметры: " + Parameters(parameters) + "\n" + CallbackSummary(name, parameters) + "\nТело Script в открытом документе не найдено. Действия отсутствующего тела неизвестны.", "Источник: DeclareScript в открытом документе."), current, local ?? current, name, offset);
        if (Grammar.TryGetValue(name, out var grammar)) return new(name, "Конструкция MOB", grammar, "Источник: проектный гайд языка MOB.");
        var input = local ?? current;
        var token = input.Tokens.FirstOrDefault(t => t.Start == offset);
        var call = input.Calls.FirstOrDefault(c => c.Start == offset && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (registry.Commands.ContainsKey(name) || Entries.ContainsKey(name) || call != null) return Command(name, input, call);
        if (current.Globals.TryGetValue(name, out var type)) return new(name, "Глобальная переменная", $"Тип: {TypeName(type)}. Объявление делает имя доступным скриптам, но само по себе не создаёт объект и не заполняет произвольную группу.\n" + (name.Equals("Heroes", StringComparison.OrdinalIgnoreCase) ? "Heroes — особая группа: движок добавляет в неё размещённых персонажей партии; сценарий может менять состав позже." : "Значение определяется присваиваниями и командами в сценарии."), "Источник: GlobalVars открытого документа.");
        return new(name, "Имя в сценарии", "Это имя не найдено среди встроенных команд, тел Script и глобальных переменных. Оно может быть параметром текущего Script. Тип и значение определяются его DeclareScript и конкретным вызовом.", "Неизвестный смысл не подменяется догадкой.");
    }
    static readonly Dictionary<string, string> Grammar = new(StringComparer.OrdinalIgnoreCase)
    {
        ["if"] = "Проверяет условия внутри следующих скобок. Все условия должны быть ненулевыми; пустой список означает безусловную ветку. В обычном активном Script проверка повторяется при обслуживании сценария.",
        ["then"] = "Действия этой ветки выполняются, когда условия if истинны. Команды идут по порядку; Sleep может приостановить последовательность.",
        ["else"] = "Ветка расширенного синтаксиса аддона. Если достигнутый if ложен, выполняется эта ветка. В пределах одной такой проверки выбирается одна ветка; изменение флага в then не запускает else следом.",
        ["Script"] = "Определяет тело именованного сценария. Само определение не запускает его. Запуск производится вызовом имени из WorldScript, другого Script или обработчика движка.",
        ["DeclareScript"] = "Объявляет имя Script и типы его параметров. Действия находятся в отдельном блоке Script с тем же именем.",
        ["WorldScript"] = "Содержит стартовую последовательность карты. Здесь можно присвоить переменные и запустить нужные Script. Вызов Script запускает экземпляр, а не ждёт завершения всего квеста.",
        ["GlobalVars"] = "Объявляет глобальные переменные сценария: object, group, float, string. Значения и привязки задаются отдельно.",
        ["object"] = "Тип ссылки на игровой объект. Это не числовой ID: ссылка получается, например, из GetObjectByID(\"334\").",
        ["group"] = "Тип группы ссылок на объекты. Группы можно объединять, пересекать и перебирать.",
        ["float"] = "Числовой тип MOB. Логические условия также используют числа: ноль — ложь, ненулевое значение — истина.",
        ["string"] = "Строковый тип MOB. Литерал записывается в двойных кавычках; игровая кодировка обычно Windows-1251."
    };
    static string Parameters(Parameter[] parameters) => parameters.Length == 0 ? "нет" : string.Join(", ", parameters.Select(p => p.Name + " — " + TypeName(p.Type)));
    public HoverHelp Script(Analysis analysis, ScriptBlock block)
    {
        var lines = new List<string>();
        if (analysis.Declarations.TryGetValue(block.Name, out var p)) lines.Add("Параметры: " + Parameters(p) + ". " + CallbackSummary(block.Name, p));
        lines.Add(block.World ? "Стартовые действия карты:" : "Что делает тело скрипта:");
        var tokens = analysis.Tokens;
        int first = tokens.FindIndex(t => t.Start >= block.Body.Start);
        int end = tokens.FindIndex(Math.Max(0, first), t => t.Start >= block.Body.End);
        if (end < 0) end = tokens.Count;
        if (first >= 0) ExplainSequence(analysis, first, end, lines, 0);
        if (lines.Count <= 2) lines.Add("Тело не содержит распознанных команд.");
        var incoming = analysis.Blocks.Where(b => b.Calls.Any(c => c.Name.Equals(block.Name, StringComparison.OrdinalIgnoreCase))).Select(b => b.Name).Distinct().ToArray();
        if (!block.World) lines.Add(incoming.Length == 0 ? "Явный запуск в этом документе не найден: возможен вызов движком или другим файлом." : "Кто запускает: " + string.Join(", ", incoming.Take(4)) + (incoming.Length > 4 ? $"; ещё {incoming.Length - 4}" : "") + ".");
        return new(block.Name, block.World ? "Старт карты · WorldScript" : "Пользовательский Script · строка " + block.Line, string.Join("\n", lines), "По текущему коду и справочному планировщику: KillScript оставляет действия начатой ветки, но прекращает дальнейшие верхнеуровневые проверки экземпляра. Вызов Script не ждёт его завершения. Игровая достижимость не проверена.");
    }
    void ExplainSequence(Analysis a, int begin, int end, List<string> lines, int depth)
    {
        if (depth > 3) { lines.Add("  … вложенные действия: подробности в коде."); return; }
        var t = a.Tokens;
        string pad = new(' ', depth * 2);
        int independentChecks = 0;
        for (int i = begin; i < end;)
        {
            if (lines.Count >= 20) { if (!lines[^1].StartsWith("…")) lines.Add("… продолжение и остальные действия смотрите в коде."); return; }
            if (t[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase) && i + 1 < end && a.Pairs.TryGetValue(i + 1, out int c) && c + 2 < end && t[c + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) && a.Pairs.TryGetValue(c + 2, out int actionsEnd))
            {
                bool topRule = depth == 0 && a.BlockAt(t[i].Start)?.World == false;
                bool hasElse = actionsEnd + 1 < end && t[actionsEnd + 1].Value.Equals("else", StringComparison.OrdinalIgnoreCase);
                if (topRule && independentChecks++ > 0) lines.Add("Следующая проверка, если экземпляр ещё не снят KillScript:");
                if (hasElse) lines.Add(pad + "При достижении выбрать одну ветку; после Sleep выбор не повторяется:");
                else if (topRule) lines.Add(pad + "Правило ожидания: условие проверяется при обслуживании Script.");
                var conditions = new List<string>();
                for (int q = i + 2; q < c;)
                {
                    var conditionCall = a.Calls.FirstOrDefault(x => x.Start == t[q].Start);
                    if (conditionCall != null) { conditions.Add(Expression(a, new(conditionCall.Start, conditionCall.End - conditionCall.Start), 0)); q = a.Tokens.FindIndex(q, x => x.Start >= conditionCall.End); if (q < 0) break; }
                    else { conditions.Add(t[q].Value); q++; }
                }
                lines.Add(pad + (conditions.Count == 0 ? "Без условия:" : "Если " + Compact(string.Join(" И ", conditions), 330) + ":"));
                ExplainSequence(a, c + 3, actionsEnd, lines, depth + 1);
                i = actionsEnd + 1;
                if (i + 1 < end && t[i].Value.Equals("else", StringComparison.OrdinalIgnoreCase) && a.Pairs.TryGetValue(i + 1, out int elseEnd))
                { lines.Add(pad + "Иначе:"); ExplainSequence(a, i + 2, elseEnd, lines, depth + 1); i = elseEnd + 1; }
                continue;
            }
            var call = a.Calls.FirstOrDefault(c => c.Start == t[i].Start);
            if (call != null)
            {
                lines.Add(pad + "• " + Action(a, call));
                int next = t.FindIndex(i, x => x.Start >= call.End); i = next < 0 ? end : next;
                if (i < end && t[i].Value == "(" && a.Pairs.TryGetValue(i, out int bodyEnd)) { ExplainSequence(a, i + 1, bodyEnd, lines, depth + 1); i = bodyEnd + 1; }
                continue;
            }
            if (i + 1 < end && t[i + 1].Value == "=") lines.Add(pad + "• Присвоить переменной " + t[i].Value + " значение следующего выражения.");
            i++;
        }
    }
    string Action(Analysis a, Call call)
    {
        string Arg(int n) => n < call.Arguments.Count ? Compact(a.Slice(call.Arguments[n]), 70) : "?";
        if (a.Declarations.ContainsKey(call.Name) || a.Blocks.Any(b => b.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase))) return "Запустить Script " + call.Name + "(" + string.Join(", ", call.Arguments.Select(r => Compact(a.Slice(r), 50))) + ").";
        return call.Name.ToLowerInvariant() switch
        {
            "killscript" => "Снять экземпляр с обслуживания; действия этой ветки продолжатся.",
            "gssetvar" => $"Записать {Arg(2)} в GS {Arg(1)} (контекст {Arg(0)}).",
            "gssetvarmax" => $"Повысить GS {Arg(1)} до {Arg(2)}, если текущее значение меньше.",
            "gsdelvar" => $"Удалить GS {Arg(1)} (контекст {Arg(0)}).",
            "sleep" => $"Подождать {Arg(0)} игровых сегментов.",
            "sleepuntil" => "Ждать, пока " + (call.Arguments.Count > 0 ? Expression(a, call.Arguments[0], 0) : "?") + ".",
            "sleepuntilidle" => $"Ждать бездействия юнита {Arg(0)}.",
            "setcp" or "setcpfast" => $"Перенести {Arg(0)} в X={Arg(1)}, Y={Arg(2)}, Z={Arg(3)}.",
            "movetopoint" => $"Приказать {Arg(0)} идти в X={Arg(1)}, Y={Arg(2)}.",
            "rotateto" => $"Повернуть {Arg(0)} к точке X={Arg(1)}, Y={Arg(2)}.",
            "hideobject" => $"Изменить скрытие {Arg(0)}: {Arg(1)} (0 — показать, ненулевое — скрыть).",
            "showmessage" or "consolestring" => (call.Name.Equals("ShowMessage", StringComparison.OrdinalIgnoreCase) ? "Показать сообщение " : "Напечатать в консоли соединённые строки ") + string.Join(" + ", Enumerable.Range(0, call.Arguments.Count).Select(Arg)) + ".",
            "playanimation" => $"Запустить у {Arg(0)} анимацию {Arg(1)}.",
            _ => Entries.TryGetValue(call.Name, out var entry) ? call.Name + ": " + Compact(entry.Purpose.Split(". ")[0], 180) + (call.Arguments.Count > 0 ? " [" + Compact(string.Join(", ", call.Arguments.Select(r => a.Slice(r))), 100) + "]" : "") : call.Name + " — неизвестное расширение; назначение требует справки."
        };
    }
    string Expression(Analysis a, Range range, int depth)
    {
        if (depth > 5) return Compact(a.Slice(range), 90);
        var c = a.Calls.FirstOrDefault(c => c.Start == range.Start && c.End == range.End);
        if (c == null) return Compact(a.Slice(range), 120);
        string P(int n) => n < c.Arguments.Count ? Expression(a, c.Arguments[n], depth + 1) : "?";
        return c.Name.ToLowerInvariant() switch
        {
            "isequal" or "isequalstring" => $"({P(0)} равно {P(1)})",
            "isless" => $"({P(0)} меньше {P(1)})",
            "isgreater" => $"({P(0)} больше {P(1)})",
            "not" => $"НЕ ({P(0)})", "add" => "(" + string.Join(" + ", Enumerable.Range(0, c.Arguments.Count).Select(P)) + ")", "sub" => $"({P(0)} − {P(1)})", "mul" => "(" + string.Join(" × ", Enumerable.Range(0, c.Arguments.Count).Select(P)) + ")", "div" => $"({P(0)} / {P(1)})",
            "partydisguisematches" => $"одежда всех живых участников партии игрока 0 соответствует профилю {P(0)}",
            "gsgetplayervar" => $"переменная партии {P(1)} у игрока {P(0)}",
            "gsgetvar" => $"GS {P(1)} у {P(0)}", "getworldtime" => "время игрового мира", "isnight" => "степень ночного освещения",
            "isalive" => $"{P(0)} жив", "isdead" => $"{P(0)} мёртв", "getobjectbyid" => $"объект с ID {P(0)}", "getobjectbyname" => $"объект {P(0)}",
            "getx" => $"X объекта {P(0)}", "gety" => $"Y объекта {P(0)}", "getz" => $"Z объекта {P(0)}", "getleverstate" => $"состояние рычага {P(0)}",
            "hp" => $"здоровье {P(0)}", "mana" => $"мана {P(0)}", "groupsize" => $"размер группы {P(0)}", "groupcross" => $"пересечение {P(0)} и {P(1)}",
            "any" => $"хотя бы для одного {P(0)} из {P(1)}: {P(2)}", "every" => $"для всех {P(0)} из {P(1)}: {P(2)}",
            "distanceunitunit" => $"расстояние между {P(0)} и {P(1)}", "distanceunitpoint" => $"расстояние от {P(0)} до ({P(1)}, {P(2)})",
            _ => Compact(a.Slice(range), 170)
        };
    }
    public static string Compact(string text, int max) { string value = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); return value.Length <= max ? value : value[..max] + "…"; }
}
