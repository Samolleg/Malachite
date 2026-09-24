using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuestStudio;
public partial class QuestView
{
    bool documentOpen;
    bool homeSortByDate, homeSortDescending;
    readonly Dictionary<string, HomeMap> homeRows = new(StringComparer.OrdinalIgnoreCase);
    public sealed record HomeMap(string Path, DateTime? ModifiedUtc = null)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Modified => ModifiedUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "Недоступно";
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
    void SetHomeSort(bool date)
    {
        homeSortDescending = date == homeSortByDate ? !homeSortDescending : date;
        homeSortByDate = date;
        UpdateHomeMaps();
        if (HomeMaps.SelectedItem != null) HomeMaps.ScrollIntoView(HomeMaps.SelectedItem);
    }
    void UpdateHomeMaps()
    {
        if (HomeMaps == null) return;
        string query = HomeSearch.Text.Trim();
        var rows = folderFiles.Where(p => System.IO.Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                if (!homeRows.TryGetValue(p, out var row)) homeRows[p] = row = HomeMap.ReadMetadata(p);
                return row;
            });
        var items = (homeSortByDate
            ? rows.OrderBy(r => r.ModifiedUtc == null)
                .ThenBy(r => homeSortDescending ? -(r.ModifiedUtc?.Ticks ?? 0) : r.ModifiedUtc?.Ticks ?? 0)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : homeSortDescending ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        HomeSortName.Content = "По имени" + (!homeSortByDate ? homeSortDescending ? " ↓" : " ↑" : "");
        HomeSortDate.Content = "По дате" + (homeSortByDate ? homeSortDescending ? " ↓" : " ↑" : "");
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
            : items.Length == 0 ? "По этому имени карты не найдены." : $"Показано карт: {items.Length} из {folderFiles.Count} · двойной щелчок или Enter — открыть";
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
