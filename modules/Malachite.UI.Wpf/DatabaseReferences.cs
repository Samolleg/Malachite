using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public sealed record DatabaseContext(string Title, string Explanation, RecordDto[] Records)
{
    public string? FocusText { get; init; }
    public string Description => Explanation + (FocusText != null ? "\n\n" + FocusText : string.Concat(Records.Select(r =>
        "\n\n" + r.Name + " · " + r.Label + "\n" + r.Reference.TableKey +
        string.Concat(r.Texts.Select(t => "\n" + t.ResourceKey + "\n" + t.Title + "\n" + t.Description +
            (t.Modified ? "\n[Текст не сохранён]" : ""))))));
}

// Resolve only explicitly known argument roles and exact resource keys. A MOB
// object identifier is not a database record identifier.
public static class DatabaseReferences
{
    public static DatabaseContext? Resolve(Analysis analysis, int position, WorkspaceSnapshot snapshot)
    {
        var call = analysis.Calls.Where(c => c.Start <= position && c.End > position)
            .OrderBy(c => c.End - c.Start).FirstOrDefault();
        if (call == null) return null;
        var questState = QuestStateReference.Resolve(analysis, call, snapshot);
        if (questState != null) return questState;
        if (call.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count > 1)
        {
            var argument = call.Arguments[1];
            if (position < call.Start + call.Name.Length || position >= argument.Start && position < argument.End)
            {
                var key = Literal(analysis, argument);
                if (key == null) return new("Квест · QuestComplete", "Ключ вычисляется во время игры. Статически определить запись нельзя.", []);
                var records = WorkspaceReferenceIndex.For(snapshot).Quests(key);
                return new("Квест: " + key, Status(records.Length, snapshot), records);
            }
        }
        var token = analysis.Tokens.FirstOrDefault(t => t.Kind == "string" && t.Start <= position && t.End > position);
        if (token == null) return null;
        var literal = Literal(analysis, new(token.Start, token.Length));
        if (literal == null) return null;
        var matches = WorkspaceReferenceIndex.For(snapshot).Texts(literal);
        return matches.Length == 0 ? null : new("Текстовый ресурс: " + literal,
            "Точное совпадение ключа в связанных текстах БД. Само совпадение не доказывает, что эта функция читает текстовый ресурс.\n" + Status(matches.Length, snapshot), matches);
    }

    static string? Literal(Analysis analysis, Range range)
    {
        var tokens = analysis.Tokens.Where(t => t.Start >= range.Start && t.End <= range.End).ToArray();
        if (tokens.Length != 1 || tokens[0].Kind != "string") return null;
        var raw = analysis.Slice(new(tokens[0].Start, tokens[0].Length));
        // Escapes require engine semantics; do not guess a different key.
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' && !raw.Contains('\\') ? raw[1..^1] : null;
    }

    static string Status(int count, WorkspaceSnapshot snapshot) =>
        (count == 0 ? "Ключ не найден в текущем снимке БД. Проверьте открытую базу и написание ключа; это не доказательство ошибки в игре." :
         count == 1 ? "Найдена запись в текущей БД." : "Найдено несколько записей. Выберите нужную; автоматический переход отключён.") +
        (snapshot.Modified ? "\nБД содержит несохранённые изменения; показаны текущие данные редактора." : "");
}
