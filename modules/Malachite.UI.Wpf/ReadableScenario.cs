namespace QuestStudio;

public sealed record ScenarioLine(string Text, int Offset, int Depth, bool Caution = false);

// A read-only view of original token ranges, never of lowered/generated jumps.
public sealed class ReadableScenario
{
    readonly Analysis analysis;
    readonly HelpService help;
    readonly List<ScenarioLine> lines = [];
    readonly Dictionary<int, Call> calls;
    ReadableScenario(Analysis analysis, HelpService help)
    {
        this.analysis = analysis; this.help = help;
        calls = analysis.Calls.GroupBy(c => c.Start).ToDictionary(g => g.Key, g => g.First());
    }
    public static IReadOnlyList<ScenarioLine> Describe(Analysis analysis, ScriptBlock block, HelpService help)
    {
        var reader = new ReadableScenario(analysis, help);
        reader.lines.Add(new(block.World ? "При запуске карты" : "Этап «" + block.Name + "» · проверки по порядку", block.Start, 0));
        var start = analysis.Tokens.FindIndex(t => t.Start >= block.Body.Start);
        var end = analysis.Tokens.FindIndex(t => t.Start >= block.Body.End);
        if (start >= 0) reader.Sequence(start, end < 0 ? analysis.Tokens.Count : end, 0);
        if (reader.lines.Count == 1) reader.lines.Add(new("В этом этапе нет действий.", block.Start, 0));
        return reader.lines;
    }
    void Sequence(int start, int end, int depth)
    {
        if (depth > 32) { lines.Add(new("Глубокая вложенность: смотрите исходный код.", analysis.Tokens[start].Start, 32, true)); return; }
        var tokens = analysis.Tokens;
        for (int i = start; i < end;)
        {
            if (lines.Count >= 500) { lines.Add(new("Показаны первые 500 строк описания. Продолжение — в исходном коде.", tokens[i].Start, depth, true)); return; }
            var token = tokens[i];
            if (token.Value.Equals("if", StringComparison.OrdinalIgnoreCase) && i + 1 < end && tokens[i + 1].Value == "(" &&
                analysis.Pairs.TryGetValue(i + 1, out var conditionEnd) && conditionEnd + 2 < end &&
                tokens[conditionEnd + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) && tokens[conditionEnd + 2].Value == "(" &&
                analysis.Pairs.TryGetValue(conditionEnd + 2, out var yesEnd) && yesEnd < end)
            {
                lines.Add(new("ЕСЛИ выполнены все условия (И):", token.Start, depth));
                if (conditionEnd == i + 2) lines.Add(new("Без условий — выполнять при достижении этой проверки.", token.Start, depth + 1));
                else Conditions(i + 2, conditionEnd, depth + 1);
                lines.Add(new("ТОГДА · действия по порядку:", tokens[conditionEnd + 1].Start, depth));
                if (yesEnd == conditionEnd + 3) lines.Add(new("Нет действий.", tokens[yesEnd].Start, depth + 1));
                else Sequence(conditionEnd + 3, yesEnd, depth + 1);
                i = yesEnd + 1;
                if (i + 1 < end && tokens[i].Value.Equals("else", StringComparison.OrdinalIgnoreCase) && tokens[i + 1].Value == "(" &&
                    analysis.Pairs.TryGetValue(i + 1, out var noEnd) && noEnd < end)
                {
                    lines.Add(new("ИНАЧЕ · если условия этой проверки не выполнены:", tokens[i].Start, depth));
                    if (noEnd == i + 2) lines.Add(new("Нет действий.", tokens[i].Start, depth + 1));
                    else Sequence(i + 2, noEnd, depth + 1);
                    i = noEnd + 1;
                }
                continue;
            }
            if (calls.TryGetValue(token.Start, out var call) && call.End <= tokens[end - 1].End)
            {
                lines.Add(new(ExplainCall(call, 0), token.Start, depth, !help.Entries.ContainsKey(call.Name) && !analysis.Blocks.Any(b => b.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase))));
                do { i++; } while (i < end && tokens[i].Start < call.End);
                continue;
            }
            // Preserve unsupported syntax as a visible source fragment, not a guessed action.
            int last = i;
            while (last + 1 < end && tokens[last + 1].Line == token.Line) last++;
            lines.Add(new("Не переведено: " + analysis.Text[token.Start..tokens[last].End], token.Start, depth, true));
            i = last + 1;
        }
    }
    void Conditions(int start, int end, int depth)
    {
        for (int i = start; i < end;)
        {
            var token = analysis.Tokens[i];
            if (calls.TryGetValue(token.Start, out var call))
            {
                lines.Add(new(ExplainCall(call, 0), token.Start, depth));
                do { i++; } while (i < end && analysis.Tokens[i].Start < call.End);
            }
            else { lines.Add(new("Значение должно быть ненулевым: «" + token.Value + "»", token.Start, depth)); i++; }
        }
    }
    string Argument(Call call, int index, int depth)
    {
        var range = call.Arguments[index];
        var nested = analysis.Calls.FirstOrDefault(c => c.Start >= range.Start && c.End <= range.End && analysis.Text[range.Start..c.Start].Trim().Length == 0 && analysis.Text[c.End..range.End].Trim().Length == 0);
        return nested != null && depth < 16 ? "(" + ExplainCall(nested, depth + 1) + ")" : analysis.Slice(range).Trim();
    }
    string ExplainCall(Call call, int depth)
    {
        string A(int n) => Argument(call, n, depth);
        var name = call.Name.ToLowerInvariant();
        int count = call.Arguments.Count;
        if (count == 2 && name is "isgreater" or "isless" or "isequal" or "isequalstring")
            return A(0) + (name == "isgreater" ? " больше " : name == "isless" ? " меньше " : " равно ") + A(1);
        if (count == 1 && name == "partydisguisematches") return "Одежда всех живых участников партии игрока 0 соответствует профилю " + A(0) + ". У каждого достаточно разрешённой брони и нет посторонней брони.";
        if (count == 1 && name == "not") return "НЕ: " + A(0) + " (значение равно нулю)";
        if (count == 2 && name == "gsgetvar") return "значение переменной " + A(1) + " для контекста " + A(0);
        if (count == 3 && name == "gssetvar") return "Установить переменную " + A(1) + " = " + A(2) + " для контекста " + A(0);
        if (count == 0 && name == "getworldtime") return "текущее время игрового мира";
        if (count == 0 && name == "killscript") return "Снять текущий скрипт с дальнейшего обслуживания. Оставшиеся действия этой ветки продолжатся.";
        if (count == 1 && name == "sleep") return "Подождать " + A(0) + " игровых сегментов (не секунд). Другие скрипты продолжают работать.";
        if (count == 2 && name == "questcomplete") return "Завершить квест " + A(1) + " для игрока " + A(0);
        if (analysis.Blocks.Any(b => b.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase)))
            return "Запустить экземпляр этапа «" + call.Name + "», не ожидая завершения. Аргументы: " + string.Join(", ", Enumerable.Range(0, count).Select(A));
        if (help.Entries.TryGetValue(call.Name, out var entry))
            return entry.Purpose + string.Concat(Enumerable.Range(0, count).Select(n => "\n• " + (n < entry.Arguments.Length ? entry.Arguments[n] : "аргумент " + (n + 1)) + ": " + A(n)));
        return "Неизвестный вызов «" + call.Name + "». Смысл не установлен. Код: " + analysis.Text[call.Start..call.End];
    }
}
