using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;
public partial class QuestView
{
    void Atlas_LayoutUpdated(object? sender, EventArgs e)
    {
        if (stopped || QuestAtlasPanel.Visibility != Visibility.Visible || QuestAtlasPanel.Parent is not Grid area) return;
        double available = Math.Max(0, Surface.RowDefinitions[1].ActualHeight - area.Margin.Top - area.Margin.Bottom - area.RowDefinitions[0].ActualHeight);
        if (double.IsNaN(QuestAtlasPanel.Height) || Math.Abs(QuestAtlasPanel.Height - available) > .5) QuestAtlasPanel.Height = available;
    }
    string? atlasQuest;
    bool atlasUpdating;
    int atlasRefreshRevision;
    readonly List<string> atlasIndexErrors = [];
    static string AtlasTextHash(string text) => "text:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    static string AtlasQuestTitle(string quest, WorkspaceSnapshot snapshot)
        => MapQuestInventory.Title(quest, snapshot);
    public void RefreshQuestAtlas()
    {
        // Index/DB changes must also refresh a stationary inspector in the code view.
        explainedAnalysis = null;
        if (stopped || QuestAtlasPanel == null || atlasUpdating || Current == null) return;
        int revision = ++atlasRefreshRevision;
        if (QuestAtlasPanel.Visibility != Visibility.Visible) { ExplainAt(CodeEditor.CaretIndex); return; }
        atlasUpdating = true;
        try
        {
            var snapshot = Host.Workspace.Snapshot();
            var items = MapQuestInventory.Read(Current, snapshot, AtlasPath, AtlasIndirect.IsChecked == true);
            var inventory = new ListCollectionView(items.Where(q => q.Category != 1).ToArray());
            var checks = new ListCollectionView(items.Where(q => q.Category == 1).ToArray());
            checks.Filter = AtlasMatchesSearch;
            AtlasOtherChecks.ItemsSource = checks;
            AtlasOtherChecks.SelectedItem = AtlasOtherChecks.Items.OfType<MapQuestItem>().FirstOrDefault(q => q.Key == atlasQuest);
            AtlasOtherExpander.Header = "Проверки квестов других карт · " + items.Count(q => q.Category == 1);
            AtlasOtherExpander.Visibility = items.Any(q => q.Category == 1) ? Visibility.Visible : Visibility.Collapsed;
            inventory.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MapQuestItem.Group)));
            inventory.Filter = AtlasMatchesSearch;
            AtlasQuests.ItemsSource = inventory;
            AtlasQuests.SelectedItem = AtlasQuests.Items.OfType<MapQuestItem>().FirstOrDefault(q => q.Key == atlasQuest);
            AtlasNodes.Children.Clear();
            AtlasStatus.Text = (folderIndexing ? "Индексируется… " + folderProgress : mapsStatus) + $" · индекс диска: {indexedMaps.Count} карт · ошибок индекса {atlasIndexErrors.Count}" +
                (snapshot.Modified ? " · снимок БД: есть несохранённые изменения" : " · текущий снимок БД");
            AtlasStatus.ToolTip = "Индекс — снимок диска на момент чтения. Открытые вкладки заменяют его текущим текстом. Обновить диск: Папка MOB…";
            AtlasNodes.Children.Add(AtlasLazyAsync("Просмотренные карты и пробелы источников", async () => AtlasCoverage((await AtlasSourcesAsync()).Maps)));
            if (atlasQuest == null) { AtlasNodes.Children.Add(AtlasNote(!items.Any(q => q.Category != 1) ? "На этой карте квесты не найдены. Включите квесты через брифинги или откройте обзор из GS-строки Инспектора." : "Выберите квест слева. У каждой строки указана причина; точные обращения — в подсказке и обзоре.")); return; }
            _ = LoadAtlasOverviewAsync(atlasQuest, snapshot, revision);
        }
        finally { atlasUpdating = false; }
    }
    async Task LoadAtlasOverviewAsync(string quest, WorkspaceSnapshot snapshot, int revision)
    {
        bool includeBriefings = AtlasIndirect.IsChecked == true;
        bool Active() => !stopped && revision == atlasRefreshRevision && QuestAtlasPanel.Visibility == Visibility.Visible;
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
            AtlasNodes.Children.Add(AtlasNote(quest + " · 0 — скрыт; 1 — доступен; 2 — выполнен; 3 — провален", true));
            if (!state) AddAtlasQuestText(quest, snapshot);
            AddAtlasGroup("Где состояние меняется", uses.Where(p => p.Write), maps, snapshot, route);
            AddAtlasGroup("Кто его проверяет", uses.Where(p => !p.Write), maps, snapshot, route);
            if (route.Points.Any(p => p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase)))
                AddAtlasGroup("Вызовы QuestComplete по точному имени БД", route.Points.Where(p => p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase)), maps, snapshot, route);
            if (includeBriefings) AddAtlasGroup("Через брифинги · действия БД и строки использования", route.Points.Where(p => p.Record != null || p.Key.StartsWith("b.", StringComparison.Ordinal)), maps, snapshot, route);
            AddAtlasDependencies(quest, maps, snapshot, route);
            AtlasNodes.Children.Add(AtlasLazy("Запуски этапов и дочерние Script", () =>
            {
                var panel = new StackPanel();
                foreach (var point in route.Points.Where(p => p.CallTarget != null)) panel.Children.Add(AtlasPointEntry(point, maps, snapshot, route));
                foreach (var map in maps)
                foreach (var script in route.Points.Where(p => p.Map == map.Path && p.Record == null).Select(p => p.Script).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var call in map.Calls.Where(c => c.To.Equals(script, StringComparison.OrdinalIgnoreCase)))
                        panel.Children.Add(AtlasLink(new(map.Path + "@launch" + call.Offset, quest, "", "", map.Path, map.Hash, call.From, call.Offset, call.Line, "", "", false),
                            Path.GetFileName(map.Path) + " · " + call.From + ":" + call.Line + " → " + script + " · прямой вызов, выполнение не доказано"));
                    if (!map.Calls.Any(c => c.To.Equals(script, StringComparison.OrdinalIgnoreCase)) && !script.Equals("WorldScript", StringComparison.OrdinalIgnoreCase))
                        panel.Children.Add(AtlasNote(Path.GetFileName(map.Path) + " · " + script + ": источник запуска не найден; возможное внешнее событие неизвестно.", true));
                }
                return panel;
            }));
        }
        catch (Exception ex) { if (Active()) loading.Text = "Не удалось построить обзор: " + ex.Message; }
    }
    bool AtlasMatchesSearch(object item) => item is MapQuestItem q &&
        (q.Title.Contains(AtlasSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase) || q.Key.Contains(AtlasSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    void AtlasSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (AtlasQuests?.ItemsSource is not ListCollectionView inventory) return;
        atlasUpdating = true;
        try
        {
            inventory.Refresh();
            if (AtlasOtherChecks.ItemsSource is ListCollectionView checks) checks.Refresh();
            AtlasQuests.SelectedItem = AtlasQuests.Items.OfType<MapQuestItem>().FirstOrDefault(q => q.Key == atlasQuest);
            AtlasOtherChecks.SelectedItem = AtlasOtherChecks.Items.OfType<MapQuestItem>().FirstOrDefault(q => q.Key == atlasQuest);
        }
        finally { atlasUpdating = false; }
    }
    void Atlas_Click(object sender, RoutedEventArgs e) { atlasQuest = null; AtlasOtherExpander.IsExpanded = false; QuestAtlasPanel.Visibility = Visibility.Visible; CodeButton.BorderBrush = (Brush)FindResource("Line"); RefreshQuestAtlas(); }
    void AtlasClose_Click(object sender, RoutedEventArgs e) => Code_Click(sender, e);
    void AtlasSelection_Changed(object sender, SelectionChangedEventArgs e) { if (!atlasUpdating && sender is ListBox { SelectedItem: MapQuestItem quest }) { atlasQuest = quest.Key; RefreshQuestAtlas(); } }
    void AtlasScope_Changed(object sender, RoutedEventArgs e) => RefreshQuestAtlas();
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
