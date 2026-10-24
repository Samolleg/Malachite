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
    readonly List<string> atlasIndexErrors = [];
    static string AtlasTextHash(string text) => "text:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    static string AtlasQuestTitle(string quest, WorkspaceSnapshot snapshot)
    {
        var records = snapshot.Records.Where(r => r.Reference.TableKey == "quests.quests" && r.Name == quest).ToArray();
        if (records.Length != 1) return quest + (records.Length == 0 ? " · нет в снимке БД" : " · неоднозначная запись БД");
        var texts = records[0].Texts.Where(t => t.ResourceKey.Equals("quest " + quest, StringComparison.OrdinalIgnoreCase)).ToArray();
        return texts.Length == 1 ? texts[0].Title : quest + " · текст QUEST отсутствует или неоднозначен";
    }
    public void RefreshQuestAtlas()
    {
        // Index/DB changes must also refresh a stationary inspector in the code view.
        explainedAnalysis = null;
        if (stopped || QuestAtlasPanel == null || atlasUpdating || Current == null) return;
        if (QuestAtlasPanel.Visibility != Visibility.Visible) { ExplainAt(CodeEditor.CaretIndex); return; }
        atlasUpdating = true;
        try
        {
            var snapshot = Host.Workspace.Snapshot(); var maps = AtlasSources();
            var current = maps.First(m => m.Path.Equals(AtlasPath, StringComparison.OrdinalIgnoreCase));
            var direct = current.Points.Where(p => p.Quest.Length > 0).Select(p => p.Quest).ToHashSet(StringComparer.Ordinal);
            var quests = new HashSet<string>(direct, StringComparer.Ordinal);
            if (AtlasIndirect.IsChecked == true)
                foreach (var briefing in snapshot.Records.Where(r => r.Reference.TableKey == "quests.briefings" && current.Briefings.Any(b => b.Name == r.Name)))
                foreach (var field in briefing.Fields.Where(f => f.FieldId is 6 or 7 && f.Value.Kind == "StringList"))
                foreach (var key in field.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (QuestAtlas.QuestKey(key) is { } name) quests.Add(name);
            if (atlasQuest != null) quests.Add(atlasQuest);
            var items = quests.OrderBy(q => !direct.Contains(q)).ThenBy(q => q).Select(q => new AtlasQuest(q,
                q.StartsWith("@GS:", StringComparison.Ordinal) ? "Общий ключ " + q[4..] : AtlasQuestTitle(q, snapshot), !direct.Contains(q))).ToArray();
            if (!AtlasQuests.Items.OfType<AtlasQuest>().SequenceEqual(items))
            {
                var inventory = new ListCollectionView(items);
                inventory.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AtlasQuest.Group))); AtlasQuests.ItemsSource = inventory;
            }
            atlasQuest ??= items.FirstOrDefault()?.Key;
            AtlasQuests.SelectedItem = AtlasQuests.Items.OfType<AtlasQuest>().FirstOrDefault(q => q.Key == atlasQuest);
            AtlasNodes.Children.Clear();
            AtlasStatus.Text = (folderIndexing ? folderProgress : mapsStatus) + $" · просмотрено {maps.Length} карт · ошибок индекса {atlasIndexErrors.Count}" +
                (snapshot.Modified ? " · снимок БД: есть несохранённые изменения" : " · текущий снимок БД");
            AtlasStatus.ToolTip = "Индекс — снимок диска на момент чтения. Открытые вкладки заменяют его текущим текстом. Обновить диск: Папка MOB…";
            AtlasNodes.Children.Add(AtlasLazy("Просмотренные карты и пробелы источников", () => AtlasCoverage(maps)));
            if (atlasQuest == null) { AtlasNodes.Children.Add(AtlasNote("На этой карте квесты не найдены. Включите квесты через брифинги или откройте обзор из GS-строки Инспектора.")); return; }
            bool state = atlasQuest.StartsWith("@GS:", StringComparison.Ordinal);
            var route = state ? new QuestRoute(atlasKeys.GetValueOrDefault(atlasQuest[4..]) ?? [], []) : QuestAtlas.Build(atlasQuest, maps, snapshot);
            AtlasNodes.Children.Add(new TextBlock { Text = state ? "Общий ключ " + atlasQuest[4..] : AtlasQuestTitle(atlasQuest, snapshot), FontSize = 20, TextWrapping = TextWrapping.Wrap, Foreground = MalachiteTheme.Accent, Margin = new(5, 10, 5, 8) });
            AtlasNodes.Children.Add(AtlasNote("Связь по данным — возможная зависимость. Порядок прохождения, текущие GS, выбор игрока и внешние события неизвестны. 0 — скрыт; 1 — доступен; 2 — выполнен; 3 — провален (только состояния квестов).", true));
            if (!state) AddAtlasQuestText(atlasQuest, snapshot);
            var keys = route.Points.Where(p => p.Key.Length > 0).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var uses = route.Points.Concat(keys.SelectMany(k => atlasKeys.GetValueOrDefault(k) ?? []))
                .Where(p => p.Key.Length > 0 || p.Quest == atlasQuest && p.CallTarget == null && p.Action.StartsWith("QuestComplete", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(p => p.Id).ToArray();
            AddAtlasGroup("Где состояние меняется", uses.Where(p => p.Write), maps, snapshot, route);
            AddAtlasGroup("Кто его проверяет", uses.Where(p => !p.Write), maps, snapshot, route);
            var locations = uses.Concat(route.Points).Where(p => p.Record == null).Select(p => (p.Map, p.Script)).ToHashSet();
            var neighbours = maps.SelectMany(m => m.Points).Where(p => p.Quest.Length > 0 && p.Quest != atlasQuest && locations.Contains((p.Map, p.Script)))
                .GroupBy(p => p.Quest).ToArray();
            AtlasNodes.Children.Add(new TextBlock { Text = "Какие задания зависят от него", FontSize = 17, Foreground = MalachiteTheme.Accent, Margin = new(5, 16, 5, 8) });
            if (neighbours.Length == 0) AtlasNodes.Children.Add(AtlasNote("Связанные задания в просмотренных Script не найдены; возможны внешние связи.", true));
            foreach (var neighbour in neighbours)
            {
                var points = neighbour.ToArray();
                AtlasNodes.Children.Add(AtlasLazy(AtlasQuestTitle(neighbour.Key, snapshot) + " · возможная зависимость", () =>
                {
                    var panel = new StackPanel();
                    panel.Children.Add(AtlasNote("Общий Script / GS-ключ связывает эти места; причинность и направление прохождения не доказаны.", true));
                    foreach (var point in points) panel.Children.Add(AtlasPointView(point, maps, snapshot, route));
                    var open = new Button { Content = "Весь квест: " + neighbour.Key }; open.Click += (_, _) => OpenWholeQuest(points[0]); panel.Children.Add(open);
                    return panel;
                }));
            }
            AtlasNodes.Children.Add(AtlasLazy("Запуски этапов и дочерние Script", () =>
            {
                var panel = new StackPanel();
                foreach (var point in route.Points.Where(p => p.CallTarget != null)) panel.Children.Add(AtlasPointView(point, maps, snapshot, route));
                foreach (var map in maps)
                foreach (var script in route.Points.Where(p => p.Map == map.Path && p.Record == null).Select(p => p.Script).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var call in map.Calls.Where(c => c.To.Equals(script, StringComparison.OrdinalIgnoreCase)))
                        panel.Children.Add(AtlasLink(new(map.Path + "@launch" + call.Offset, atlasQuest, "", "", map.Path, map.Hash, call.From, call.Offset, call.Line, "", "", false),
                            Path.GetFileName(map.Path) + " · " + call.From + ":" + call.Line + " → " + script + " · прямой вызов, выполнение не доказано"));
                    if (!map.Calls.Any(c => c.To.Equals(script, StringComparison.OrdinalIgnoreCase)) && !script.Equals("WorldScript", StringComparison.OrdinalIgnoreCase))
                        panel.Children.Add(AtlasNote(Path.GetFileName(map.Path) + " · " + script + ": источник запуска не найден; возможное внешнее событие неизвестно.", true));
                }
                return panel;
            }));
        }
        finally { atlasUpdating = false; }
    }
    sealed record AtlasQuest(string Key, string Title, bool Indirect)
    {
        public string Group => Indirect ? "Через брифинги / выбранные по ссылке" : "В скриптах текущей карты";
        public override string ToString() => Title + " · " + Key;
    }
    void Atlas_Click(object sender, RoutedEventArgs e) { QuestAtlasPanel.Visibility = Visibility.Visible; CodeButton.BorderBrush = (Brush)FindResource("Line"); RefreshQuestAtlas(); }
    void AtlasClose_Click(object sender, RoutedEventArgs e) => Code_Click(sender, e);
    void AtlasSelection_Changed(object sender, SelectionChangedEventArgs e) { if (!atlasUpdating && AtlasQuests.SelectedItem is AtlasQuest quest) { atlasQuest = quest.Key; RefreshQuestAtlas(); } }
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
