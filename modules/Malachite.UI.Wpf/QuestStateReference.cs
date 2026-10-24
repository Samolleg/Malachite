using System.Globalization;
using System.Text.RegularExpressions;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public static class QuestStateReference
{
    public static bool Supports(string name) => name.ToLowerInvariant() is "gsgetvar" or "gssetvar" or "gssetvarmax" or "gsdelvar";

    public static DatabaseContext? Resolve(Analysis analysis, Call call, WorkspaceSnapshot snapshot)
    {
        if (!Supports(call.Name) || call.Arguments.Count < 2) return null;
        var raw = analysis.Slice(call.Arguments[1]).Trim();
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"' || raw.Contains('\\')) return null;
        var key = raw[1..^1];
        var parts = key.Split('.');
        if (parts.Length is not (3 or 4) || parts[0] != "q" || parts.Any(string.IsNullOrWhiteSpace)) return null;
        var record = QuestAtlas.QuestRecord(QuestAtlas.QuestRoot(key)!, snapshot);
        RecordDto[] records = record == null ? [] : [record];
        string target = parts.Length == 4 ? "Подпункт " + parts[3] : "Квест";
        string title = "Квест «" + parts[2] + "» · " + target + " · зона " + parts[1];
        string text = "";
        if (records.Length == 1)
        {
            var resources = records[0].Texts.Where(t => t.ResourceKey.Equals("quest " + parts[2], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (resources.Length == 1)
            {
                var resource = resources[0];
                title = "Квест «" + resource.Title + "» · зона " + parts[1];
                if (parts.Length == 4)
                {
                    var sections = Regex.Matches(resource.Description, @"(?im)^[\t ]*#subobj[\t ]+(\S+)[\t ]*\r?$");
                    var matches = sections.Cast<Match>().Where(m => m.Groups[1].Value == parts[3]).ToArray();
                    if (matches.Length == 1)
                    {
                        int start = matches[0].Index + matches[0].Length;
                        var tail = resource.Description[start..].TrimStart('\r', '\n');
                        var next = Regex.Match(tail, @"(?m)^[\t ]*#");
                        if (next.Success) tail = tail[..next.Index];
                        var rows = tail.Replace("\r\n", "\n").Split('\n');
                        title += "\nПодпункт " + parts[3] + ": «" + rows[0].Trim() + "»";
                        text = string.Join("\n", rows.Skip(1)).Trim();
                    }
                    else text = matches.Length == 0 ? "Подпункт #subobj " + parts[3] + " отсутствует в текущем тексте. Название не определено." : "В тексте несколько #subobj " + parts[3] + ". Связь неоднозначна.";
                }
                else
                {
                    var marker = Regex.Match(resource.Description, @"(?m)^[\t ]*#");
                    text = (marker.Success ? resource.Description[..marker.Index] : resource.Description).Trim();
                }
                if (resource.Modified) text += "\n[Текст не сохранён]";
            }
            else text = "Текст QUEST " + parts[2] + " отсутствует или неоднозначен в текущем снимке.";
        }
        else text = "Для полного ключа нет единственной записи с точным именем и подходящей зоной QDB; название и подпункт не назначены.";

        string operation;
        var name = call.Name.ToLowerInvariant();
        if (name == "gsgetvar") operation = "Прочитать состояние. Сохранённое значение игры в редакторе неизвестно.";
        else if (name == "gsdelvar") operation = "Удалить переменную: последующее чтение вернёт 0 (неизвестен / скрыт).";
        else
        {
            var value = call.Arguments.Count > 2 ? analysis.Slice(call.Arguments[2]).Trim() : "не указан";
            var state = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number switch
            { 0 => "неизвестен / скрыт", 1 => "доступен / активен", 2 => "выполнен", 3 => "провален", _ => "нестандартное значение; смысл не назначен" } : "вычисляемое значение";
            operation = name == "gssetvarmax"
                ? "Повысить состояние минимум до " + value + " («" + state + "»). Новое значение = максимум из текущего и указанного; более высокое состояние не понижается."
                : "Установить состояние " + value + " («" + state + "»), заменив текущее.";
        }
        string explanation = operation + "\nКонтекст GS: " + analysis.Slice(call.Arguments[0]).Trim() + "; ключ: " + key +
            "\n0 — неизвестен/скрыт; 1 — доступен; 2 — выполнен; 3 — провален.";
        if (parts.Length == 4) explanation += "\nЭто состояние подпункта, а не всего квеста. Видимость зависит также от состояния родительского задания; запись не гарантирует показ уведомления.";
        explanation += "\nИзменение GS само по себе не выполняет QuestComplete и не выдаёт награду.";
        return new(title, explanation, records) { FocusText = text };
    }
}
