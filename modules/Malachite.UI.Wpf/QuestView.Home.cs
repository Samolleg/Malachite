using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuestStudio;
public partial class QuestView
{
    bool documentOpen;
    bool homeSortByDate, homeSortDescending, homeUpdating;
    string homeFilter = "all";
    readonly MapCatalogStore catalog;
    readonly Dictionary<string, string> noteDrafts = new(StringComparer.OrdinalIgnoreCase);
    string? homeDetailsPath;
    bool homeDetailsUpdating;
    readonly Dictionary<string, HomeMap> homeRows = new(StringComparer.OrdinalIgnoreCase);
    public sealed record HomeMap(string Path, DateTime? ModifiedUtc = null)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Modified => ModifiedUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "Недоступно";
        public bool Favorite { get; init; }
        public string FavoriteMark => Favorite ? "★" : "";
        public string Note { get; init; } = "";
        public DateTime? LastOpenedUtc { get; init; }
        public int? ScriptCount { get; init; }
        public int? ObjectCount { get; init; }
        public string Facts => ScriptCount is { } count ? CountLabel(count, "скрипт", "скрипта", "скриптов") : "Нет данных индекса";
        public static HomeMap ReadMetadata(string path)
        {
            // Metadata only, for paths already supplied by the host. MOB contents still use authorized Read.
            try { var file = new System.IO.FileInfo(path); return new(path, file.Exists ? file.LastWriteTimeUtc : null); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return new(path); }
        }
    }
    void ShowHome(bool show)
    {
        HomePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.BorderBrush = show ? MalachiteTheme.Accent : (System.Windows.Media.Brush)FindResource("Line");
        HomeButton.BorderThickness = new Thickness(show ? 2 : 1);
        EditorSurface.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        CopiesButton.IsEnabled = documentOpen;
        SaveChangesButton.IsEnabled = documentOpen;
        HomeResume.Visibility = documentOpen ? Visibility.Visible : Visibility.Collapsed;
        if (show) { homeRows.Clear(); UpdateHomeMaps(); HomeSearch.Focus(); }
    }
    void Home_Click(object sender, RoutedEventArgs e) => ShowHome(true);
    void HomeResume_Click(object sender, RoutedEventArgs e) => ShowHome(false);
    void HomeSearch_Changed(object sender, TextChangedEventArgs e) => UpdateHomeMaps();
    void HomeSortName_Click(object sender, RoutedEventArgs e) => SetHomeSort(false);
    void HomeSortDate_Click(object sender, RoutedEventArgs e) => SetHomeSort(true);
    void HomeFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filter }) { homeFilter = filter; UpdateHomeMaps(); }
    }
    void SetHomeSort(bool date)
    {
        homeSortDescending = date == homeSortByDate ? !homeSortDescending : date;
        homeSortByDate = date;
        UpdateHomeMaps();
        if (HomeMaps.SelectedItem != null) HomeMaps.ScrollIntoView(HomeMaps.SelectedItem);
    }
    void UpdateHomeMaps()
    {
        if (HomeMaps == null || homeUpdating) return;
        homeUpdating = true;
        try
        {
        string query = HomeSearch.Text.Trim();
        var indexed = indexedMaps.GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var rows = folderFiles.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                if (!homeRows.TryGetValue(p, out var row)) homeRows[p] = row = HomeMap.ReadMetadata(p);
                var personal = catalog.Get(p); indexed.TryGetValue(p, out var index);
                return row with { Favorite = personal.Favorite, Note = personal.Note, LastOpenedUtc = personal.LastOpenedUtc,
                    ScriptCount = p.Equals(Workspace.SourcePath, StringComparison.OrdinalIgnoreCase) && documentOpen ? Current?.Blocks.Count(b => !b.World) : index?.ScriptCount,
                    ObjectCount = index?.ObjectCount };
            }).Where(r => (r.Name + " " + r.Note).Contains(query, StringComparison.OrdinalIgnoreCase)
                && (homeFilter != "favorites" || r.Favorite) && (homeFilter != "recent" || r.LastOpenedUtc != null));
        var items = (homeFilter == "recent" ? rows.OrderByDescending(r => r.LastOpenedUtc) : homeSortByDate
            ? rows.OrderBy(r => r.ModifiedUtc == null)
                .ThenBy(r => homeSortDescending ? -(r.ModifiedUtc?.Ticks ?? 0) : r.ModifiedUtc?.Ticks ?? 0)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : homeSortDescending ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        HomeSortName.Content = "По имени" + (!homeSortByDate ? homeSortDescending ? " ↓" : " ↑" : "");
        HomeSortDate.Content = "По дате" + (homeSortByDate ? homeSortDescending ? " ↓" : " ↑" : "");
        HomeSortName.IsEnabled = HomeSortDate.IsEnabled = homeFilter != "recent";
        foreach (var button in new[] { HomeAll, HomeRecent, HomeFavorites })
            button.BorderBrush = (string)button.Tag == homeFilter ? MalachiteTheme.Accent : (System.Windows.Media.Brush)FindResource("Line");
        HomeSortName.BorderBrush = !homeSortByDate ? MalachiteTheme.Accent : (System.Windows.Media.Brush)FindResource("Line");
        HomeSortDate.BorderBrush = homeSortByDate ? MalachiteTheme.Accent : (System.Windows.Media.Brush)FindResource("Line");
        if (!HomeMaps.Items.OfType<HomeMap>().SequenceEqual(items))
        {
            var selected = HomeMaps.SelectedItem as HomeMap;
            HomeMaps.ItemsSource = items;
            HomeMaps.SelectedItem = items.FirstOrDefault(i => i.Path == selected?.Path) ?? items.FirstOrDefault();
        }
        HomeOpen.IsEnabled = items.Length > 0;
        HomeStatus.Text = folderIndexing ? folderProgress : folderFiles.Count == 0
            ? "Карты пока не найдены. Откройте БД с папкой Maps или выберите файл / папку MOB."
            : items.Length == 0 ? homeFilter == "recent" ? "Недавно открытых карт пока нет." : homeFilter == "favorites" ? "Избранных карт не найдено." : "Карты не найдены."
            : $"Показано карт: {items.Length} из {folderFiles.Count} · двойной щелчок или Enter — открыть";
        FillHomeDetails();
        }
        finally { homeUpdating = false; }
    }
    void HomeSelection_Changed(object sender, SelectionChangedEventArgs e) { if (!homeUpdating) FillHomeDetails(); }
    void FillHomeDetails()
    {
        if (HomeMapDetails == null) return;
        var map = HomeMaps.SelectedItem as HomeMap;
        if (homeDetailsPath != null && homeDetailsPath != map?.Path && noteDrafts.TryGetValue(homeDetailsPath, out var draft))
        {
            try { catalog.Update(homeDetailsPath, old => old with { Note = draft.Trim() }); noteDrafts.Remove(homeDetailsPath); }
            catch (Exception ex) { Host.Report("Заметка осталась в поле этого сеанса: " + ex.Message); }
        }
        HomeMapDetails.IsEnabled = map != null;
        HomeMapName.Text = map?.Name ?? "Выберите карту";
        HomeMapPath.Text = map?.Path ?? "";
        HomeMapModified.Text = map?.Modified ?? "—";
        HomeMapScripts.Text = map?.ScriptCount?.ToString() ?? "Нет данных индекса";
        HomeMapObjects.Text = map?.ObjectCount?.ToString() ?? "Нет данных индекса";
        homeDetailsPath = map?.Path;
        homeDetailsUpdating = true;
        HomeNote.Text = map == null ? "" : noteDrafts.GetValueOrDefault(map.Path, map.Note);
        homeDetailsUpdating = false;
        HomeFavorite.Content = map?.Favorite == true ? "★ В избранном" : "☆ В избранное";
        HomeFavorite.BorderBrush = map?.Favorite == true ? MalachiteTheme.Accent : (System.Windows.Media.Brush)FindResource("Line");
    }
    void HomeFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (HomeMaps.SelectedItem is not HomeMap map) return;
        try { catalog.Update(map.Path, old => old with { Favorite = !old.Favorite }); UpdateHomeMaps(); }
        catch (Exception ex) { SetStatus(ex.Message); Host.Report(ex.Message); }
    }
    void HomeNoteSave_Click(object sender, RoutedEventArgs e)
    {
        if (homeDetailsPath is not { } path) return;
        try { catalog.Update(path, old => old with { Note = HomeNote.Text.Trim() }); noteDrafts.Remove(path); UpdateHomeMaps(); SetStatus("Заметка к карте сохранена."); }
        catch (Exception ex) { SetStatus(ex.Message); Host.Report(ex.Message); }
    }
    void HomeNote_Changed(object sender, TextChangedEventArgs e)
    {
        if (!homeDetailsUpdating && homeDetailsPath != null) noteDrafts[homeDetailsPath] = HomeNote.Text;
    }
    void HomeNote_LostFocus(object sender, RoutedEventArgs e)
    {
        if (homeDetailsPath != null && noteDrafts.ContainsKey(homeDetailsPath)) HomeNoteSave_Click(sender, e);
    }
    void RememberOpenedMap(string path)
    {
        if (System.IO.Path.GetExtension(path).Equals(".mob", StringComparison.OrdinalIgnoreCase))
            try { catalog.Update(path, old => old with { LastOpenedUtc = DateTime.UtcNow }); }
            catch (Exception ex) { Host.Report("Не удалось запомнить карту: " + ex.Message); }
    }
    void HomeMaps_Open(object sender, RoutedEventArgs e)
    {
        if (HomeMaps.SelectedItem is HomeMap map) OpenPath(map.Path);
    }
    void HomeMaps_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { HomeMaps_Open(sender, e); e.Handled = true; }
    }
}
