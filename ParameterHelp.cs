using System.Text;

namespace QuestStudio;

public sealed partial class HelpService
{
    static bool BriefingCallback(string script, Parameter[] parameters) =>
        script.Equals("#OnBriefingComplete", StringComparison.OrdinalIgnoreCase) && parameters.Length == 2 &&
        parameters[0].Type == "float" && parameters[1].Type == "string";

    static string CallbackSummary(string script, Parameter[] parameters) => BriefingCallback(script, parameters)
        ? "Обработчик завершения брифинга. В справочной реализации WorldServer::BriefingComplete движок передаёт номер игрока и полный ключ брифинга (например b.npc.dialog). Это событие завершения разговора, а не любого квеста. Реальное значение зависит от вызова."
        : "";

    HoverHelp? ParameterHelp(Analysis current, string name, Analysis input, int offset, string? scope)
    {
        if (offset < 0) return null;
        if (scope == null && ReferenceEquals(current, input))
        {
            scope = current.DeclarationRanges.FirstOrDefault(x => offset >= x.Value.Start && offset < x.Value.End).Key;
            scope ??= current.BlockAt(offset)?.Name;
        }
        if (scope == null || !current.Declarations.TryGetValue(scope, out var parameters)) return null;
        int index = Array.FindIndex(parameters, p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var parameter = parameters[index];
        var body = new StringBuilder($"Параметр №{index + 1} скрипта «{scope}». Тип: {TypeName(parameter.Type)} ({parameter.Type}).\nЗначение передаётся при запуске этого Script; одноимённый параметр другого скрипта — отдельная переменная.");
        if (BriefingCallback(scope, parameters))
            body.Append(index == 0
                ? "\nПо справочной реализации WorldServer::BriefingComplete: номер игрока, для которого завершён брифинг."
                : "\nПо справочной реализации WorldServer::BriefingComplete: полный ключ завершённого брифинга, например b.npc.dialog. Это технический ключ разговора, а не его текст и не название квеста.");
        var uses = current.Calls.Where(c => c.Owner.Equals(scope, StringComparison.OrdinalIgnoreCase));
        var roles = new HashSet<string>();
        foreach (var call in uses)
        {
            if (!Entries.TryGetValue(call.Name, out var entry)) continue;
            for (int i = 0; i < call.Arguments.Count && i < entry.Arguments.Length; i++)
                if (current.Slice(call.Arguments[i]).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    roles.Add(call.Name + ": " + entry.Arguments[i]);
        }
        if (roles.Count > 0) body.Append("\n\nКак используется в этом Script:\n• ").Append(string.Join("\n• ", roles.Take(6)));
        var calls = current.Calls.Where(c => c.Name.Equals(scope, StringComparison.OrdinalIgnoreCase) && c.Arguments.Count > index).ToArray();
        if (calls.Length > 0)
        {
            body.Append("\n\nПередача в явных вызовах этого документа:");
            foreach (var call in calls.Take(4)) body.Append($"\nСтрока {call.Line}, {call.Owner}: ").Append(Compact(current.Slice(call.Arguments[index]), 140));
            if (calls.Length > 4) body.Append($"\nЕщё вызовов: {calls.Length - 4}.");
            body.Append("\nЭто выражения из кода, а не вычисленные значения запущенной игры.");
        }
        else body.Append("\nЯвных вызовов с этим аргументом в документе не найдено; текущее значение неизвестно.");
        return new(name, "Параметр Script · " + scope, body.ToString(), "Источник: DeclareScript, область видимости и вызовы открытого документа." +
            (BriefingCallback(scope, parameters) ? " Событие: справочный WorldServer.cpp / BriefingComplete; модификации runtime здесь не исполняются." : " Назначение не выводится только из имени переменной."));
    }

    static HoverHelp WithCallArguments(HoverHelp help, Analysis current, Analysis input, string name, int offset)
    {
        if (!current.Declarations.TryGetValue(name, out var parameters)) return help;
        var call = input.Calls.FirstOrDefault(c => c.Start == offset && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (call == null) return help;
        var lines = call.Arguments.Select((argument, i) => $"{i + 1}. " +
            (i < parameters.Length ? parameters[i].Name + " (" + TypeName(parameters[i].Type) + ")" : "лишний аргумент") +
            " ← " + Compact(input.Slice(argument), 140));
        return help with { Body = help.Body + "\n\nВ этом вызове передано:\n" +
            (call.Arguments.Count == 0 ? "Аргументов нет." : string.Join("\n", lines)) +
            (call.Arguments.Count < parameters.Length ? "\nНе хватает аргументов по DeclareScript." : "") };
    }
}
