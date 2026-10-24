using System.IO;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public sealed record MapQuestItem(string Key, string Title, int Category, string Reason, string Proof)
{
    public string Group => Category == 0 ? "Квесты этой карты" : "Через брифинги";
    public override string ToString() => Title + " · " + Key + " · " + Reason;
}

// Shared by the picker and the delivery extract; no cross-map index or explanations on the UI thread.
public static class MapQuestInventory
{
    public static string Title(string quest, WorkspaceSnapshot snapshot)
    {
        var record = QuestAtlas.QuestRecord(quest, snapshot);
        if (record == null) return "Состояние без подтверждённого квеста БД";
        var texts = record.Texts.Where(t => t.ResourceKey.Equals("quest " + record.Name, StringComparison.Ordinal)).ToArray();
        return texts.Length == 1 ? texts[0].Title : quest + " · текст QUEST отсутствует или неоднозначен";
    }

    public static MapQuestItem[] Read(Analysis analysis, WorkspaceSnapshot snapshot, string path, bool includeBriefings)
    {
        var direct = analysis.Calls.Where(c => QuestStateReference.Supports(c.Name))
            .Select(c => (Call: c, Key: QuestAtlas.Literal(analysis, c, 1)))
            .Where(p => p.Key != null && QuestAtlas.QuestRoot(p.Key) != null)
            .GroupBy(p => QuestAtlas.QuestRoot(p.Key!)!, StringComparer.Ordinal).ToArray();
        var items = new List<MapQuestItem>();
        string Proof(Call c, string key) => Path.GetFileName(path) + ":" + c.Line + " · " + key + " · " + analysis.Text[c.Start..c.End];
        var completed = analysis.Calls.Where(c => c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase))
            .Select(c => (Call: c, Name: QuestAtlas.Literal(analysis, c, 1)))
            .Where(p => p.Name != null && QuestAtlas.QuestRecord(p.Name, snapshot) != null).ToArray();
        foreach (var group in direct)
        {
            var record = QuestAtlas.QuestRecord(group.Key, snapshot);
            if (record == null) continue;
            var writes = group.Where(p => (p.Call.Name.Equals("GSSetVar", StringComparison.OrdinalIgnoreCase) ||
                p.Call.Name.Equals("GSSetVarMax", StringComparison.OrdinalIgnoreCase)) && p.Call.Arguments.Count == 3 &&
                QuestPredicate.Number(analysis.Slice(p.Call.Arguments[2])) is >= 1).ToArray();
            var completion = completed.Where(p => p.Name == record.Name).ToArray();
            var reads = group.Where(p => p.Call.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (writes.Length == 0 && completion.Length == 0 && reads.Length == 0) continue;
            bool local = writes.Length > 0 || completion.Length > 0;
            // One short proof in the picker; every occurrence remains in Proof and the overview.
            var primary = writes.FirstOrDefault(p => p.Key == group.Key);
            if (primary.Call == null) primary = writes.FirstOrDefault();
            string reason = primary.Call != null ? WriteReason(primary.Key!, QuestPredicate.Number(analysis.Slice(primary.Call.Arguments[2]))!.Value) + " стр. " + primary.Call.Line :
                completion.Length > 0 ? "завершается (QuestComplete) стр. " + completion[0].Call.Line : ReadReason(analysis, reads[0].Call);
            items.Add(new(group.Key, Title(group.Key, snapshot), local ? 0 : 1, reason,
                string.Join("\n", group.Select(p => Proof(p.Call, p.Key!)).Concat(completion.Select(p => Proof(p.Call, p.Name!))))));
        }
        // QuestComplete names a DB record, not a short GS alias. Keep that explicit identity
        // if no matching full root is present on this map (never invent a zone suffix).
        foreach (var group in completed.GroupBy(p => p.Name!, StringComparer.Ordinal))
            if (!items.Any(q => q.Category == 0 && QuestAtlas.QuestRecord(q.Key, snapshot)?.Name == group.Key))
                items.Add(new(group.Key, Title(group.Key, snapshot), 0, "завершается (QuestComplete) стр. " + group.First().Call.Line,
                    string.Join("\n", group.Select(p => Proof(p.Call, p.Name!)))));
        if (includeBriefings)
        {
            var briefings = analysis.Calls.Where(c => QuestStateReference.Supports(c.Name))
                .Select(c => QuestAtlas.Literal(analysis, c, 1)?.Split('.'))
                .Where(p => p is { Length: 3 } && p[0] == "b").Select(p => p![2]).ToHashSet(StringComparer.Ordinal);
            foreach (var briefing in snapshot.Records.Where(r => r.Reference.TableKey == "quests.briefings" && briefings.Contains(r.Name)))
            foreach (var field in briefing.Fields.Where(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList"))
            foreach (var key in field.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (QuestAtlas.QuestRoot(key) is { } root && QuestAtlas.QuestRecord(root, snapshot) != null && !items.Any(q => q.Key == root && q.Category != 2))
                {
                    var calls = analysis.Calls.Where(c => QuestStateReference.Supports(c.Name) && QuestAtlas.Literal(analysis, c, 1)?.Split('.') is { Length: 3 } p && p[0] == "b" && p[2] == briefing.Name);
                    items.Add(new(root, Title(root, snapshot), 2, "брифинг " + briefing.Name + " · " + (field.FieldId == 6 ? "выдача" : "завершение"),
                        "БД · брифинг " + briefing.Name + " · поле " + field.FieldId + " · " + key + "\n" +
                        string.Join("\n", calls.Select(c => Proof(c, QuestAtlas.Literal(analysis, c, 1)!)))));
                }
        }
        return items.GroupBy(q => (q.Key, q.Category)).Select(g => g.First() with
            { Reason = string.Join("; ", g.Select(q => q.Reason).Distinct()), Proof = string.Join("\n", g.Select(q => q.Proof).Distinct()) })
            .OrderBy(q => q.Category).ThenBy(q => q.Title).ThenBy(q => q.Key).ToArray();
    }

    static string WriteReason(string key, double value)
    {
        string operation = value switch { 1 => "выдаётся", 2 => "завершается", 3 => "проваливается", _ => "состояние " + value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        return key.Split('.') is { Length: 4 } parts ? "подпункт " + parts[3] + " " + (value == 2 ? "выполнен" : value == 1 ? "активен" : value == 3 ? "провален" : operation) : operation;
    }

    static string ReadReason(Analysis analysis, Call read)
    {
        var t = analysis.Tokens;
        for (int i = 0; i + 2 < t.Count; i++)
        {
            if (!t[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase) || t[i + 1].Value != "(" ||
                !analysis.Pairs.TryGetValue(i + 1, out int end) || read.Start < t[i + 1].End || read.End > t[end].Start ||
                end + 2 >= t.Count || !t[end + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) ||
                t[end + 2].Value != "(" || !analysis.Pairs.TryGetValue(end + 2, out int body)) continue;
            bool deletion = analysis.Calls.Any(c => c.Start >= t[end + 2].End && c.End <= t[body].Start &&
                (c.Name.Equals("RemoveObject", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("RemoveObjectFromServer", StringComparison.OrdinalIgnoreCase)));
            return (deletion ? "условие для удаления объекта" : "условие в " + read.Owner) + ", стр. " + read.Line;
        }
        return "проверка состояния в " + read.Owner + ", стр. " + read.Line;
    }
}
