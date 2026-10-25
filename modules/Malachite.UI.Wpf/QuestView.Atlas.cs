using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;
public partial class QuestView
{
    string? atlasQuest;
    bool atlasUpdating;
    int atlasRefreshRevision;
    readonly List<string> atlasIndexErrors = [];
    static string AtlasTextHash(string text) => "text:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    static string AtlasQuestTitle(string quest, WorkspaceSnapshot snapshot)
        => MapQuestInventory.Title(quest, snapshot);
    public void RefreshQuestAtlas()
    {
        explainedAnalysis = null;
        if (stopped || QuestOverview == null || atlasUpdating || Current == null) return;
        int revision = ++atlasRefreshRevision;
        AtlasNodes.Children.Clear();
        if (atlasQuest == null || !QuestOverview.IsExpanded) return;
        var snapshot = Host.Workspace.Snapshot();
        AtlasStatus.Text = (folderIndexing ? folderProgress : mapsStatus) + " · текущий снимок БД";
        AtlasNodes.Children.Add(AtlasLazyAsync("Просмотренные карты и пробелы источников", async () => AtlasCoverage((await AtlasSourcesAsync()).Maps)));
        _ = LoadAtlasOverviewAsync(atlasQuest, snapshot, revision);
    }
    void QuestOverview_Expanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, QuestOverview)) RefreshQuestAtlas();
    }
    async Task LoadAtlasOverviewAsync(string quest, WorkspaceSnapshot snapshot, int revision)
    {
        bool includeBriefings = true;
        bool Active() => !stopped && revision == atlasRefreshRevision && QuestOverview.IsExpanded;
        var loading = AtlasNote("Индексируется… подготовка выбранного квеста", true); AtlasNodes.Children.Add(loading);
        try
        {
            var index = await AtlasSourcesAsync();
            if (!Active()) return;
            var maps = index.Maps;
            bool state = quest.StartsWith("@GS:", StringComparison.Ordinal);
            var route = await Task.Run(() => state ? new QuestRoute(index.Keys.GetValueOrDefault(quest[4..]) ?? [], []) : QuestAtlas.Build(quest, maps, snapshot, includeBriefings));
            if (!Active()) return;
            AtlasNodes.Children.Remove(loading);
            AtlasStatus.Text += $" · просмотрено {maps.Length} карт (открытые вкладки заменяют диск)";
            AtlasNodes.Children.Add(new TextBlock { Text = state ? "Общий ключ " + quest[4..] : AtlasQuestTitle(quest, snapshot), FontSize = 20, TextWrapping = TextWrapping.Wrap, Foreground = MalachiteTheme.Accent, Margin = new(5, 10, 5, 8) });
            var uses = route.Points.Where(p => p.Record == null && (state ? p.Key == quest[4..] : QuestAtlas.QuestRoot(p.Key) == quest))
                .DistinctBy(p => p.Id).ToArray();
            string Places(double value)
            {
                var locations = uses.Where(p => p.Write && p.Key == quest && p.WrittenValue == value)
                    .Select(p => Path.GetFileNameWithoutExtension(p.Map) + " стр. " + p.Line).Distinct().ToArray();
                return locations.Length == 0 ? "" : locations[0] + (locations.Length > 1 ? " (ещё " + (locations.Length - 1) + ")" : "");
            }
            string issued = Places(1), completed = Places(2);
            AtlasNodes.Children.Add(AtlasNote((issued.Length > 0 ? "Выдаётся: " + issued : "Выдача в скриптах не найдена") + " · " +
                (completed.Length > 0 ? "Завершается: " + completed : "Завершение в скриптах не найдено") + " · Проверяется на " + uses.Where(p => !p.Write).Select(p => p.Map).Distinct().Count() + " картах"));
            AtlasNodes.Children.Add(AtlasNote(quest + " · 0 — скрыт / не выдан; 1 — выдан; 2 — выполнен; 3 — провален", true));
            if (!state) AddAtlasQuestText(quest, snapshot);
            AddAtlasGroup("Где состояние меняется", uses.Where(p => p.Write), maps, snapshot, route);
            AddAtlasGroup("Кто его проверяет", uses.Where(p => !p.Write), maps, snapshot, route);
            if (route.Points.Any(p => p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase)))
                AddAtlasGroup("Вызовы QuestComplete по точному имени БД", route.Points.Where(p => p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase)), maps, snapshot, route);
            if (includeBriefings) AddAtlasGroup("Через брифинги · действия БД и строки использования", route.Points.Where(p => p.Record != null || p.Key.StartsWith("b.", StringComparison.Ordinal)), maps, snapshot, route);
            AddAtlasDependencies(quest, maps, snapshot, route);
            var starts = route.Points.Where(p => p.CallTarget != null).ToArray();
            var scripts = route.Points.Where(p => p.Record == null).GroupBy(p => p.Map).ToDictionary(g => g.Key,
                g => g.Select(p => p.Script).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var launches = maps.SelectMany(map => map.Calls.Where(c => scripts.TryGetValue(map.Path, out var names) && names.Contains(c.To))
                .Select(call => (Map: map, Call: call))).ToArray();
            if (starts.Length > 0 || launches.Length > 0) AtlasNodes.Children.Add(AtlasLazy("Запуски этапов и дочерние Script", () =>
            {
                var panel = new StackPanel();
                foreach (var point in starts) panel.Children.Add(AtlasPointEntry(point, maps, snapshot, route));
                foreach (var (map, call) in launches)
                {
                    panel.Children.Add(AtlasLink(new(map.Path + "@launch" + call.Offset, quest, "", "", map.Path, map.Hash, call.From, call.Offset, call.Line, "", "", false),
                        Path.GetFileName(map.Path) + " · " + call.From + ":" + call.Line + " → " + call.To + " · прямой вызов, выполнение не доказано"));
                }
                return panel;
            }));
        }
        catch (Exception ex) { if (Active()) loading.Text = "Не удалось построить обзор: " + ex.Message; }
    }
    public void NavigateQuestPoint(QuestPoint point)
    {
        if (point.Record != null) { var result = Host.Workspace.Navigate(point.Record); if (!result.Succeeded) Host.Report(result.Message); return; }
        try
        {
            if (point.Hash?.StartsWith("text:", StringComparison.Ordinal) == true)
            {
                var tab = openTabs.FirstOrDefault(t => (string.IsNullOrEmpty(t.Workspace.SourcePath) ? t.Workspace.Name : t.Workspace.SourcePath).Equals(point.Map, StringComparison.OrdinalIgnoreCase));
                string? text = point.Map.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase) ? CodeEditor.Text : tab == null ? null : tab.Conflict ? tab.State.Code : documentHost?.Documents.Get(tab.Id)?.Text ?? tab.State.Code;
                if (text == null || point.Hash != AtlasTextHash(text)) throw new IOException("Открытый код изменился или вкладка закрыта. Обновите обзор квеста.");
                if (tab != null && tab != activeTab && !SwitchDocument(tab.Id)) return;
            }
            else if (!point.Map.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase))
            {
                if (Host.Files is not IModuleFileBrowser browser) throw new IOException("Хост не поддерживает чтение MOB по пути.");
                EnsureIndexedTarget(point.Map);
                var file = browser.Read(point.Map); var target = Workspace.FromBytes(file.DisplayPath, file.Content.ToArray());
                if (target.Mob?.Hash != point.Hash) throw new IOException("MOB изменён после индексации. Заново выберите папку MOB.");
                if (documentHost == null && !CanLeave()) return;
                LoadOpenedFile(file);
            }
            else throw new IOException("Текущая вкладка заменяет дисковый индекс. Обновите обзор квеста.");
            GoToCode(point.Offset);
        }
        catch (Exception ex) { Host.Report(ex.Message); }
    }
}
