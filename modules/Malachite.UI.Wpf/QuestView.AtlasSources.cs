using System.IO;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    sealed record AtlasSource(string Text, MobDocument? Mob, QuestMap Map);
    sealed record AtlasExplanation(QuestMap Map, Analysis Analysis, CodeContextIndex Index, ScenarioExplanation Explanation);
    readonly Dictionary<string, AtlasSource> atlasOpenSources = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AtlasExplanation> atlasExplanations = new(StringComparer.OrdinalIgnoreCase);
    QuestMap[] atlasLookupSources = [];
    Dictionary<string, QuestPoint[]> atlasKeys = new(StringComparer.Ordinal);
    Dictionary<string, QuestPoint[]> atlasQuestPoints = new(StringComparer.Ordinal);
    string AtlasPath => string.IsNullOrEmpty(Workspace.SourcePath) ? Workspace.Name : Workspace.SourcePath;

    QuestMap[] AtlasSources()
    {
        var maps = indexedMaps.Where(m => m.Quests != null).ToDictionary(m => m.Path, m => m.Quests!, StringComparer.OrdinalIgnoreCase);
        var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Open(string path, string text, Workspace workspace, Analysis? analysis = null)
        {
            openPaths.Add(path);
            if (!atlasOpenSources.TryGetValue(path, out var previous) || previous.Text != text || !ReferenceEquals(previous.Mob, workspace.Mob))
            {
                analysis ??= AnalyzeText(text);
                var map = MapIndexCache.Describe(workspace, analysis, path, AtlasTextHash(text)).Quests!;
                atlasOpenSources[path] = new(text, workspace.Mob, map);
            }
            maps[path] = atlasOpenSources[path].Map;
        }
        foreach (var tab in openTabs.Where(t => t != activeTab))
        {
            string text = tab.Conflict ? tab.State.Code : documentHost?.Documents.Get(tab.Id)?.Text ?? tab.State.Code;
            Open(string.IsNullOrEmpty(tab.Workspace.SourcePath) ? tab.Workspace.Name : tab.Workspace.SourcePath, text, tab.Workspace);
        }
        Open(AtlasPath, CodeEditor.Text, Workspace, Current.Text == CodeEditor.Text ? Current : null);
        foreach (var path in atlasOpenSources.Keys.Where(p => !openPaths.Contains(p)).ToArray()) atlasOpenSources.Remove(path);
        var result = maps.Values.ToArray();
        if (result.Length != atlasLookupSources.Length || result.Where((m, i) => !ReferenceEquals(m, atlasLookupSources[i])).Any())
        {
            atlasLookupSources = result;
            atlasKeys = result.SelectMany(m => m.Points).Where(p => p.Key.Length > 0).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            atlasQuestPoints = result.SelectMany(m => m.Points).Where(p => p.Quest.Length > 0).GroupBy(p => p.Quest, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var path in atlasExplanations.Keys.Where(p => !maps.ContainsKey(p)).ToArray()) atlasExplanations.Remove(path);
        }
        return result;
    }
    AtlasExplanation AtlasExplain(QuestMap map, WorkspaceSnapshot snapshot)
    {
        if (!atlasExplanations.TryGetValue(map.Path, out var cached) || !ReferenceEquals(cached.Map, map))
        {
            var analysis = map.Path.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase) && map.Text == Current.Text ? Current : AnalyzeText(map.Text);
            cached = new(map, analysis, new(analysis), new(analysis, Help, map.Objects, snapshot, Registry));
            atlasExplanations[map.Path] = cached;
        }
        return cached;
    }
    static TextBlock AtlasNote(string text, bool muted = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = muted ? MalachiteTheme.Muted : MalachiteTheme.Text, Margin = new(5, 4, 5, 8) };
    Button AtlasLink(QuestPoint point, string? caption = null)
    {
        var link = new Button { Content = AtlasNote(caption ?? Path.GetFileName(point.Map) + " · " + point.Script + (point.Line > 0 ? " · строка " + point.Line : "")),
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new(0, 3, 0, 0) };
        link.Click += (_, _) => NavigateQuestPoint(point);
        return link;
    }
    static Expander AtlasLazy(string title, Func<FrameworkElement> content)
    {
        var expander = new Expander { Header = title, Margin = new(5, 5, 5, 8), Foreground = MalachiteTheme.Muted };
        expander.Expanded += (_, e) => { if (ReferenceEquals(e.Source, expander) && expander.Content == null) expander.Content = content(); };
        return expander;
    }
    void OpenWholeQuest(QuestPoint point)
    {
        atlasQuest = point.Quest.Length > 0 ? point.Quest : "@GS:" + point.Key;
        Atlas_Click(this, new());
    }
    void AddAtlasContext(StackPanel panel, CodeContextIndex.Position position)
    {
        var calls = Current.Calls.Where(c => QuestStateReference.Supports(c.Name) || c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase))
            .Where(c => position.Call != null ? c.Start >= position.Call.Start && c.End <= position.Call.End : c.Start >= position.Start && c.Start <= position.End).ToArray();
        if (calls.Length == 0) return;
        var maps = AtlasSources();
        var current = maps.First(m => m.Path.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase));
        foreach (var call in calls)
        {
            var point = current.Points.FirstOrDefault(p => p.Offset == call.Start);
            if (point == null) { panel.Children.Add(AtlasNote("Вычисляемый ключ: межкартовая связь не определена.", true)); continue; }
            var uses = (point.Key.Length > 0 ? atlasKeys.GetValueOrDefault(point.Key) : atlasQuestPoints.GetValueOrDefault(point.Quest)) ?? [];
            var elsewhere = uses.Where(p => !p.Map.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            var box = new StackPanel();
            box.Children.Add(AtlasNote($"На других картах: пишут {elsewhere.Count(p => p.Write)} · проверяют {elsewhere.Count(p => !p.Write)}", true));
            box.Children.Add(AtlasNote(point.Key.Length > 0 ? point.Key : "Квест " + point.Quest, true));
            box.Children.Add(AtlasLazy("Места использования · возможная зависимость", () =>
            {
                var links = new StackPanel();
                foreach (var use in elsewhere) links.Children.Add(AtlasLink(use, (use.Write ? "Пишет · " : "Проверяет · ") + Path.GetFileName(use.Map) + " · " + use.Script + ":" + use.Line));
                if (elsewhere.Length == 0) links.Children.Add(AtlasNote("Совпадений в просмотренных картах нет; это не доказательство отсутствия внешних событий.", true));
                return links;
            }));
            var whole = new Button { Content = "Весь квест", HorizontalAlignment = HorizontalAlignment.Left };
            whole.Click += (_, _) => OpenWholeQuest(point); box.Children.Add(whole);
            box.Children.Add(AtlasNote((folderIndexing ? folderProgress : mapsStatus) + $" · просмотрено {maps.Length} карт · ошибок индекса {atlasIndexErrors.Count}. Брифинги БД — в обзоре.", true));
            panel.Children.Add(box);
        }
    }
}
