using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    sealed record NavigatorRow(string Id, string Title, string Subtitle, string Tooltip, Thickness Indent,
        NavigatorPlace? Place = null, string? Quest = null, bool Expandable = false);
    NavigatorData? navigatorData;
    Task<NavigatorData>? navigatorBuild;
    Analysis? navigatorAnalysis;
    WorkspaceStamp? navigatorStamp;
    MobDocument? navigatorMob;
    int navigatorRevision;
    readonly HashSet<string> navigatorExpanded = [];

    void ResetNavigator()
    {
        navigatorRevision++; navigatorData = null; navigatorBuild = null; navigatorAnalysis = null;
        navigatorExpanded.Clear(); atlasQuest = null;
        if (QuestOverview != null) { QuestOverview.IsExpanded = false; QuestOverview.Visibility = Visibility.Collapsed; }
    }
    void Search_Changed(object sender, TextChangedEventArgs e) { if (Current != null) FillNavigation(); }
    void Quests_Click(object sender, RoutedEventArgs e) { mode = "quests"; FillNavigation(); }
    void Scripts_Click(object sender, RoutedEventArgs e) { mode = "scripts"; FillNavigation(); }
    void NavigatorGroup_Changed(object sender, SelectionChangedEventArgs e) { if (Current != null) FillNavigation(); }
    async void FillNavigation()
    {
        if (Navigation == null || Current == null || stopped || internalUpdate) return;
        UpdateHomeMaps();
        int revision = ++navigatorRevision;
        var analysis = Current; var snapshot = Host.Workspace.Snapshot(); var mob = Workspace.Mob;
        var path = AtlasPath;
        QuestsTab.BorderBrush = mode == "quests" ? MalachiteTheme.Accent : MalachiteTheme.Border;
        ScriptsTab.BorderBrush = mode == "scripts" ? MalachiteTheme.Accent : MalachiteTheme.Border;
        NavigatorGroup.Visibility = mode == "scripts" ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (navigatorBuild == null || navigatorAnalysis != analysis || navigatorStamp != snapshot.Stamp || navigatorMob != mob)
            {
                navigatorAnalysis = analysis; navigatorStamp = snapshot.Stamp; navigatorMob = mob;
                navigatorData = null; Navigation.ItemsSource = null;
                NavigatorStatus.Text = "Подготовка навигатора…";
                navigatorBuild = Task.Run(() => MapNavigator.Build(analysis, snapshot, path,
                    mob == null ? [] : MobObjects.Read(mob), Help, Registry));
            }
            var data = await navigatorBuild;
            if (stopped || revision != navigatorRevision || Current != analysis || path != AtlasPath) return;
            navigatorData = data;
            RenderNavigator();
        }
        catch (Exception ex) { if (!stopped && revision == navigatorRevision) NavigatorStatus.Text = "Навигатор: " + ex.Message; }
    }
    void RenderNavigator()
    {
        if (navigatorData is not { } data) return;
        var rows = new List<NavigatorRow>();
        string query = SearchBox.Text.Trim();
        string groupFilter = mode == "scripts" ? (NavigatorGroup.SelectedItem as ComboBoxItem)?.Content as string ?? "Все" : "Все";
        NavigatorRow Place(NavigatorPlace p, int indent = 0) => new("p:" + p.Offset, p.Title, p.Subtitle, p.Tooltip, new(indent, 0, 0, 0), p, p.Quest);
        void Header(string id, string title, string subtitle, int indent = 0, string? quest = null)
            => rows.Add(new(id, (navigatorExpanded.Contains(id) ? "▾ " : "▸ ") + title, subtitle,
                subtitle, new(indent, 0, 0, 0), Quest: quest, Expandable: true));
        void Quest(NavigatorQuest quest, int indent)
        {
            string id = "q:" + quest.Item.Key;
            Header(id, quest.Item.Title, quest.Item.DisplayKey + " · " + quest.Item.Reason, indent, quest.Item.Key);
            if (!navigatorExpanded.Contains(id)) return;
            foreach (var group in quest.Places.GroupBy(p => p.Group).OrderBy(g => g.Key == "Выдача" ? 0 :
                g.Key == "Завершение / провал" ? 2 : g.Key == "Проверки состояния" ? 3 : 1))
            {
                string child = id + ":" + group.Key;
                Header(child, group.Key, group.Count() + " мест", indent + 10, quest.Item.Key);
                if (navigatorExpanded.Contains(child)) rows.AddRange(group.Select(p => Place(p, indent + 20)));
            }
        }
        if (query.Length > 0)
        {
            var matches = MapNavigator.Search(data, query, groupFilter);
            rows.AddRange(matches.Select(p => Place(p)));
            NavigatorStatus.Text = "Найдено мест: " + matches.Length;
        }
        else if (mode == "scripts")
        {
            foreach (string group in MapNavigator.Groups.Where(g => groupFilter == "Все" || g == groupFilter))
            {
                var scripts = data.Scripts.Where(p => p.Group == group).ToArray();
                if (scripts.Length == 0) continue;
                // Script groups are expanded by default; an explicit closed marker remembers folding.
                string id = "s:" + group;
                bool closed = navigatorExpanded.Contains("closed:" + id);
                rows.Add(new(id, (closed ? "▸ " : "▾ ") + group, scripts.Length + " блоков", group, new(0), Expandable: true));
                if (!closed) rows.AddRange(scripts.Select(p => Place(p, 10)));
            }
            NavigatorStatus.Text = data.Scripts.Length + " блоков · Script + WorldScript";
        }
        else
        {
            foreach (var quest in data.Quests.Where(q => q.Item.Category != 1)) Quest(quest, 0);
            var checks = data.Quests.Where(q => q.Item.Category == 1).ToArray();
            if (checks.Length > 0)
            {
                Header("checks", "Только проверяются здесь", checks.Length + " квестов");
                if (navigatorExpanded.Contains("checks")) foreach (var quest in checks) Quest(quest, 10);
            }
            NavigatorStatus.Text = "Квестов карты: " + data.Quests.Count(q => q.Item.Category != 1) + " · проверяются: " + checks.Length;
        }
        var selected = (Navigation.SelectedItem as NavigatorRow)?.Id;
        Navigation.ItemsSource = rows;
        Navigation.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
    }
    void Navigator_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Navigation, source) is ListBoxItem { Content: NavigatorRow row })
        { Navigation.SelectedItem = row; ActivateNavigator(row); e.Handled = true; }
    }
    void Navigator_KeyDown(object sender, KeyEventArgs e)
    {
        if (Navigation.SelectedItem is not NavigatorRow row || e.Key is not (Key.Enter or Key.Space or Key.Right or Key.Left)) return;
        if (e.Key is Key.Right or Key.Left && !row.Expandable) return;
        if (row.Expandable && e.Key is Key.Right or Key.Left)
        {
            bool open = row.Id.StartsWith("s:") ? !navigatorExpanded.Contains("closed:" + row.Id) : navigatorExpanded.Contains(row.Id);
            if (open == (e.Key == Key.Right)) { e.Handled = true; return; }
        }
        ActivateNavigator(row); e.Handled = true;
    }
    void ActivateNavigator(NavigatorRow row)
    {
        if (Current.Text != CodeEditor.Text || navigatorData?.Analysis != Current)
        { SetStatus("Код изменён; дождитесь обновления навигатора."); return; }
        if (row.Expandable)
        {
            string id = row.Id.StartsWith("s:") ? "closed:" + row.Id : row.Id;
            if (!navigatorExpanded.Add(id)) navigatorExpanded.Remove(id);
            RenderNavigator();
            if (row.Quest != null) ShowNavigatorQuest(row.Quest);
        }
        else if (row.Place is { } place) GoToCode(place.Offset);
    }
    void ShowNavigatorQuest(string quest)
    {
        InspectorTitle.Text = MapQuestInventory.Title(quest, Host.Workspace.Snapshot());
        InspectorHint.Text = quest;
        var panel = new StackPanel();
        panel.Children.Add(AtlasNote(MapNavigator.QuestText(quest, Host.Workspace.Snapshot())));
        var item = navigatorData?.Quests.FirstOrDefault(q => q.Item.Keys.Contains(quest, StringComparer.Ordinal));
        if (item?.Item.Keys.Length > 1)
        {
            panel.Children.Add(AtlasNote("Ключи одной записи БД. Выберите корень для отдельного обзора связей:", true));
            foreach (string key in item.Item.Keys)
            {
                var button = new Button { Content = key, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new(0, 3, 0, 0) };
                button.Click += (_, _) => ShowNavigatorQuest(key);
                panel.Children.Add(button);
            }
        }
        ExplanationContent.Content = panel;
        ExplanationContent.Visibility = Visibility.Visible; HelpPanel.Visibility = Visibility.Collapsed;
        explainedAnalysis = null; atlasQuest = quest;
        QuestOverview.Visibility = Visibility.Visible;
        RefreshQuestAtlas();
    }
    void NewStage_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolveCard() || CodeEditor.IsReadOnly || publishConflict || !documentOpen) return;
        if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
        var edit = MapNavigator.InsertStage(Current, CodeEditor.CaretIndex);
        ReplaceEditor(edit.Text);
        GoToCode(edit.Caret);
        SetStatus(edit.Name + ": заполните условие и действия; запуск этапа задаётся в коде.");
    }
    void AddNavigatorQuestText(StackPanel panel, CodeContextIndex.Position position)
    {
        var snapshot = Host.Workspace.Snapshot();
        var calls = position.Block?.Calls.Where(c => c.Start >= (position.Rule?.Start ?? position.Block.Start) &&
            c.Start < (position.Rule?.End ?? position.Block.End)) ?? [];
        var keys = calls.Select(c => QuestStateReference.Supports(c.Name) ? QuestAtlas.QuestRoot(QuestAtlas.Literal(Current, c, 1) ?? "") :
                c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase) ? QuestAtlas.Literal(Current, c, 1) : null)
            .Where(k => k != null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        foreach (string key in keys)
        {
            panel.Children.Add(new TextBlock { Text = "КВЕСТ · texts.res", Foreground = MalachiteTheme.Accent, Margin = new(0, 12, 0, 6) });
            panel.Children.Add(AtlasNote(MapNavigator.QuestText(key, snapshot)));
        }
        string? next = keys.FirstOrDefault();
        if (atlasQuest != next)
        {
            atlasQuest = next; QuestOverview.IsExpanded = false; atlasRefreshRevision++; AtlasNodes.Children.Clear();
        }
        QuestOverview.Visibility = next == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
