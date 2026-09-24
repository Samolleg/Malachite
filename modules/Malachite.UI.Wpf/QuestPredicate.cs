using System.Globalization;

namespace QuestStudio;
public sealed record QuestPredicate(string Operator, double Value)
{
    public string Description => Operator + " " + Value.ToString(CultureInfo.InvariantCulture);
    public static double? Number(string text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : null;
    public static QuestPredicate? Read(Analysis a, Call read)
    {
        var parents = a.Calls.Where(c => c.Start < read.Start && c.End >= read.End).OrderBy(c => c.End - c.Start).ToArray();
        bool Exact(Range r, Call c) => r.Start <= c.Start && r.End >= c.End && a.Text[r.Start..c.Start].Trim().Length == 0 && a.Text[c.End..r.End].Trim().Length == 0;
        if (parents.Length == 0 || parents[0].Name.Equals("Not", StringComparison.OrdinalIgnoreCase))
        {
            var expression = read; bool zero = false;
            foreach (var outer in parents)
            {
                if (!outer.Name.Equals("Not", StringComparison.OrdinalIgnoreCase) || outer.Arguments.Count != 1 || !Exact(outer.Arguments[0], expression)) return null;
                zero = !zero; expression = outer;
            }
            // A bare number is a predicate only as a top-level expression of if.
            for (int i = 0; i + 1 < a.Tokens.Count; i++)
            {
                if (!a.Tokens[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase) || a.Tokens[i + 1].Value != "(" || !a.Pairs.TryGetValue(i + 1, out int end)) continue;
                for (int p = i + 2; p < end;)
                {
                    int last = p;
                    if (p + 1 < end && a.Tokens[p + 1].Value == "(" && a.Pairs.TryGetValue(p + 1, out int close)) last = close;
                    if (a.Tokens[p].Start == expression.Start && a.Tokens[last].End == expression.End) return new(zero ? "=" : "!=", 0);
                    p = last + 1;
                }
            }
            return null;
        }
        var parent = parents[0];
        string op = parent.Name.ToLowerInvariant() switch { "isequal" => "=", "isgreater" => ">", "isless" => "<", _ => "" };
        if (op.Length == 0 || parent.Arguments.Count != 2) return null;
        int position = Exact(parent.Arguments[0], read) ? 0 : Exact(parent.Arguments[1], read) ? 1 : -1;
        if (position < 0 || Number(a.Slice(parent.Arguments[1 - position])) is not double value) return null;
        if (position == 1) op = op == ">" ? "<" : op == "<" ? ">" : op;
        var child = parent;
        foreach (var outer in parents.Skip(1))
        {
            if (!outer.Name.Equals("Not", StringComparison.OrdinalIgnoreCase) || outer.Arguments.Count != 1 || !Exact(outer.Arguments[0], child)) return null;
            op = op switch { "=" => "!=", "!=" => "=", ">" => "<=", "<" => ">=", ">=" => "<", _ => ">" };
            child = outer;
        }
        return new(op, value);
    }
    public bool Accepts(double value) => Operator switch { "=" => value == Value, "!=" => value != Value, ">" => value > Value, "<" => value < Value, ">=" => value >= Value, "<=" => value <= Value, _ => false };
    // Max writes produce a range, not a guaranteed exact value. Only eliminate
    // impossible predicates; compatible edges remain explicitly conditional.
    public bool Compatible(double written, bool maximum) => !maximum ? Accepts(written) : Operator switch
    { "=" => Value >= written, "<" => written < Value, "<=" => written <= Value, _ => true };
}
