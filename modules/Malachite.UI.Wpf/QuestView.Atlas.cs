using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;

namespace QuestStudio;
public partial class QuestView
{
    void Atlas_LayoutUpdated(object? sender, EventArgs e)
    {
        if (stopped || QuestAtlasPanel.Visibility != Visibility.Visible || QuestAtlasPanel.Parent is not Grid area) return;
        // The code view has minimum row heights. The atlas overlay must fit the
        // actual workspace even when those underlying rows exceed a small window.
        // Reserve the full height even when there are no quest rows; MaxHeight alone
        // lets the overlay shrink and exposes the previous view underneath.
        double available = Math.Max(0, Surface.RowDefinitions[1].ActualHeight - area.Margin.Top - area.Margin.Bottom - area.RowDefinitions[0].ActualHeight);
        if (double.IsNaN(QuestAtlasPanel.Height) || Math.Abs(QuestAtlasPanel.Height - available) > .5)
            QuestAtlasPanel.Height = available;
    }
    string? atlasQuest;
    bool atlasUpdating;
    readonly List<string> atlasIndexErrors = [];
    static string AtlasTextHash(string text) => "text:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    public void RefreshQuestAtlas()
    {
        if (stopped || QuestAtlasPanel == null || QuestAtlasPanel.Visibility != Visibility.Visible || atlasUpdating || Current == null) return;
        atlasUpdating = true;
        try
        {
            var snapshot = Host.Workspace.Snapshot();
            var analysis = Current.Text == CodeEditor.Text ? Current : ScriptAnalyzer.Analyze(CodeEditor.Text, Registry);
            string path = string.IsNullOrEmpty(Workspace.SourcePath) ? Workspace.Name : Workspace.SourcePath;
            var current = QuestAtlas.Read(analysis, path, AtlasTextHash(analysis.Text));
            var maps = indexedMaps.Where(m => !m.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && m.Quests != null).Select(m => m.Quests!).Append(current).ToArray();
            var localQuests = current.Points.Where(p => p.Quest.Length > 0).Select(p => p.Quest).ToHashSet(StringComparer.Ordinal);
            var scopeMaps = new[] { current };
            var directQuests = scopeMaps.SelectMany(m => m.Points).Where(p => p.Quest.Length > 0).Select(p => p.Quest).ToHashSet(StringComparer.Ordinal);
            foreach (var briefing in snapshot.Records.Where(r => r.Reference.TableKey == "quests.briefings" && scopeMaps.Any(m => m.Briefings.Any(b => b.Name == r.Name))))
            foreach (var field in briefing.Fields.Where(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList"))
            foreach (var key in field.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (QuestAtlas.QuestKey(key) is { } name) localQuests.Add(name);
            var quests = localQuests.Where(q => q.Length > 0 && (AtlasIndirect.IsChecked == true || directQuests.Contains(q))).Distinct().OrderBy(q => q).ToArray();
            var items = quests.OrderBy(q => !directQuests.Contains(q)).ThenBy(q => q).Select(q => new AtlasQuest(q, snapshot.Records.FirstOrDefault(r => r.Reference.TableKey == "quests.quests" && r.Name == q)?.Texts.FirstOrDefault()?.Title ?? q, !directQuests.Contains(q))).ToArray();
            // Selecting a quest updates its details, not the inventory or its scroll position.
            if (!AtlasQuests.Items.OfType<AtlasQuest>().SequenceEqual(items))
            {
                var inventory = new ListCollectionView(items);
                inventory.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AtlasQuest.Group)));
                AtlasQuests.ItemsSource = inventory;
            }
            if (atlasQuest == null || !quests.Contains(atlasQuest)) atlasQuest = items.FirstOrDefault()?.Key;
            AtlasQuests.SelectedItem = AtlasQuests.Items.OfType<AtlasQuest>().FirstOrDefault(q => q.Key == atlasQuest);
            AtlasNodes.Children.Clear();
            AtlasStatus.Text = $"Текущая карта · {quests.Length} квестов · в индексе {maps.Length} MOB";
            if (folderIndexing) AtlasStatus.Text = folderProgress;
            AtlasStatus.ToolTip = mapsStatus + "\nИндекс других карт — снимок. Открытый документ учитывает несохранённый код.\nСтрелки — возможные связи, не порядок прохождения. Карточки сгруппированы по MOB и строкам. Сохранённая игра не загружена.\nКосвенные квесты связаны через эффекты брифингов в БД. Maps находится автоматически рядом с БД. Другую папку можно выбрать через «Папка MOB…».";
            if (atlasIndexErrors.Count > 0)
            {
                AtlasStatus.Text += $" · Не прочитано: {atlasIndexErrors.Count}";
                AtlasStatus.ToolTip += "\n" + string.Join("\n", atlasIndexErrors);
            }
            if (atlasQuest == null)
            {
                AtlasNodes.Children.Add(new TextBlock {
                    Text = "На этой карте квесты не найдены по выбранному фильтру.",
                    TextWrapping = TextWrapping.Wrap, Foreground = MalachiteTheme.Muted, Margin = new(12)
                });
                return;
            }
            var route = QuestAtlas.Build(atlasQuest, maps, snapshot);
            foreach (var briefing in snapshot.Records.Where(r => r.Reference.TableKey == "quests.briefings" && r.Fields.Any(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList" && f.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(k => QuestAtlas.QuestKey(k) == atlasQuest))))
            foreach (var map in scopeMaps)
            foreach (var use in map.Briefings.Where(b => b.Name == briefing.Name))
            {
                var title = briefing.Texts.FirstOrDefault()?.Title ?? briefing.Label;
                var effects = briefing.Fields.Where(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList").SelectMany(f => f.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(k => QuestAtlas.QuestKey(k) == atlasQuest).Select(k => (f.FieldId == 6 ? "выдать" : "завершить") + " «" + (AtlasQuests.SelectedItem as AtlasQuest)?.Title + "»" + (k.Split('.').Length == 4 ? ", подпункт " + k.Split('.')[3] : "") + " · " + k));
                AtlasNodes.Children.Add(new Border { BorderBrush = MalachiteTheme.Muted, BorderThickness = new(1), Padding = new(12), Margin = new(5), Child = new TextBlock {
                    Text = "КОСВЕННАЯ СВЯЗЬ ЧЕРЕЗ БРИФИНГ\n" + Path.GetFileName(map.Path) + ", строка " + use.Line + ": " + use.Action + "\n→ Разговор «" + title + "» (" + briefing.Name + ")\n→ После прохождения, по данным БД: " + string.Join("; ", effects) + "\nОбращение к состоянию разговора само по себе не выдаёт квест и не доказывает прохождение брифинга.", TextWrapping = TextWrapping.Wrap } });
            }


            foreach (var group in route.Points.GroupBy(p => p.Map))
            {
                AtlasNodes.Children.Add(new TextBlock { Text = Path.GetFileName(group.Key), FontSize = 17, Foreground = MalachiteTheme.Accent, Margin = new(8, 14, 8, 6) });
                foreach (var point in group.OrderBy(p => p.Line))
                {
                    var panel = new StackPanel();
                    var title = new Button { Content = new TextBlock { Text = point.Script + (point.Line > 0 ? " · строка " + point.Line : ""), TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 7) };
                    title.Click += (_, _) => NavigateQuestPoint(point);
                    panel.Children.Add(title);
                    string actionText = point.Action;
                    if (point.Record != null) actionText = (point.Key.Split('.').Length == 4 ? "Подпункт " + point.Key.Split('.')[3] : "Квест целиком") + " · " + point.Key + "\n" + actionText;
                    if (point.Record == null && QuestAtlas.QuestKey(point.Key) != null)
                    {
                        var local = ScriptAnalyzer.Analyze(point.Action, Registry);
                        var call = local.Calls.FirstOrDefault();
                        var description = call == null ? null : QuestStateReference.Resolve(local, call, snapshot);
                        if (description != null) actionText = description.Title + "\n" + description.Explanation.Split('\n')[0] + "\n" + description.FocusText;
                    }
                    panel.Children.Add(new TextBlock { Text = actionText, TextWrapping = TextWrapping.Wrap, ToolTip = point.Action });
                    panel.Children.Add(new TextBlock { Text = point.Conditions, TextWrapping = TextWrapping.Wrap, Foreground = MalachiteTheme.Muted, Margin = new(0, 7, 0, 5) });
                    foreach (var edge in route.Connections.Where(e => e.From == point.Id).Take(40))
                    {
                        var target = route.Points.First(p => p.Id == edge.To);
                        var button = new Button { Content = new TextBlock { Text = "→ " + Path.GetFileName(target.Map) + " · " + target.Script + (target.Record != null ? " · " + target.Key : ":" + target.Line) + "\n" + edge.Reason, TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new(0, 3, 0, 0) };
                        button.Click += (_, _) => NavigateQuestPoint(target); panel.Children.Add(button);
                    }
                    if (route.Connections.Count(e => e.From == point.Id) > 40) panel.Children.Add(new TextBlock { Text = "Первые 40 исходящих связей; все места использования перечислены в карточках.", TextWrapping = TextWrapping.Wrap });
                    AtlasNodes.Children.Add(new Border { Child = panel, BorderBrush = MalachiteTheme.Border, BorderThickness = new(1), Padding = new(10), Margin = new(5), CornerRadius = new(5) });
                }
            }
        }
        finally { atlasUpdating = false; }
    }
    sealed record AtlasQuest(string Key, string Title, bool Indirect)
    {
        public string Group => Indirect ? "Через брифинги · косвенно" : "В скриптах MOB · напрямую";
        public override string ToString() => Title + " · " + Key;
    }
    void Atlas_Click(object sender, RoutedEventArgs e) { QuestAtlasPanel.Visibility = Visibility.Visible; CodeButton.BorderBrush = (Brush)FindResource("Line"); RefreshQuestAtlas(); }
    void AtlasClose_Click(object sender, RoutedEventArgs e) => Code_Click(sender, e);
    void AtlasSelection_Changed(object sender, SelectionChangedEventArgs e) { if (!atlasUpdating && AtlasQuests.SelectedItem is AtlasQuest quest) { atlasQuest = quest.Key; RefreshQuestAtlas(); } }
    void AtlasScope_Changed(object sender, RoutedEventArgs e) => RefreshQuestAtlas();
    public void NavigateQuestPoint(QuestPoint point)
    {
        if (point.Record != null) { var result = Host.Workspace.Navigate(point.Record); if (!result.Succeeded) Host.Report(result.Message); return; }
        string currentPath = string.IsNullOrEmpty(Workspace.SourcePath) ? Workspace.Name : Workspace.SourcePath;
        if (point.Map.Equals(currentPath, StringComparison.OrdinalIgnoreCase) && point.Hash?.StartsWith("text:", StringComparison.Ordinal) == true && point.Hash != AtlasTextHash(CodeEditor.Text))
        { Host.Report("Код изменился после построения цепочки. Обновите «Квесты карты»."); return; }
        if (!point.Map.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Host.Files is not DatabaseEditor.Modules.V1.IModuleFileBrowser browser) throw new IOException("Выберите папку MOB через хост.");
                EnsureIndexedTarget(point.Map);
                var file = browser.Read(point.Map); var target = Workspace.FromBytes(file.DisplayPath, file.Content.ToArray());
                if (target.Mob?.Hash != point.Hash) throw new IOException("MOB изменён после индексации. Заново выберите папку MOB.");
                if (documentHost == null && !CanLeave()) return;
                LoadOpenedFile(file);
            }
            catch (Exception ex) { Host.Report(ex.Message); return; }
        }
        QuestAtlasPanel.Visibility = Visibility.Collapsed; GoToCode(point.Offset);
    }
}
