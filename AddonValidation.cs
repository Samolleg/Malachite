using System.Globalization;

namespace QuestStudio;

public static class AddonValidation
{
    public static void Check(Analysis a, CommandRegistry registry)
    {
        var tokens = a.Tokens;
        var synchronous = new List<Range>();
        foreach (var call in a.Calls)
        {
            if (!registry.Commands.TryGetValue(call.Name, out var command)) continue;
            if (command.Name is "DistancePointPoint" or "CheckObjectFlag" or "CheckObjectFlag2" or "CastSomeSpellTarget")
                a.Add("warning", "HANDLER_CONTRACT", $"{call.Name}: в исходнике аддона обнаружено несоответствие регистрации и чтения аргументов обработчиком. Подробности — в подсказке команды.", call.Start);
            int next = tokens.FindIndex(t => t.Start >= call.End);
            if (next >= 0 && tokens[next].Value == "(" && a.Pairs.TryGetValue(next, out int end))
            {
                if (!command.Trailing) a.Add("error", "UNEXPECTED_BODY", $"{call.Name} не принимает дополнительное тело команд.", tokens[next].Start);
                else synchronous.Add(new(tokens[next].End, tokens[end].Start - tokens[next].End));
            }
            if (command.Internal && command.Accepts(call.Arguments.Count))
            {
                var range = call.Arguments[^1];
                if (double.TryParse(a.Slice(range).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                    (!double.IsFinite(value) || value < 0 || value > 16777215 || value != Math.Floor(value)))
                    a.Add("error", "BRANCH_OFFSET", "Смещение служебного перехода должно быть целым от 0 до 16777215.", range.Start);
            }
        }
        foreach (var call in a.Calls.Where(c => c.Name.Equals("CreateVoiceFXWait", StringComparison.OrdinalIgnoreCase)))
            if (synchronous.Any(r => call.Start >= r.Start && call.End <= r.End))
                a.Add("error", "VOICE_SYNC_BODY", "CreateVoiceFXWait не поддерживается внутри синхронного тела For/ForIf или Debug-команды цикла.", call.Start);
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != "id" || a.BlockAt(token.Start) == null) continue;
            if (token.Value.Equals("if", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count && a.Pairs.TryGetValue(i + 1, out int endCondition))
            {
                for (int j = i + 2; j < endCondition;)
                {
                    int end = j + 1;
                    if (end < endCondition && tokens[end].Value == "(" && a.Pairs.TryGetValue(end, out int expressionEnd)) end = expressionEnd + 1;
                    var range = new Range(tokens[j].Start, tokens[end - 1].End - tokens[j].Start);
                    var type = ScriptAnalyzer.InferType(a, registry, range, a.BlockAt(token.Start)!.Name);
                    if (type != null && type != "float") a.Add("error", "CONDITION_TYPE", "Условие if должно возвращать число, а не " + type + ".", range.Start);
                    j = end;
                }
            }
            if (i + 2 < tokens.Count && tokens[i + 1].Value == "=")
            {
                int end = i + 3;
                if (end < tokens.Count && tokens[end].Value == "(" && a.Pairs.TryGetValue(end, out int expressionEnd)) end = expressionEnd + 1;
                var owner = a.BlockAt(token.Start)!.Name;
                var target = ScriptAnalyzer.InferType(a, registry, new(token.Start, token.Length), owner);
                var value = ScriptAnalyzer.InferType(a, registry, new(tokens[i + 2].Start, tokens[end - 1].End - tokens[i + 2].Start), owner);
                if (target == null) a.Add("warning", "ASSIGN_TARGET", "Переменная назначения не объявлена в открытом документе: " + token.Value, token.Start);
                else if (value != null && target != value) a.Add("error", "ASSIGN_TYPE", $"Присваивание {token.Value}: ожидается {target}, получен {value}.", token.Start);
            }
        }
    }
}
