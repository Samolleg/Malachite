namespace QuestStudio;

public sealed record ScenarioLine(string Text, int Offset, int Depth, bool Caution = false)
{
    public int End { get; init; }
    public bool Compact { get; init; }
}

// A read-only view of original token ranges, never of lowered/generated jumps.
public sealed class ReadableScenario
{
    readonly Analysis analysis;
    readonly ScenarioExplanation explanation;
    readonly List<ScenarioLine> lines = [];
    readonly Dictionary<int, Call> calls;
    ReadableScenario(Analysis analysis, ScenarioExplanation explanation)
    {
        this.analysis = analysis; this.explanation = explanation;
        calls = analysis.Calls.GroupBy(c => c.Start).ToDictionary(g => g.Key, g => g.First());
    }
    public static IReadOnlyList<ScenarioLine> Describe(Analysis analysis, ScriptBlock block, HelpService help)
        => Describe(analysis, block, new ScenarioExplanation(analysis, help));

    public static IReadOnlyList<ScenarioLine> Describe(Analysis analysis, ScriptBlock block, ScenarioExplanation explanation)
    {
        var reader = new ReadableScenario(analysis, explanation);
        var first = block.Calls.FirstOrDefault();
        string title = block.World ? "При запуске карты" : first == null ? "Этап без вызовов" :
            "Ориентир этапа: " + Short(explanation.Brief(first, first.Start < (block.Conditions?.End ?? 0)), 190);
        reader.lines.Add(new(title + "\n" + block.Display + (block.World ? "" : " · проверки по порядку; соседние ЕСЛИ независимы, это не ИНАЧЕ") +
            "\nЭто описание исходника, не состояние игры. Источники имён и полное объяснение — в раскрытии.", block.Start, 0) { Compact = true, End = block.End });
        var start = analysis.Tokens.FindIndex(t => t.Start >= block.Body.Start);
        var end = analysis.Tokens.FindIndex(t => t.Start >= block.Body.End);
        if (start >= 0)
        {
            int last = end < 0 ? analysis.Tokens.Count : end;
            if (block.World && start < last) reader.lines.Add(new("При запуске · по порядку: " + reader.Summary(start, last, false), block.Start, 0) { Compact = true, End = block.End });
            reader.Sequence(start, last, 0);
        }
        if (reader.lines.Count == 1) reader.lines.Add(new("В этом этапе нет действий.", block.Start, 0) { Compact = true });
        return reader.lines;
    }
    static string Short(string text, int limit) => text.Length <= limit ? text : text[..limit] + "… [полностью — в раскрытии]";
    string Summary(int start, int end, bool conditions)
    {
        var parts = new List<string>();
        for (int i = start; i < end;)
        {
            var token = analysis.Tokens[i];
            if (!calls.TryGetValue(token.Start, out var call) || call.End > analysis.Tokens[end - 1].End)
            {
                parts.Add("объяснение пока недоступно / вложенная структура — см. раскрытие и исходный код");
                break;
            }
            parts.Add(explanation.Brief(call, conditions));
            do { i++; } while (i < end && analysis.Tokens[i].Start < call.End);
        }
        if (parts.Count == 0) return conditions ? "без условий" : "нет действий";
        return string.Join("; ", parts.Take(6).Select(p => Short(p.Replace('\n', ' '), 240))) +
            (parts.Count > 6 ? "; ещё " + (parts.Count - 6) + " — в раскрытии" : "");
    }
    void Sequence(int start, int end, int depth)
    {
        if (depth > 32) { lines.Add(new("Глубокая вложенность: смотрите исходный код.", analysis.Tokens[start].Start, 32, true) { Compact = true }); return; }
        var tokens = analysis.Tokens;
        for (int i = start; i < end;)
        {
            if (lines.Count >= 500) { lines.Add(new("Показаны первые 500 строк описания. Продолжение — в исходном коде.", tokens[i].Start, depth, true) { Compact = true }); return; }
            var token = tokens[i];
            if (token.Value.Equals("if", StringComparison.OrdinalIgnoreCase) && i + 1 < end && tokens[i + 1].Value == "(" &&
                analysis.Pairs.TryGetValue(i + 1, out var conditionEnd) && conditionEnd + 2 < end &&
                tokens[conditionEnd + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) && tokens[conditionEnd + 2].Value == "(" &&
                analysis.Pairs.TryGetValue(conditionEnd + 2, out var yesEnd) && yesEnd < end)
            {
                lines.Add(new("Ожидать одновременно: " + Summary(i + 2, conditionEnd, true) +
                    "\n→ После выполнения условий · по порядку: " + Summary(conditionEnd + 3, yesEnd, false), token.Start, depth)
                    { Compact = depth == 0, End = tokens[yesEnd].End });
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
                    lines.Add(new("ИНАЧЕ · если условия этой проверки не выполнены: " + Summary(i + 2, noEnd, false), tokens[i].Start, depth)
                        { Compact = depth == 0, End = tokens[noEnd].End });
                    if (noEnd == i + 2) lines.Add(new("Нет действий.", tokens[i].Start, depth + 1));
                    else Sequence(i + 2, noEnd, depth + 1);
                    i = noEnd + 1;
                }
                continue;
            }
            if (calls.TryGetValue(token.Start, out var call) && call.End <= tokens[end - 1].End)
            {
                var text = explanation.Explain(call);
                lines.Add(new(text, token.Start, depth, text.StartsWith("Объяснение пока недоступно", StringComparison.Ordinal)) { End = call.End });
                do { i++; } while (i < end && tokens[i].Start < call.End);
                continue;
            }
            // Preserve unsupported syntax as a visible source fragment, not a guessed action.
            int last = i;
            while (last + 1 < end && tokens[last + 1].Line == token.Line) last++;
            lines.Add(new("Объяснение пока недоступно. Код: " + analysis.Text[token.Start..tokens[last].End], token.Start, depth, true) { End = tokens[last].End });
            i = last + 1;
        }
    }
    void Conditions(int start, int end, int depth)
    {
        for (int i = start; i < end;)
        {
            var token = analysis.Tokens[i];
            if (lines.Count >= 500)
            {
                lines.Add(new("Показаны первые 500 строк описания. Остальные условия — в исходном коде.", token.Start, depth, true) { Compact = true });
                return;
            }
            if (calls.TryGetValue(token.Start, out var call))
            {
                lines.Add(new(explanation.Explain(call, true), token.Start, depth) { End = call.End });
                do { i++; } while (i < end && analysis.Tokens[i].Start < call.End);
            }
            else { lines.Add(new("Объяснение пока недоступно. Условие должно быть ненулевым. Код: «" + token.Value + "»", token.Start, depth, true) { End = token.End }); i++; }
        }
    }
}
