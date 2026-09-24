using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public sealed record QuestPoint(string Id, string Quest, string Key, string Context, string Map, string? Hash,
    string Script, int Offset, int Line, string Action, string Conditions, bool Write, RecordRef? Record = null)
{
    public double? WrittenValue { get; init; }
    public bool Maximum { get; init; }
    public QuestPredicate? Predicate { get; init; }
    public string? CallTarget { get; init; }
}
public sealed record QuestConnection(string From, string To, string Reason, bool Certain);
public sealed record ScriptConnection(string From, string To, int Offset, int Line);
public sealed record BriefingUse(string Name, string Map, string Script, int Offset, int Line, string Key, string Context, string Action);
public sealed record QuestMap(string Path, string? Hash, QuestPoint[] Points, ScriptConnection[] Calls, BriefingUse[] Briefings);
public sealed record QuestRoute(QuestPoint[] Points, QuestConnection[] Connections);

public static class QuestAtlas
{
    public static string? Literal(Analysis a, Call c, int index)
    {
        if (index >= c.Arguments.Count) return null;
        var value = a.Slice(c.Arguments[index]).Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' && !value[1..^1].Contains('"') && !value.Contains('\\') ? value[1..^1] : null;
    }
    public static string? QuestKey(string key)
    {
        var parts = key.Split('.');
        return parts.Length is 3 or 4 && parts[0] == "q" && parts.All(p => p.Length > 0 && !p.Any(char.IsWhiteSpace)) ? parts[2] : null;
    }
    public static QuestMap Read(Analysis a, string path, string? hash)
    {
        List<QuestPoint> points = []; List<BriefingUse> briefings = [];
        var guards = BuildGuards(a);
        string Conditions(Call call) => string.Join("\n", guards.Where(g => call.Start >= g.Start && call.End <= g.End).Select(g => g.Text)) is { Length: > 0 } value ? value : "Без найденной обрамляющей проверки; порядок смотрите в исходном скрипте.";
        foreach (var c in a.Calls)
        {
            bool gs = QuestStateReference.Supports(c.Name);
            string? key = Literal(a, c, 1);
            string? quest = gs && key != null ? QuestKey(key) : c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase) ? key : null;
            var context = c.Arguments.Count > 0 ? a.Slice(c.Arguments[0]).Trim() : "";
            bool write = !c.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase);
            string action = c.Name + "(" + string.Join(", ", c.Arguments.Select(a.Slice)) + ")";
            if (quest != null) points.Add(new(path + "@" + c.Start, quest, gs ? key! : "", context, path, hash, c.Owner,
                c.Start, c.Line, action, Conditions(c), write)
            {
                WrittenValue = gs && write ? (c.Name.Equals("GSDelVar", StringComparison.OrdinalIgnoreCase) ? 0 : c.Arguments.Count == 3 ? QuestPredicate.Number(a.Slice(c.Arguments[2])) : null) : null,
                Maximum = c.Name.Equals("GSSetVarMax", StringComparison.OrdinalIgnoreCase),
                Predicate = !write ? QuestPredicate.Read(a, c) : null
            });
            if (c.Name.Equals("LeaveToZone", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("AddMob", StringComparison.OrdinalIgnoreCase))
                points.Add(new(path + "@" + c.Start, "", "", context, path, hash, c.Owner, c.Start, c.Line, action,
                    Conditions(c) + "\nИмя зоны не приравнивается к имени MOB. Переход по файлу ищите среди использований состояния на других картах.", false));
            if (gs && key != null)
            {
                var parts = key.Split('.');
                if (parts.Length == 3 && parts[0] == "b") briefings.Add(new(parts[2], path, c.Owner, c.Start, c.Line, key, context, action));
            }
        }
        var scriptNames = a.Blocks.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calls = a.Calls.Where(c => scriptNames.Contains(c.Name)).Select(c => new ScriptConnection(c.Owner, c.Name, c.Start, c.Line)).Distinct().ToArray();
        return new(path, hash, points.ToArray(), calls, briefings.ToArray());
    }
    static List<(int Start, int End, string Text)> BuildGuards(Analysis a)
    {
        List<(int Start, int End, string Text)> conditions = [];
        for (int i = 0; i + 2 < a.Tokens.Count; i++)
        {
            var t = a.Tokens;
            if (!t[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase) || t[i + 1].Value != "(" || !a.Pairs.TryGetValue(i + 1, out int end)) continue;
            if (end + 2 >= t.Count || !t[end + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) || !a.Pairs.TryGetValue(end + 2, out int yes)) continue;
            string expression = a.Text[t[i + 1].End..t[end].Start].Trim();
            conditions.Add((t[i].Start, t[end].End, "Проверка: " + expression));
            conditions.Add((t[end + 2].End, t[yes].Start, "ТОГДА: " + expression));
            if (yes + 2 < t.Count && t[yes + 1].Value.Equals("else", StringComparison.OrdinalIgnoreCase) && a.Pairs.TryGetValue(yes + 2, out int no))
                conditions.Add((t[yes + 2].End, t[no].Start, "ИНАЧЕ: " + expression));
        }
        return conditions;
    }
    public static QuestRoute Build(string quest, IEnumerable<QuestMap> maps, WorkspaceSnapshot snapshot)
    {
        var sources = maps.ToArray(); var points = sources.SelectMany(m => m.Points).Where(p => p.Quest == quest).ToList();
        List<QuestConnection> edges = [];
        foreach (var record in snapshot.Records.Where(r => r.Reference.TableKey == "quests.briefings"))
        foreach (var field in record.Fields.Where(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList"))
        foreach (var key in field.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(k => QuestKey(k) == quest))
        {
            string id = "brief:" + record.Reference.RecordId + ":" + field.FieldId + ":" + key;
            string action = field.FieldId == 6 ? "Выдача: повысить состояние минимум до 1" : "Завершение: установить состояние 2";
            var title = record.Texts.FirstOrDefault()?.Title ?? record.Label;
            points.Add(new(id, quest, key, "контекст брифинга", "БД · брифинги", null, record.Name, 0, 0,
                action + "\n" + title + "\n" + string.Join("\n", record.Texts.Select(t => t.Description)), "Действие после прохождения брифинга (поле QDB " + field.FieldId + ").", true, record.Reference)
                { WrittenValue = field.FieldId == 6 ? 1 : 2, Maximum = field.FieldId == 6 });
            foreach (var use in sources.SelectMany(m => m.Briefings).Where(b => b.Name == record.Name))
            {
                var source = sources.First(m => m.Path == use.Map);
                string useId = use.Map + "@brief" + use.Offset;
                if (!points.Any(p => p.Id == useId)) points.Add(new(useId, quest, use.Key, use.Context, use.Map, source.Hash, use.Script, use.Offset, use.Line, use.Action, "Использование состояния брифинга. Не доказывает, что игрок его прошёл.", !use.Action.StartsWith("GSGetVar", StringComparison.OrdinalIgnoreCase)));
                edges.Add(new(useId, id, "Состояние брифинга → запись БД; прохождение зависит от игрока", false));
            }
        }
        foreach (var map in sources)
        {
            var scripts = points.Where(p => p.Map == map.Path).Select(p => p.Script).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var forward = new HashSet<string>(scripts, StringComparer.OrdinalIgnoreCase);
            bool changed;
            do { changed = false; foreach (var link in map.Calls) if (forward.Contains(link.From)) changed |= forward.Add(link.To); } while (changed);
            do { changed = false; foreach (var link in map.Calls) if (scripts.Contains(link.To)) changed |= scripts.Add(link.From); } while (changed);
            scripts.IntersectWith(forward);
            foreach (var link in map.Calls.Where(c => scripts.Contains(c.From) && scripts.Contains(c.To)))
            {
                string id = map.Path + "@call" + link.Offset;
                points.Add(new(id, quest, "", "", map.Path, map.Hash, link.From, link.Offset, link.Line,
                    "Запустить Script «" + link.To + "»", "Прямой вызов в исходнике; условия выполнения смотрите в коде. Запуск не ждёт завершения Script.", false) { CallTarget = link.To });
            }
            points.AddRange(map.Points.Where(p => p.Quest.Length == 0 && scripts.Contains(p.Script)).Select(p => p with { Quest = quest }));
        }
        foreach (var source in points)
        foreach (var target in points)
        {
            if (source.Id == target.Id) continue;
            if (source.Key.Length > 0 && source.Key == target.Key && source.Write && !target.Write &&
                (source.Context == target.Context || source.Record != null) && source.WrittenValue is double value && target.Predicate is { } predicate && predicate.Compatible(value, source.Maximum))
                edges.Add(new(source.Id, target.Id, "Состояние совместимо с проверкой " + predicate.Description + "; остальные условия и достижимость не доказаны" + (source.Record != null ? "; контекст брифинга требует проверки" : ""), false));
            if (source.CallTarget != null && target.Record == null && source.Map == target.Map && source.CallTarget.Equals(target.Script, StringComparison.OrdinalIgnoreCase))
                edges.Add(new(source.Id, target.Id, "Этот вызов запускает Script «" + target.Script + "»; его условия проверяются отдельно", true));
        }
        return new(points.DistinctBy(p => p.Id).ToArray(), edges.Distinct().ToArray());
    }
}
