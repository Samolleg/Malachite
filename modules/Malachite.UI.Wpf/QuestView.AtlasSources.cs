using System.IO;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    sealed record AtlasParsed(QuestMap Map, Analysis Analysis);
    sealed record AtlasSource(string Text, MobDocument? Mob, Task<AtlasParsed> Parsed, Task<QuestMap> Map);
    sealed record AtlasExplanation(QuestMap Map, Analysis Analysis, CodeContextIndex Index, ScenarioExplanation Explanation);
    sealed record AtlasIndex(QuestMap[] Maps, Dictionary<string, QuestPoint[]> Keys, Dictionary<string, QuestPoint[]> Quests);
    readonly Dictionary<string, AtlasSource> atlasOpenSources = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (QuestMap Map, WorkspaceStamp Stamp, Task<AtlasExplanation> Value)> atlasExplanations = new(StringComparer.OrdinalIgnoreCase);
    QuestMap[] atlasLookupSources = [];
    Task<AtlasIndex>? atlasIndex;
    int atlasDataRevision;
    string AtlasPath => string.IsNullOrEmpty(Workspace.SourcePath) ? Workspace.Name : Workspace.SourcePath;

    async Task<AtlasIndex> AtlasSourcesAsync()
    {
        // Capture UI-owned text and MOB identity before scheduling any parsing.
        var maps = indexedMaps.Where(m => m.Quests != null).ToDictionary(m => m.Path, m => Task.FromResult(m.Quests!), StringComparer.OrdinalIgnoreCase);
        var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Open(string path, string text, Workspace workspace, Analysis? analysis = null)
        {
            openPaths.Add(path);
            if (!atlasOpenSources.TryGetValue(path, out var previous) || previous.Text != text || !ReferenceEquals(previous.Mob, workspace.Mob))
            {
                var mob = workspace.Mob;
                var knownAnalysis = analysis;
                var parsed = Task.Run(() =>
                {
                    var source = knownAnalysis ?? AnalyzeText(text);
                    return new AtlasParsed(MapIndexCache.Describe(new Workspace { Mob = mob }, source, path, AtlasTextHash(text)).Quests!, source);
                });
                async Task<QuestMap> Map() => (await parsed).Map;
                atlasOpenSources[path] = new(text, mob, parsed, Map());
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
        foreach (var path in atlasExplanations.Keys.Where(p => !maps.ContainsKey(p)).ToArray()) atlasExplanations.Remove(path);
        var result = await Task.WhenAll(maps.Values);
        if (atlasIndex == null || result.Length != atlasLookupSources.Length || result.Where((m, i) => !ReferenceEquals(m, atlasLookupSources[i])).Any())
        {
            atlasLookupSources = result;
            atlasIndex = Task.Run(() => new AtlasIndex(result,
                result.SelectMany(m => m.Points).Where(p => p.Key.Length > 0).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal),
                result.SelectMany(m => m.Points).Where(p => p.Quest.Length > 0).GroupBy(p => p.Quest, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal)));
        }
        return await atlasIndex;
    }
    Task<AtlasExplanation> AtlasExplainAsync(QuestMap map, WorkspaceSnapshot snapshot)
    {
        if (!atlasExplanations.TryGetValue(map.Path, out var cached) || !ReferenceEquals(cached.Map, map) || cached.Stamp != snapshot.Stamp)
        {
            var knownAnalysis = map.Path.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase) && map.Text == Current.Text ? Current : null;
            if (knownAnalysis == null && atlasOpenSources.TryGetValue(map.Path, out var open) && open.Parsed.IsCompletedSuccessfully &&
                ReferenceEquals(open.Parsed.Result.Map, map)) knownAnalysis = open.Parsed.Result.Analysis;
            cached = (map, snapshot.Stamp, Task.Run(() =>
            {
                var analysis = knownAnalysis ?? AnalyzeText(map.Text);
                return new AtlasExplanation(map, analysis, new(analysis), new(analysis, Help, map.Objects, snapshot, Registry));
            }));
            atlasExplanations[map.Path] = cached;
        }
        return cached.Value;
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
    Expander AtlasLazyAsync(string title, Func<Task<FrameworkElement>> content)
    {
        var expander = new Expander { Header = title, Margin = new(5, 5, 5, 8), Foreground = MalachiteTheme.Muted };
        expander.Expanded += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, expander) || expander.Content != null) return;
            expander.Content = AtlasNote("Индексируется…", true);
            try { expander.Content = await content(); }
            catch (Exception ex) { expander.Content = AtlasNote("Не удалось построить обзор: " + ex.Message, true); }
        };
        return expander;
    }
    void OpenWholeQuest(QuestPoint point)
    {
        atlasQuest = point.Quest.Length > 0 ? point.Quest : "@GS:" + point.Key;
        QuestOverview.Visibility = Visibility.Visible;
        if (QuestOverview.IsExpanded) RefreshQuestAtlas();
        else QuestOverview.IsExpanded = true;
    }
    async void AddAtlasContext(StackPanel panel, CodeContextIndex.Position position)
    {
        var calls = Current.Calls.Where(c => QuestStateReference.Supports(c.Name) || c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase))
            .Where(c => position.Call != null ? c.Start >= position.Call.Start && c.End <= position.Call.End : c.Start >= position.Start && c.Start <= position.End).ToArray();
        if (calls.Length == 0) return;
        var analysis = Current; var path = AtlasPath; int revision = atlasDataRevision;
        var loading = AtlasNote("Межкартовые связи: индексируется…", true); panel.Children.Add(loading);
        AtlasIndex index;
        try { index = await AtlasSourcesAsync(); }
        catch (Exception ex) { loading.Text = "Не удалось прочитать связи: " + ex.Message; return; }
        if (stopped || Current != analysis || path != AtlasPath || revision != atlasDataRevision) return;
        panel.Children.Remove(loading);
        var maps = index.Maps;
        var current = maps.First(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        foreach (var call in calls)
        {
            var point = current.Points.FirstOrDefault(p => p.Offset == call.Start);
            if (point == null) { panel.Children.Add(AtlasNote("Вычисляемый ключ: межкартовая связь не определена.", true)); continue; }
            var uses = (point.Key.Length > 0 ? index.Keys.GetValueOrDefault(point.Key) : index.Quests.GetValueOrDefault(point.Quest)) ?? [];
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
