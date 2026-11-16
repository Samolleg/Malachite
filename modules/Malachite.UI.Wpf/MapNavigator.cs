using System.Text.RegularExpressions;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public sealed record NavigatorPlace(string Title, string Subtitle, string Tooltip, int Offset, int Line,
    string Script, string Group, string Search, string? Quest = null);
public sealed record NavigatorQuest(MapQuestItem Item, NavigatorPlace[] Places);
public sealed record NavigatorData(Analysis Analysis, NavigatorQuest[] Quests, NavigatorPlace[] Scripts,
    NavigatorPlace[] Places);
public sealed record StageInsertion(string Text, int Caret, string Name);

// Shared presentation data for the live navigator and the requested delivery extract.
// No WPF objects or changes to the MOB; build off the UI thread from one snapshot.
public static class MapNavigator
{
    public static readonly string[] Groups = ["WorldScript", "Квестовые", "Объекты и NPC", "Прочие"];
    public static string QuestText(string key, WorkspaceSnapshot snapshot)
    {
        var record = QuestAtlas.QuestRecord(key, snapshot);
        if (record == null) return key + "\nНет подтверждённого квеста БД для этого полного ключа.";
        var texts = record.Texts.Where(t => t.ResourceKey.Equals("quest " + record.Name, StringComparison.Ordinal)).ToArray();
        return MapQuestInventory.Title(key, snapshot) + "\n" + key + "\n" + (texts.Length == 1
            ? texts[0].Description + (texts[0].Modified ? "\n[Текст не сохранён]" : "")
            : "Текст QUEST отсутствует или неоднозначен.");
    }
    public static string PartTitle(string key, WorkspaceSnapshot snapshot)
    {
        var parts = key.Split('.');
        if (parts.Length != 4) return "Сам квест";
        var record = QuestAtlas.QuestRecord(QuestAtlas.QuestRoot(key)!, snapshot);
        var texts = record?.Texts.Where(t => t.ResourceKey.Equals("quest " + record.Name, StringComparison.Ordinal)).ToArray();
        if (texts?.Length == 1)
        {
            var matches = Regex.Matches(texts[0].Description, @"(?im)^[\t ]*#subobj[\t ]+" + Regex.Escape(parts[3]) + @"[\t ]*\r?$\r?\n([^\r\n]*)");
            if (matches.Count == 1 && matches[0].Groups[1].Value.Trim().Length > 0)
                return matches[0].Groups[1].Value.Trim();
        }
        return "Подпункт " + parts[3] + " · нет названия в связанных текстах";
    }
    public static NavigatorData Build(Analysis analysis, WorkspaceSnapshot snapshot, string path,
        IReadOnlyList<MobObject> objects, HelpService help, CommandRegistry registry)
    {
        var inventory = MapQuestInventory.Read(analysis, snapshot, path, false);
        var map = QuestAtlas.Read(analysis, path, null);
        var index = new CodeContextIndex(analysis);
        var explanation = new ScenarioExplanation(analysis, help, objects, snapshot, registry);
        var questPlaces = new List<NavigatorQuest>();
        foreach (var quest in inventory)
        {
            var record = QuestAtlas.QuestRecord(quest.Key, snapshot);
            var points = map.Points.Where(p => quest.Keys.Contains(p.Quest, StringComparer.Ordinal) ||
                p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase) &&
                QuestAtlas.QuestRecord(p.Quest, snapshot)?.Reference == record?.Reference).ToArray();
            var places = points.Select(p =>
            {
                var call = analysis.Calls.FirstOrDefault(c => c.Start == p.Offset);
                string title = call == null ? p.Action : explanation.Explain(call, !p.Write);
                string group = !p.Write ? "Проверки состояния" : p.Key.Split('.').Length == 4 ? PartTitle(p.Key, snapshot) :
                    p.WrittenValue == 1 ? "Выдача" : p.WrittenValue is 2 or 3 || p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase)
                        ? "Завершение / провал" : "Другие изменения";
                return new NavigatorPlace(title, p.Script + " · строка " + p.Line, p.Key + "\n" + title,
                    p.Offset, p.Line, p.Script, group, QuestText(quest.Key, snapshot) + "\n" + p.Key + "\n" + title, p.Quest);
            }).OrderBy(p => p.Line).ToArray();
            questPlaces.Add(new(quest, places));
        }
        var scripts = new List<NavigatorPlace>();
        var searchPlaces = new List<NavigatorPlace>();
        foreach (var block in analysis.Blocks)
        {
            var lines = index.StageLines(block, explanation);
            var conditions = new List<string>(); var actions = new List<string>(); string section = "";
            foreach (var line in lines)
            {
                if (line.Kind == "separator") break;
                if (line.Kind == "heading" && line.Indent == 0)
                { if (line.Text == "Иначе") break; section = line.Text; continue; }
                if (line.Kind != "item" || line.Indent != 0) continue;
                if (section == "Когда") conditions.Add(line.Text);
                if (section is "Тогда" or "При запуске карты") actions.Add(line.Text);
            }
            string action = actions.FirstOrDefault(a => Regex.IsMatch(a, "^«(?!q\\.)") &&
                Regex.IsMatch(a, "— (выполнен|выдан|провален)") && !a.Contains("подпункт")) ??
                actions.FirstOrDefault(a => Regex.IsMatch(a, "^«(?!q\\.)")) ?? actions.FirstOrDefault() ??
                lines.FirstOrDefault(l => l.Kind == "note" && l.Text != "без условий")?.Text ?? "Открыть код: нет краткого объяснения";
            string title = (conditions.Count > 0 ? conditions[0] + " → " : "") + action;
            bool technical = Regex.IsMatch(title, @"не объяснено|Расширенная команда|Выполняет|Проверяет|…|\(|#output|запустить этап|переменная|«q\.|этот объект|нет краткого|подпункт \d+ —") || !title.Contains('«');
            var related = questPlaces.Where(q => q.Places.Any(p => p.Script == block.Name)).ToArray();
            string group = block.World ? Groups[0] : related.Length > 0 ? Groups[1] :
                block.Calls.Any(c => Regex.IsMatch(c.Name, "^(GetObjectByID|GetObjectByName|IsDead|GetLeverState|SetLeverState|RemoveObject|RemoveObjectFromServer|SetUnit|DistanceUnit|GiveCommand|AddUnitPost|SetPlayer)", RegexOptions.IgnoreCase)) ? Groups[2] : Groups[3];
            string questText = string.Join("\n", related.SelectMany(q => q.Item.Keys.Select(k => QuestText(k, snapshot))));
            string preview = title + $"\nУсловий: {conditions.Count} · действий: {actions.Count} · блоков: {lines.Count(l => l.Kind == "heading" && l.Text == "Когда" && l.Indent == 0)}";
            string summary = CodeContextIndex.PlainText(lines);
            string search = block.Name + "\n" + analysis.Text[block.Start..block.End] + "\n" + summary + "\n" + questText;
            scripts.Add(new(title, block.Display + " · строка " + block.Line + (technical ? " · контекст в коде" : ""),
                preview, block.Start, block.Line, block.Name, group, search));
            // One result per source line, plus the block header for name/text matches.
            searchPlaces.Add(scripts[^1]);
            foreach (var calls in block.Calls.GroupBy(c => c.Line))
            {
                var call = calls.OrderBy(c => c.Start).First();
                string descriptions = string.Join("\n", calls.Select(c => explanation.Explain(c, index.At(c.Start).Condition)));
                searchPlaces.Add(new(descriptions.Split('\n')[0], block.Display + " · строка " + call.Line,
                    descriptions, call.Start, call.Line, block.Name, group,
                    block.Name + "\n" + descriptions + "\n" + questText + "\n" + string.Join("\n", calls.Select(c => analysis.Text[c.Start..c.End]))));
            }
        }
        return new(analysis, questPlaces.ToArray(), scripts.ToArray(), searchPlaces.DistinctBy(p => p.Line).ToArray());
    }
    public static NavigatorPlace[] Search(NavigatorData data, string query, string group = "Все") =>
        data.Places.Where(p => (group == "Все" || p.Group == group) && p.Search.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    public static StageInsertion InsertStage(Analysis analysis, int caret)
    {
        string text = analysis.Text, nl = text.Contains("\r\n") ? "\r\n" : "\n";
        int number = 1;
        while (analysis.Declarations.ContainsKey("NewStage" + number) || analysis.Blocks.Any(b => b.Name.Equals("NewStage" + number, StringComparison.OrdinalIgnoreCase))) number++;
        string name = "NewStage" + number;
        // Never insert a Script inside a Script, declaration or a token.
        var block = analysis.BlockAt(Math.Clamp(caret, 0, text.Length));
        int offset = block?.End ?? analysis.Blocks.Where(b => b.Start >= caret).Select(b => b.Start).DefaultIfEmpty(text.Length).Min();
        string body = nl + nl + "Script " + name + nl + "(" + nl + "  if ( )" + nl + "  then" + nl + "  (" + nl + "  )" + nl + ")" + nl;
        int declarationOffset = Math.Min(offset, analysis.Blocks.Select(b => b.Start).DefaultIfEmpty(0).Min());
        string declaration = "DeclareScript " + name + "()" + nl + nl;
        string result = text.Insert(offset, body).Insert(declarationOffset, declaration);
        return new(result, offset + declaration.Length + body.IndexOf("if (", StringComparison.Ordinal) + 5, name);
    }
}
