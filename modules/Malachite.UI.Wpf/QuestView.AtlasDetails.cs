using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    FrameworkElement AtlasCoverage(QuestMap[] maps)
    {
        var panel = new StackPanel();
        panel.Children.Add(AtlasNote("Снимок индексированных файлов; новые/удалённые файлы обнаруживаются при обновлении папки. Непросмотренные карты и внешние события неизвестны. Открытые вкладки: текущий код, включая несохранённый и конфликтный локальный ввод. Неприменённые поля конструктора не входят в текст.", true));
        foreach (var map in maps.OrderBy(m => m.Path))
        {
            panel.Children.Add(AtlasNote(map.Path + (map.Hash?.StartsWith("text:", StringComparison.Ordinal) == true ? " · текущий текст открытой вкладки" : " · дисковый снимок") +
                (map.Text.Length == 0 ? " · текст недоступен в старом индексе: обновите папку MOB" : ""), true));
            foreach (var gap in map.Gaps) panel.Children.Add(AtlasNote(Path.GetFileName(map.Path) + " · " + gap, true));
        }
        foreach (var path in folderFiles.Where(p => !maps.Any(m => m.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
            panel.Children.Add(AtlasNote(path + " · отсутствует в прочитанном индексе (не прочитан / ещё обрабатывается)", true));
        foreach (var error in atlasIndexErrors) panel.Children.Add(AtlasNote(error, true));
        return panel;
    }
    void AddAtlasQuestText(string quest, WorkspaceSnapshot snapshot)
    {
        var records = snapshot.Records.Where(r => r.Reference.TableKey == "quests.quests" && r.Name == quest).ToArray();
        if (records.Length != 1) { AtlasNodes.Children.Add(AtlasNote("Название и подпункты не установлены: запись отсутствует или неоднозначна.", true)); return; }
        var texts = records[0].Texts.Where(t => t.ResourceKey.Equals("quest " + quest, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (texts.Length != 1) { AtlasNodes.Children.Add(AtlasNote("Название и подпункты: текст QUEST отсутствует или неоднозначен в снимке БД.", true)); return; }
        var resource = texts[0];
        if (resource.Modified) AtlasNodes.Children.Add(AtlasNote("Показан несохранённый текст QUEST из текущего снимка БД.", true));
        var sections = Regex.Matches(resource.Description, @"(?im)^[\t ]*#subobj[\t ]+(\S+)[\t ]*\r?$").Cast<Match>().ToArray();
        foreach (var section in sections)
        {
            var tail = resource.Description[(section.Index + section.Length)..].TrimStart('\r', '\n');
            string title = tail.Split('\r', '\n')[0].Trim();
            bool duplicate = sections.Count(s => s.Groups[1].Value == section.Groups[1].Value) != 1;
            AtlasNodes.Children.Add(AtlasNote("Подпункт " + section.Groups[1].Value + ": " + (duplicate ? "неоднозначный #subobj" : title)));
        }
        AtlasNodes.Children.Add(AtlasLazy("Описание из текущего текста QUEST", () => AtlasNote(resource.Description)));
    }
    void AddAtlasGroup(string title, IEnumerable<QuestPoint> points, QuestMap[] maps, WorkspaceSnapshot snapshot, QuestRoute route)
    {
        AtlasNodes.Children.Add(new TextBlock { Text = title, FontSize = 17, Foreground = MalachiteTheme.Accent, Margin = new(5, 16, 5, 8) });
        var rows = points.OrderBy(p => p.Record == null).ThenBy(p => p.Map).ThenBy(p => p.Line).ToArray();
        if (rows.Length == 0) AtlasNodes.Children.Add(AtlasNote("В просмотренных источниках не найдено; это пробел, а не доказательство отсутствия.", true));
        foreach (var point in rows) AtlasNodes.Children.Add(AtlasPointView(point, maps, snapshot, route));
    }
    FrameworkElement AtlasPointView(QuestPoint point, QuestMap[] maps, WorkspaceSnapshot snapshot, QuestRoute route)
    {
        var panel = new StackPanel(); panel.Children.Add(AtlasLink(point));
        if (point.Record != null)
        {
            panel.Children.Add(AtlasNote(point.Action + "\n" + point.Key + "\n" + point.Conditions));
            panel.Children.Add(AtlasNote("Брифинг должен быть пройден игроком. Место разговора и достижимость не определяются одним GS-обращением.", true));
            var briefing = snapshot.Records.FirstOrDefault(r => r.Reference == point.Record);
            bool found = false;
            if (briefing != null)
            foreach (var sourceMap in maps)
            foreach (var use in sourceMap.Briefings.Where(b => b.Name == briefing.Name))
            {
                found = true;
                panel.Children.Add(AtlasLink(new(sourceMap.Path + "@" + use.Offset, point.Quest, use.Key, use.Context, sourceMap.Path, sourceMap.Hash, use.Script, use.Offset, use.Line, use.Action, "", false),
                    "Состояние разговора · " + Path.GetFileName(sourceMap.Path) + " · " + use.Script + ":" + use.Line + " · не доказательство выдачи"));
            }
            if (!found) panel.Children.Add(AtlasNote("Использование состояния этого брифинга в просмотренных картах не найдено; место разговора неизвестно.", true));
        }
        else if (maps.FirstOrDefault(m => m.Path.Equals(point.Map, StringComparison.OrdinalIgnoreCase)) is { Text.Length: > 0 } map)
        {
            var context = AtlasExplain(map, snapshot); var position = context.Index.At(point.Offset);
            var call = context.Analysis.Calls.FirstOrDefault(c => c.Start == point.Offset);
            panel.Children.Add(AtlasNote(call == null ? point.Action : context.Explanation.Explain(call, position.Condition)));
            if (position.Block != null)
            {
                panel.Children.Add(AtlasNote(position.Rule == null ? context.Index.StageSummary(position.Block, context.Explanation) : context.Index.RuleSummary(position.Rule, context.Explanation), true));
                panel.Children.Add(AtlasChildren(map, position.Block.Name, snapshot, new(StringComparer.OrdinalIgnoreCase)));
                if (!map.Calls.Any(c => c.To.Equals(position.Block.Name, StringComparison.OrdinalIgnoreCase)) && !position.Block.World)
                    panel.Children.Add(AtlasNote("Входящий запуск этого Script не найден. Внешнее событие / достижимость неизвестны.", true));
            }
            // Other independent checks in the same Script can finish a neighbouring quest.
            var nearby = map.Points.Where(p => p.Script.Equals(point.Script, StringComparison.OrdinalIgnoreCase) && p.Id != point.Id &&
                (p.Write || !p.Write && p.Key == point.Key)).ToArray();
            if (nearby.Length > 0) panel.Children.Add(AtlasLazy("Другие обращения этого Script · условия независимы", () =>
            {
                var other = new StackPanel();
                foreach (var use in nearby)
                {
                    other.Children.Add(AtlasLink(use)); var usePosition = context.Index.At(use.Offset);
                    var useCall = context.Analysis.Calls.FirstOrDefault(c => c.Start == use.Offset);
                    other.Children.Add(AtlasNote(useCall == null ? use.Action : context.Explanation.Explain(useCall, usePosition.Condition)));
                    if (usePosition.Rule != null) other.Children.Add(AtlasNote(context.Index.RuleSummary(usePosition.Rule, context.Explanation), true));
                }
                return other;
            }));
        }
        else panel.Children.Add(AtlasNote(point.Action + "\nТекст старого индекса недоступен: обновите папку MOB.", true));
        if (point.Key.Length > 0) panel.Children.Add(AtlasNote("Ключ " + point.Key + " · контекст " + point.Context, true));
        foreach (var edge in route.Connections.Where(e => !e.Certain && (e.From == point.Id || e.To == point.Id)))
        {
            var other = route.Points.First(p => p.Id == (edge.From == point.Id ? edge.To : edge.From));
            panel.Children.Add(AtlasLink(other, "Возможная зависимость · " + Path.GetFileName(other.Map) + " · " + other.Script + ":" + other.Line + "\n" + edge.Reason));
        }
        return new Border { Child = panel, BorderBrush = MalachiteTheme.Border, BorderThickness = new(0, 0, 0, 1), Padding = new(5, 4, 5, 10), Margin = new(5) };
    }
    FrameworkElement AtlasChildren(QuestMap map, string script, WorkspaceSnapshot snapshot, HashSet<string> ancestors)
    {
        return AtlasLazy("Дочерние Script · " + script, () =>
        {
            var panel = new StackPanel();
            var next = new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase);
            if (!next.Add(script) || next.Count > 32) { panel.Children.Add(AtlasNote("Цикл / глубина цепочки; продолжение смотрите в коде.", true)); return panel; }
            var context = AtlasExplain(map, snapshot);
            foreach (var link in map.Calls.Where(c => c.From.Equals(script, StringComparison.OrdinalIgnoreCase)))
            {
                panel.Children.Add(AtlasLink(new(map.Path + "@" + link.Offset, "", "", "", map.Path, map.Hash, script, link.Offset, link.Line, "", "", false),
                    Path.GetFileName(map.Path) + " · " + script + ":" + link.Line + " → " + link.To));
                var block = context.Analysis.Blocks.FirstOrDefault(b => b.Name.Equals(link.To, StringComparison.OrdinalIgnoreCase));
                if (block != null) panel.Children.Add(AtlasNote(context.Index.StageSummary(block, context.Explanation)));
                panel.Children.Add(AtlasChildren(map, link.To, snapshot, next));
            }
            if (panel.Children.Count == 0) panel.Children.Add(AtlasNote("Прямых вызовов дочерних Script не найдено; внешние события не определены.", true));
            return panel;
        });
    }
}
