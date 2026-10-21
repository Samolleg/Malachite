using System.IO;
using System.Text.RegularExpressions;

namespace QuestStudio;

public static class ContextAction
{
    public static EditorPlan Build(Analysis analysis, string blockName, bool otherwise, CommandInfo command, string[] arguments, CommandRegistry registry)
    {
        if (analysis.Issues.Any(i => i.Severity == "error")) throw new InvalidDataException("Сначала исправьте ошибки кода.");
        if (!registry.Commands.TryGetValue(command.Name, out var known) || known != command || command.ReturnType != "void" || command.DebugOnly || command.Internal || !command.Accepts(arguments.Length))
            throw new InvalidDataException("Выберите доступное действие и заполните его параметры.");
        var block = analysis.Blocks.Single(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
        var range = block.World && !otherwise ? block.Body : block.CardEditable ? otherwise ? block.ElseActions : block.Actions : null;
        if (range == null) throw new InvalidDataException("У этого скрипта несколько веток или нет выбранной ветки. Выберите точное место в коде либо используйте шаблон.");
        string call = command.Name + "(" + string.Join(", ", arguments) + ")";
        var fragment = ScriptAnalyzer.Analyze("WorldScript(" + call + ")", registry);
        // The complete result below also checks names in the actual declaration scope.
        if (fragment.Issues.Any(i => i.Code is "PAREN" or "STRING" || i.Code == "ARITY" && i.Severity == "error")) throw new InvalidDataException("Неверные параметры действия.");
        string source = analysis.Text, nl = source.Contains("\r\n") ? "\r\n" : "\n";
        int at = range.End;
        while (at > range.Start && char.IsWhiteSpace(source[at - 1])) at--;
        var first = block.Calls.FirstOrDefault(c => c.Start >= range.Start && c.End <= range.End);
        int lineStart = first == null ? -1 : source.LastIndexOf('\n', Math.Max(0, first.Start - 1));
        string indent = first == null ? "    " : source[(lineStart + 1)..first.Start];
        if (!Regex.IsMatch(indent, "^[ \\t]*$")) indent = "    ";
        string insert = nl + indent + call;
        if (!source[at..range.End].Contains('\n')) insert += nl;
        string result = EditorOperations.Apply(source, [new(at, 0, insert)]);
        var after = ScriptAnalyzer.Analyze(result, registry);
        if (after.Issues.Any(i => i.Severity == "error")) throw new InvalidDataException("Действие не добавлено: " + after.Issues.First(i => i.Severity == "error").Message);
        return new(source, result, command.Name + " → " + block.Display + (otherwise ? " · ИНАЧЕ" : block.World ? " · запуск карты" : " · ТОГДА"));
    }
}
