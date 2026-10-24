namespace QuestStudio;

// Source positions are indexed once per Analysis. Caret movement never reparses text.
internal sealed class CodeContextIndex
{
    internal sealed record Rule(int Start, int End, Range Conditions, Range Actions, Range? ElseActions);
    internal sealed record Construction(string Kind, string? Name);
    internal sealed record Position(int Line, int Start, int End, Call? Call, ScriptBlock? Block,
        Construction? Construction, Rule? Rule, bool Condition);
    sealed record Span<T>(int Start, int End, T Value);
    sealed class Regions<T>
    {
        readonly Span<T>[] spans;
        readonly int[] parents;
        public Regions(IEnumerable<Span<T>> source)
        {
            spans = source.OrderBy(s => s.Start).ThenByDescending(s => s.End).ToArray();
            parents = new int[spans.Length];
            var stack = new Stack<int>();
            for (int i = 0; i < spans.Length; i++)
            {
                while (stack.Count > 0 && spans[stack.Peek()].End <= spans[i].Start) stack.Pop();
                parents[i] = stack.Count == 0 ? -1 : stack.Peek(); stack.Push(i);
            }
        }
        public T? At(int offset, int outerFrom = int.MaxValue)
        {
            int low = 0, high = spans.Length;
            while (low < high) { int mid = (low + high) / 2; if (spans[mid].Start <= offset) low = mid + 1; else high = mid; }
            int at = low - 1;
            while (at >= 0 && offset >= spans[at].End) at = parents[at];
            while (at >= 0 && parents[at] >= 0 && spans[parents[at]].Start >= outerFrom) at = parents[at];
            return at < 0 ? default : spans[at].Value;
        }
    }
    readonly Analysis analysis;
    readonly int[] lines;
    readonly Regions<Call> calls;
    readonly Regions<ScriptBlock> blocks;
    readonly Regions<Construction> constructions;
    readonly Regions<Rule> rules;
    readonly Dictionary<int, Call> callsByStart;
    readonly Dictionary<string, ScriptBlock> definitions;
    readonly Dictionary<int, string> ruleSummaries = [];
    readonly Dictionary<int, string> stageSummaries = [];
    readonly List<Rule> allRules = [];

    public CodeContextIndex(Analysis analysis)
    {
        this.analysis = analysis;
        var starts = new List<int> { 0 };
        for (int i = 0; i < analysis.Text.Length; i++) if (analysis.Text[i] == '\n') starts.Add(i + 1);
        lines = starts.ToArray();
        calls = new(analysis.Calls.Select(c => new Span<Call>(c.Start, c.End, c)));
        callsByStart = analysis.Calls.GroupBy(c => c.Start).ToDictionary(g => g.Key, g => g.First());
        blocks = new(analysis.Blocks.Select(b => new Span<ScriptBlock>(b.Start, b.End, b)));
        definitions = analysis.Blocks.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var declarations = new List<Span<Construction>>();
        var tokens = analysis.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            string word = tokens[i].Value;
            bool named = word.Equals("DeclareScript", StringComparison.OrdinalIgnoreCase);
            if (named || word.Equals("GlobalVars", StringComparison.OrdinalIgnoreCase))
            {
                int open = i + (named ? 2 : 1);
                if (open < tokens.Count && tokens[open].Value == "(" && analysis.Pairs.TryGetValue(open, out int end))
                    declarations.Add(new(tokens[i].Start, tokens[end].End, new(word, named ? tokens[i + 1].Value : null)));
            }
            if (!word.Equals("if", StringComparison.OrdinalIgnoreCase) || i + 1 >= tokens.Count ||
                tokens[i + 1].Value != "(" || !analysis.Pairs.TryGetValue(i + 1, out int conditionEnd) ||
                conditionEnd + 2 >= tokens.Count || !tokens[conditionEnd + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) ||
                tokens[conditionEnd + 2].Value != "(" || !analysis.Pairs.TryGetValue(conditionEnd + 2, out int actionEnd)) continue;
            Range Slice(int open, int close) => new(tokens[open].End, tokens[close].Start - tokens[open].End);
            int last = actionEnd; Range? otherwise = null;
            if (last + 2 < tokens.Count && tokens[last + 1].Value.Equals("else", StringComparison.OrdinalIgnoreCase) &&
                tokens[last + 2].Value == "(" && analysis.Pairs.TryGetValue(last + 2, out int elseEnd))
            { otherwise = Slice(last + 2, elseEnd); last = elseEnd; }
            allRules.Add(new(tokens[i].Start, tokens[last].End, Slice(i + 1, conditionEnd), Slice(conditionEnd + 2, actionEnd), otherwise));
        }
        constructions = new(declarations);
        rules = new(allRules.Select(r => new Span<Rule>(r.Start, r.End, r)));
    }
    int TokenAtOrAfter(int offset)
    {
        int low = 0, high = analysis.Tokens.Count;
        while (low < high) { int mid = (low + high) / 2; if (analysis.Tokens[mid].End <= offset) low = mid + 1; else high = mid; }
        return low;
    }
    public Position At(int offset)
    {
        offset = Math.Clamp(offset, 0, analysis.Text.Length);
        int line = Array.BinarySearch(lines, offset); if (line < 0) line = ~line - 1;
        int start = lines[line], end = line + 1 < lines.Length ? lines[line + 1] - 1 : analysis.Text.Length;
        if (end > start && analysis.Text[end - 1] == '\r') end--;
        int token = TokenAtOrAfter(offset), focus = offset;
        if (token < analysis.Tokens.Count && analysis.Tokens[token].Start < end)
            focus = Math.Max(offset, analysis.Tokens[token].Start);
        else if (token > 0 && analysis.Tokens[token - 1].Start >= start) focus = analysis.Tokens[token - 1].Start;
        var declaration = constructions.At(focus);
        var block = blocks.At(focus);
        if (declaration?.Name is { } name) block = definitions.GetValueOrDefault(name);
        var rule = rules.At(focus);
        return new(line + 1, start, end, calls.At(focus, start), block, declaration, rule,
            rule != null && focus >= rule.Conditions.Start && focus <= rule.Conditions.End);
    }
    string Summary(Range range, bool condition, ScenarioExplanation explanation)
    {
        int token = TokenAtOrAfter(range.Start), count = 0;
        var parts = new List<string>();
        while (token < analysis.Tokens.Count && analysis.Tokens[token].Start < range.End && count < 6)
        {
            if (!callsByStart.TryGetValue(analysis.Tokens[token].Start, out var call) || call.End > range.End)
            { parts.Add("Объяснение пока недоступно / вложенная структура — смотрите код"); break; }
            string text = explanation.Brief(call, condition).Replace('\n', ' ');
            parts.Add(text.Length > 240 ? text[..240] + "…" : text);
            count++; token = TokenAtOrAfter(call.End);
        }
        if (count == 6 && token < analysis.Tokens.Count && analysis.Tokens[token].Start < range.End) parts.Add("… продолжение в коде");
        return parts.Count == 0 ? condition ? "без условий" : "нет действий" : string.Join("; ", parts);
    }
    public string RuleSummary(Rule rule, ScenarioExplanation explanation)
    {
        if (!ruleSummaries.TryGetValue(rule.Start, out var summary))
        {
            summary = "Ожидать одновременно: " + Summary(rule.Conditions, true, explanation) +
                "\n→ После выполнения · по порядку: " + Summary(rule.Actions, false, explanation);
            if (rule.ElseActions != null) summary += "\n→ Иначе: " + Summary(rule.ElseActions, false, explanation) +
                "\nПри достижении проверки выбирается одна ветка.";
            ruleSummaries[rule.Start] = summary;
        }
        return summary;
    }
    public string StageSummary(ScriptBlock block, ScenarioExplanation explanation)
    {
        if (!stageSummaries.TryGetValue(block.Start, out var summary))
        {
            var stages = new List<string>();
            int token = TokenAtOrAfter(block.Body.Start);
            while (token < analysis.Tokens.Count && analysis.Tokens[token].Start < block.Body.End)
            {
                if (stages.Count == 4) { stages.Add("Продолжение этапа — в коде."); break; }
                int offset = analysis.Tokens[token].Start;
                var rule = allRules.FirstOrDefault(r => r.Start == offset);
                if (rule != null)
                { stages.Add(RuleSummary(rule, explanation)); token = TokenAtOrAfter(rule.End); }
                else if (callsByStart.TryGetValue(offset, out var call))
                { stages.Add(explanation.Brief(call)); token = TokenAtOrAfter(call.End); }
                else { stages.Add("Объяснение пока недоступно / вложенная структура — смотрите код"); break; }
            }
            summary = block.World ? "При запуске карты · по порядку: " + Summary(block.Body, false, explanation) :
                stages.Count == 0 ? "В этом этапе нет действий." : string.Join("\n\n", stages);
            stageSummaries[block.Start] = summary;
        }
        return summary;
    }
}
