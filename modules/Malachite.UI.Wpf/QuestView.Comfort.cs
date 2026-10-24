using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuestStudio;

public partial class QuestView
{
    static string CountLabel(int count, string one, string few, string many) => count + " " +
        (count % 100 is >= 11 and <= 14 ? many : count % 10 == 1 ? one : count % 10 is >= 2 and <= 4 ? few : many);
    int explanationOffset = -1;
    Analysis? explainedAnalysis;
    int explainedToken = -1;
    bool explanationWaiting;
    bool scenarioOnly, sidebarHidden, inspectorHidden, checksHidden;
    double checksHeight = 120;
    void ExplainAt(int offset)
    {
        if (ExplanationContent == null || Current == null) return;
        if (Current.Text != CodeEditor.Text)
        {
            explanationOffset = -1;
            if (!explanationWaiting) ExplanationContent.Content = new TextBlock { Text = "Объяснение обновится после ввода…", Foreground = MalachiteTheme.Muted };
            explanationWaiting = true; explainedAnalysis = null;
            return;
        }
        offset = Math.Clamp(offset, 0, Math.Max(0, Current.Text.Length - 1));
        var call = Current.Calls.Where(c => c.Start <= offset && offset < c.End).MaxBy(c => c.End - c.Start);
        int tokenStart = Current.Tokens.FirstOrDefault(t => t.Start <= offset && offset < t.End)?.Start ?? call?.Start ?? offset;
        if (explainedAnalysis == Current && explainedToken == tokenStart && !explanationWaiting) return;
        explainedAnalysis = Current; explainedToken = tokenStart; explanationWaiting = false;
        var help = HelpAt(CodeEditor, offset) ?? (call == null ? null : EnrichDatabaseHelp(Current, call.Start, Help.Command(call.Name, Current, call)));
        if (help == null && Selected != null) help = Help.Script(Current, Selected);
        explanationOffset = call?.Start ?? offset;
        if (call == null) ExplanationContent.Content = help == null
            ? new TextBlock { Text = "Выберите действие или поставьте курсор в коде.", Foreground = MalachiteTheme.Muted } : HelpView(help);
        else
        {
            var panel = new StackPanel();
            bool condition = Current.Tokens.Select((token, i) => (token, i)).Any(t =>
                t.token.Value.Equals("if", StringComparison.OrdinalIgnoreCase) && t.i + 1 < Current.Tokens.Count &&
                Current.Pairs.TryGetValue(t.i + 1, out int close) && Current.Tokens[t.i + 1].Start < call.Start && Current.Tokens[close].Start >= call.End);
            panel.Children.Add(new TextBlock { Text = CurrentExplanation().Explain(call, condition), TextWrapping = TextWrapping.Wrap,
                FontSize = 14, LineHeight = 22, Foreground = MalachiteTheme.Text });
            panel.Children.Add(new Expander { Header = "Исходный код · строка " + call.Line, Content = ScenarioSource(call.Start, call.End),
                Foreground = MalachiteTheme.Muted, Margin = new Thickness(0, 8, 0, 0) });
            var source = new Button { Content = "Перейти к строке " + call.Line, Margin = new Thickness(0, 6, 0, 6) };
            source.Click += (_, _) => { ShowCodeArea(); GoTo(call.Start); };
            panel.Children.Add(source);
            if (help != null) panel.Children.Add(new Expander { Header = "Справка и данные БД", Content = HelpView(help), Foreground = MalachiteTheme.Muted });
            ExplanationContent.Content = panel;
        }
        if (ExplanationContent.Content is FrameworkElement element) element.MaxHeight = 380;
        HighlightScenario();
    }
    void SelectScenarioAction(int offset)
    {
        if (!ResolveCard()) return;
        if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
        mode = "scripts"; Selected = Current.BlockAt(offset) ?? Selected;
        if (Selected != null) { FillNavigation(); FillCard(Selected); }
        Scenario_Click(this, new());
        GoTo(offset);
        ExplainAt(offset);
        scenarioButtons.FirstOrDefault(b => b.Tag is int at && at == offset)?.BringIntoView();
        ShowHome(false);
    }
    void HighlightScenario()
    {
        if (ScenarioItems == null) return;
        foreach (var button in scenarioButtons)
        {
            bool selected = button.Tag is int offset && offset == explanationOffset;
            button.BorderBrush = selected ? MalachiteTheme.Accent : (Brush)FindResource("Line");
            button.Background = selected ? (Brush)new SolidColorBrush(Color.FromRgb(35, 58, 44)) : (Brush)FindResource("Panel");
        }
    }
    void ReadableOnly_Click(object sender, RoutedEventArgs e)
    {
        Scenario_Click(sender, e); scenarioOnly = true;
        CodeBorder.Visibility = Visibility.Collapsed; CodeRow.MinHeight = 0; CodeRow.Height = new GridLength(0);
        ScenarioSplitter.Visibility = Visibility.Collapsed; EditorArea.RowDefinitions[2].Height = new GridLength(0);
        GraphRow.MaxHeight = double.PositiveInfinity; GraphRow.Height = new GridLength(1, GridUnitType.Star);
        UpdateModeButtons();
    }
    void ShowCodeArea()
    {
        scenarioOnly = false; CodeBorder.Visibility = Visibility.Visible; CodeRow.MinHeight = 180; CodeRow.Height = new GridLength(1, GridUnitType.Star);
        ScenarioSplitter.Visibility = codeOnly ? Visibility.Collapsed : Visibility.Visible;
        EditorArea.RowDefinitions[2].Height = new GridLength(codeOnly ? 0 : 5);
        UpdateEditorBounds(); UpdateModeButtons();
    }
    void UpdateModeButtons()
    {
        if (ReadableButton == null) return;
        ReadableButton.BorderBrush = scenarioOnly ? MalachiteTheme.Accent : (Brush)FindResource("Line");
        TogetherButton.BorderBrush = readable && !codeOnly && !scenarioOnly && QuestAtlasPanel.Visibility != Visibility.Visible ? MalachiteTheme.Accent : (Brush)FindResource("Line");
        CodeButton.BorderBrush = codeOnly ? MalachiteTheme.Accent : (Brush)FindResource("Line");
    }
    void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        sidebarHidden = !sidebarHidden;
        SidebarPanel.Visibility = SidebarSplitter.Visibility = sidebarHidden ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.MinWidth = sidebarHidden ? 0 : 195; SidebarColumn.Width = new GridLength(sidebarHidden ? 0 : 210);
        EditorSurface.ColumnDefinitions[1].Width = new GridLength(sidebarHidden ? 0 : 5);
        SidebarToggle.Content = sidebarHidden ? "Скрипты ›" : "‹ Скрипты";
    }
    void InspectorToggle_Click(object sender, RoutedEventArgs e)
    {
        inspectorHidden = !inspectorHidden;
        InspectorPanel.Visibility = InspectorSplitter.Visibility = inspectorHidden ? Visibility.Collapsed : Visibility.Visible;
        InspectorColumn.MinWidth = inspectorHidden ? 0 : 265; InspectorColumn.Width = new GridLength(inspectorHidden ? 0 : 325);
        EditorSurface.ColumnDefinitions[3].Width = new GridLength(inspectorHidden ? 0 : 5);
        InspectorToggle.Content = inspectorHidden ? "Объяснение ‹" : "Объяснение ›";
    }
    void ChecksToggle_Click(object sender, RoutedEventArgs e)
    {
        checksHidden = !checksHidden;
        if (checksHidden) checksHeight = DiagnosticsRow.ActualHeight;
        Diagnostics.Visibility = DiagnosticsSplitter.Visibility = checksHidden ? Visibility.Collapsed : Visibility.Visible;
        EditorArea.RowDefinitions[4].Height = new GridLength(checksHidden ? 0 : 8);
        DiagnosticsRow.MinHeight = checksHidden ? 36 : 70; DiagnosticsRow.Height = new GridLength(checksHidden ? 36 : Math.Max(70, checksHeight));
        ChecksToggle.Content = checksHidden ? "Проверки ▸" : "Проверки ▾";
        UpdateEditorBounds();
    }
}
