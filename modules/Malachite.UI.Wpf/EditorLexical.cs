using System.Globalization;

namespace QuestStudio;

public sealed record EditorSpan(int Start, int Length, string Value, string Kind)
{ public int End => Start + Length; }
public sealed record ArgumentHint(string Name, int Index);
public static class EditorLexical
{
    public static List<EditorSpan> Scan(string text)
    {
        List<EditorSpan> spans = [];
        for (int i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            string kind;
            if (text[i] == '"') { i++; while (i < text.Length && text[i] != '"') i++; if (i < text.Length) i++; kind = "string"; }
            else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; kind = "comment"; }
            else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            { i += 2; while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++; i = Math.Min(text.Length, i + 2); kind = "comment"; }
            else if ("(),:=".Contains(text[i])) { i++; kind = "symbol"; }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !"(),:=\"".Contains(text[i]))
                { if (i + 1 < text.Length && text[i] == '/' && text[i + 1] is '/' or '*') break; i++; }
                if (i == start) i++;
                kind = double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "number" : "id";
            }
            spans.Add(new(start, i - start, text[start..i], kind));
        }
        return spans;
    }
    public static ArgumentHint? ArgumentAt(List<EditorSpan> spans, int caret)
    {
        var stack = new Stack<ArgumentHint>(); EditorSpan? previous = null;
        foreach (var span in spans)
        {
            if (span.Start >= caret) break;
            if (span.Kind == "comment") continue;
            if (span.Value == "(") stack.Push(new(previous?.Kind == "id" ? previous.Value : "", 0));
            else if (span.Value == ")" && stack.Count > 0) stack.Pop();
            else if (span.Value == "," && stack.Count > 0) { var top = stack.Pop(); stack.Push(top with { Index = top.Index + 1 }); }
            previous = span;
        }
        return stack.Count == 0 ? null : stack.Peek();
    }
    public static (int Start, int End)? BracketPair(List<EditorSpan> spans, int caret)
    {
        var stack = new Stack<int>(); var pairs = new Dictionary<int, int>();
        foreach (var span in spans.Where(s => s.Kind == "symbol"))
        { if (span.Value == "(") stack.Push(span.Start); else if (span.Value == ")" && stack.TryPop(out int open)) { pairs[open] = span.Start; pairs[span.Start] = open; } }
        int selected = pairs.ContainsKey(caret) ? caret : caret - 1;
        return pairs.TryGetValue(selected, out int other) ? (selected, other) : null;
    }
}
