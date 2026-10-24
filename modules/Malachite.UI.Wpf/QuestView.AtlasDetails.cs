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
        var record = QuestAtlas.QuestRecord(quest, snapshot);
        if (record == null) { AtlasNodes.Children.Add(AtlasNote("Название БД не назначено: нет единственной записи с точным именем и подходящей зоной QDB. Показан буквальный GS-ключ.", true)); return; }
        var texts = record.Texts.Where(t => t.ResourceKey.Equals("quest " + record.Name, StringComparison.Ordinal)).ToArray();
        if (texts.Length != 1) { AtlasNodes.Children.Add(AtlasNote("Название и подпункты: текст QUEST отсутствует или неоднозначен в снимке БД.", true)); return; }
        var resource = texts[0];
        if (resource.Modified) AtlasNodes.Children.Add(AtlasNote("Показан несохранённый текст QUEST из текущего снимка БД.", true));
        var sections = Regex.Matches(resource.Description, @"(?im)^[\t ]*#subobj[\t ]+(\S+)[\t ]*\r?$").Cast<Match>().ToArray();
        AtlasNodes.Children.Add(AtlasLazy("Описание и подпункты из БД", () =>
        {
            var panel = new StackPanel();
            foreach (var section in sections)
            {
                var tail = resource.Description[(section.Index + section.Length)..].TrimStart('\r', '\n');
                string title = tail.Split('\r', '\n')[0].Trim();
                bool duplicate = sections.Count(s => s.Groups[1].Value == section.Groups[1].Value) != 1;
                panel.Children.Add(AtlasNote("Подпункт " + section.Groups[1].Value + ": " + (duplicate ? "неоднозначный #subobj" : title)));
            }
            panel.Children.Add(AtlasNote(resource.Description)); return panel;
        }));
    }
    void AddAtlasGroup(string title, IEnumerable<QuestPoint> points, QuestMap[] maps, WorkspaceSnapshot snapshot, QuestRoute route)
    {
        var rows = points.OrderBy(p => p.Record == null).ThenBy(p => p.Map).ThenBy(p => p.Line).ToArray();
        if (rows.Length == 0) return;
        AtlasNodes.Children.Add(AtlasLazy(title + " · " + rows.Length, () =>
        {
            var panel = new StackPanel();
            foreach (var part in rows.GroupBy(p => p.Record != null ? "Действия БД" : p.Key.StartsWith("b.", StringComparison.Ordinal) ? "Состояние брифинга" : QuestAtlas.Part(p.Key)))
                panel.Children.Add(AtlasLazy(part.Key + " · " + part.Count(), () =>
                {
                    var entries = new StackPanel();
                    foreach (var point in part) entries.Children.Add(AtlasPointEntry(point, maps, snapshot, route));
                    return entries;
                }));
            return panel;
        }));
    }
    FrameworkElement AtlasPointEntry(QuestPoint point, QuestMap[] maps, WorkspaceSnapshot snapshot, QuestRoute route)
    {
        var entry = AtlasLazyAsync(ScenarioExplanation.PointTitle(point), () => AtlasPointViewAsync(point, maps, snapshot, route));
        entry.ToolTip = point.Key + (point.Context == "0" ? "" : " · для игрока " + point.Context);
        return entry;
    }
    async Task<FrameworkElement> AtlasPointViewAsync(QuestPoint point, QuestMap[] maps, WorkspaceSnapshot snapshot, QuestRoute route)
    {
        var panel = new StackPanel();
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
            var context = await AtlasExplainAsync(map, snapshot);
            var position = context.Index.At(point.Offset);
            var text = await Task.Run(() =>
            {
                lock (context)
                {
                    var call = context.Analysis.Calls.FirstOrDefault(c => c.Start == point.Offset);
                    return (Explanation: call == null ? point.Action : context.Explanation.Explain(call, position.Condition),
                        Tooltip: call == null ? point.Key : context.Explanation.Tooltip(call, position.Condition),
                        Summary: position.Block == null ? [] : position.Rule == null ? context.Index.StageLines(position.Block, context.Explanation) : context.Index.RuleLines(position.Rule, context.Explanation));
                }
            });
            if (position.Block != null)
            {
                panel.Children.Add(ScenarioView(text.Summary));
                if (map.Calls.Any(c => c.From.Equals(position.Block.Name, StringComparison.OrdinalIgnoreCase)))
                    panel.Children.Add(AtlasChildren(map, position.Block.Name, snapshot, new(StringComparer.OrdinalIgnoreCase)));
            }
            else { var sentence = AtlasNote(text.Explanation); sentence.ToolTip = text.Tooltip; panel.Children.Add(sentence); }
            // Other independent checks in the same Script can finish a neighbouring quest.
            var nearby = map.Points.Where(p => p.Script.Equals(point.Script, StringComparison.OrdinalIgnoreCase) && p.Id != point.Id && p.Key == point.Key).ToArray();
            if (nearby.Length > 0) panel.Children.Add(AtlasLazy("Другие обращения этого Script", () =>
            {
                var other = new StackPanel();
                foreach (var use in nearby)
                {
                    other.Children.Add(AtlasLazyAsync("Строка " + use.Line + " · " + use.Script, async () =>
                    {
                        var detail = new StackPanel(); detail.Children.Add(AtlasLink(use));
                        var description = await Task.Run(() =>
                        {
                            lock (context)
                            {
                                var usePosition = context.Index.At(use.Offset);
                                var useCall = context.Analysis.Calls.FirstOrDefault(c => c.Start == use.Offset);
                                return usePosition.Rule == null
                                    ? new[] { new ScenarioLine(useCall == null ? use.Action : context.Explanation.Explain(useCall, usePosition.Condition),
                                        useCall == null ? use.Key : context.Explanation.Tooltip(useCall, usePosition.Condition)) }
                                    : context.Index.RuleLines(usePosition.Rule, context.Explanation);
                            }
                        });
                        detail.Children.Add(ScenarioView(description)); return detail;
                    }));
                }
                return other;
            }));
        }
        else panel.Children.Add(AtlasNote(point.Action + "\nТекст старого индекса недоступен: обновите папку MOB.", true));
        panel.Children.Add(AtlasLink(point, point.Record == null ? "Открыть в коде" : "Открыть в БД"));
        foreach (var edge in route.Connections.Where(e => !e.Certain && (e.From == point.Id || e.To == point.Id)))
        {
            var other = route.Points.First(p => p.Id == (edge.From == point.Id ? edge.To : edge.From));
            panel.Children.Add(AtlasLink(other, "Возможная зависимость · " + Path.GetFileName(other.Map) + " · " + other.Script + ":" + other.Line + "\n" + edge.Reason));
        }
        return new Border { Child = panel, BorderBrush = MalachiteTheme.Border, BorderThickness = new(0, 0, 0, 1), Padding = new(5, 4, 5, 10), Margin = new(5) };
    }
    void AddAtlasDependencies(string quest, QuestMap[] maps, WorkspaceSnapshot snapshot, QuestRoute route)
    {
        var links = maps.SelectMany(m => m.Dependencies).Where(d => d.Check.Quest == quest || d.Action.Quest == quest).ToArray();
        if (links.Length == 0) return;
        AtlasNodes.Children.Add(AtlasLazy("Зависимости по условиям и действиям · " + links.Length, () =>
        {
            var panel = new StackPanel();
            foreach (var group in links.GroupBy(d => (Incoming: d.Action.Quest == quest, Other: d.Action.Quest == quest ? d.Check.Quest : d.Action.Quest)))
            {
                panel.Children.Add(AtlasLazy((group.Key.Incoming ? "Зависит от: " : "Влияет на: ") + AtlasQuestTitle(group.Key.Other, snapshot) + " · " + group.Key.Other, () =>
                {
                    var proof = new StackPanel();
                    foreach (var link in group)
                    {
                        proof.Children.Add(AtlasNote("Условие → " + link.Branch + ". Это связь в коде; достижимость и остальные условия не установлены.", true));
                        proof.Children.Add(AtlasLink(link.Check, Path.GetFileName(link.Check.Map) + ":" + link.Check.Line + " · " + link.Check.Action));
                        foreach (var call in link.Calls)
                            proof.Children.Add(AtlasLink(link.Check with { Offset = call.Offset, Line = call.Line, Script = call.From }, "Строка " + call.Line + " · " + call.From + " → " + call.To));
                        proof.Children.Add(AtlasLink(link.Action, Path.GetFileName(link.Action.Map) + ":" + link.Action.Line + " · " + link.Action.Action));
                        proof.Children.Add(AtlasPointEntry(link.Action, maps, snapshot, route));
                    }
                    var open = new Button { Content = "Обзор " + group.Key.Other };
                    open.Click += (_, _) => OpenWholeQuest(group.Key.Incoming ? group.First().Check : group.First().Action);
                    proof.Children.Add(open); return proof;
                }));
            }
            return panel;
        }));
    }
    FrameworkElement AtlasChildren(QuestMap map, string script, WorkspaceSnapshot snapshot, HashSet<string> ancestors)
    {
        return AtlasLazyAsync("Дочерние Script · " + script, async () =>
        {
            var panel = new StackPanel();
            var next = new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase);
            if (!next.Add(script) || next.Count > 32) { panel.Children.Add(AtlasNote("Цикл / глубина цепочки; продолжение смотрите в коде.", true)); return panel; }
            var context = await AtlasExplainAsync(map, snapshot);
            foreach (var link in map.Calls.Where(c => c.From.Equals(script, StringComparison.OrdinalIgnoreCase)))
            {
                panel.Children.Add(AtlasLink(new(map.Path + "@" + link.Offset, "", "", "", map.Path, map.Hash, script, link.Offset, link.Line, "", "", false),
                    Path.GetFileName(map.Path) + " · " + script + ":" + link.Line + " → " + link.To));
                var block = context.Analysis.Blocks.FirstOrDefault(b => b.Name.Equals(link.To, StringComparison.OrdinalIgnoreCase));
                if (block != null) panel.Children.Add(AtlasLazyAsync("Объяснение этапа · " + block.Name, async () =>
                {
                    ScenarioLine[] lines = await Task.Run(() => { lock (context) return context.Index.StageLines(block, context.Explanation); });
                    return ScenarioView(lines);
                }));
                if (map.Calls.Any(c => c.From.Equals(link.To, StringComparison.OrdinalIgnoreCase)))
                    panel.Children.Add(AtlasChildren(map, link.To, snapshot, next));
            }
            return panel;
        });
    }
}
