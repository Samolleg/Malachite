using System.Text;

namespace QuestStudio;

// Port of the installed addon's MobIfElse text pass. Original text is never replaced.
// Keeps forward jumps and line placement available for parity tests and inspection.
public sealed record LoweredScript(string Text, bool Changed, string? Error, int Offset, bool DepthExceeded = false);
public sealed class AddonSyntax
{
    readonly Analysis a;
    readonly List<Token> tokens;
    int at, depth;
    bool changed;
    AddonSyntax(Analysis a) { this.a = a; tokens = a.Tokens; }
    bool Is(string value) => at < tokens.Count && tokens[at].Value.Equals(value, StringComparison.OrdinalIgnoreCase);
    Token Take() => at < tokens.Count ? tokens[at++] : throw new InvalidOperationException("Неожиданный конец конструкции if/else.");
    Token Need(string value) => Is(value) ? Take() : throw new InvalidOperationException($"Ожидается «{value}».");
    static Token Generated(string text, Token origin) => new(text, "generated", origin.Start, 0, origin.Line);
    List<Token> Balanced()
    {
        List<Token> result = [Need("(")]; int count = 1;
        while (count != 0) { var t = Take(); if (t.Value == "(") count++; if (t.Value == ")") count--; result.Add(t); }
        return result;
    }
    List<Token> Expression()
    {
        List<Token> result = [Take()];
        if (Is("(")) result.AddRange(Balanced());
        return result;
    }
    List<List<Token>> Conditions()
    {
        Need("("); List<List<Token>> result = [];
        while (!Is(")")) result.Add(Expression());
        Need(")"); return result;
    }
    static List<List<Token>> Branch(List<List<Token>> conditions, List<List<Token>> yes, List<List<Token>> no, Token head, Token endYes)
    {
        List<List<Token>> result = [];
        int tail = yes.Count + (no.Count == 0 ? 0 : 1);
        for (int i = 0; i < conditions.Count; i++)
        {
            List<Token> command = [Generated("JmvBranchUnless", head), Generated("(", head)];
            command.AddRange(conditions[i]); var last = command[^1];
            command.AddRange([Generated(",", last), Generated((conditions.Count - i - 1 + tail).ToString(), last), Generated(")", last)]);
            result.Add(command);
        }
        result.AddRange(yes);
        if (no.Count > 0) result.Add([Generated("JmvBranchSkip", endYes), Generated("(", endYes), Generated(no.Count.ToString(), endYes), Generated(")", endYes)]);
        result.AddRange(no); return result;
    }
    List<List<Token>> Actions()
    {
        if (++depth > 64) throw new InvalidOperationException("Вложенность списков действий превышает 64 уровня.");
        Need("("); List<List<Token>> result = [];
        while (!Is(")"))
        {
            if (Is("if"))
            {
                var head = Take(); var condition = Conditions(); Need("then"); var yes = Actions(); var endYes = tokens[at - 1];
                List<List<Token>> no = [];
                if (Is("else")) { Take(); no = Actions(); }
                result.AddRange(Branch(condition, yes, no, head, endYes)); changed = true;
            }
            else
            {
                if (Is("else")) throw new InvalidOperationException("else без соответствующего if/then.");
                List<Token> command = [Take()];
                if (Is("=")) { command.Add(Take()); command.AddRange(Expression()); }
                else command.AddRange(Balanced());
                if (Is("("))
                {
                    var open = tokens[at]; var body = Actions(); command.Add(open);
                    command.AddRange(body.SelectMany(c => c)); command.Add(tokens[at - 1]);
                }
                result.Add(command);
            }
        }
        Need(")"); depth--; return result;
    }
    List<Token> Script()
    {
        List<Token> result = [Need("(")];
        while (!Is(")"))
        {
            int begin = at; var head = Need("if"); var condition = Conditions(); Need("then");
            var open = tokens[at]; var yes = Actions(); var close = tokens[at - 1];
            if (Is("else"))
            {
                Take(); var no = Actions(); var last = tokens[at - 1];
                result.AddRange(new[] { "if", "(", ")", "then", "(" }.Select(s => Generated(s, head)));
                result.AddRange(Branch(condition, yes, no, head, close).SelectMany(c => c)); result.Add(last); changed = true;
            }
            else
            {
                int prefix = begin;
                while (prefix < at && !(tokens[prefix].Value == "(" && prefix > begin && tokens[prefix - 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase))) prefix++;
                result.AddRange(tokens.GetRange(begin, prefix - begin)); result.Add(open); result.AddRange(yes.SelectMany(c => c)); result.Add(close);
            }
        }
        result.Add(Need(")")); return result;
    }
    LoweredScript Run()
    {
        try
        {
            List<Token> result = [];
            while (at < tokens.Count)
            {
                var word = Take(); result.Add(word);
                switch (word.Value.ToLowerInvariant())
                {
                    case "script": result.Add(Take()); result.AddRange(Script()); break;
                    case "worldscript":
                        var open = tokens[at]; var commands = Actions(); result.Add(open); result.AddRange(commands.SelectMany(c => c)); result.Add(tokens[at - 1]); break;
                    case "declarescript": result.Add(Take()); result.AddRange(Balanced()); break;
                    case "globalvars": result.AddRange(Balanced()); break;
                    default: throw new InvalidOperationException("Недопустимый раздел сценария.");
                }
            }
            if (!changed) return new(a.Text, false, null, 0);
            var output = new StringBuilder(); int line = 1;
            foreach (var token in result)
            {
                while (line < token.Line) { output.Append('\n'); line++; }
                output.Append(token.Value).Append(' '); line += token.Value.Count(c => c == '\n');
            }
            return new(output.ToString(), true, null, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            bool extension = changed || tokens.Any(t => t.Kind == "id" && t.Value.Equals("else", StringComparison.OrdinalIgnoreCase));
            return new(a.Text, false, extension ? ex.Message : null, at < tokens.Count ? tokens[at].Start : a.Text.Length, depth > 64);
        }
    }
    public static LoweredScript Lower(Analysis analysis) => new AddonSyntax(analysis).Run();
}
