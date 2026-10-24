using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuestStudio;
public partial class QuestView
{
    bool readable = true;
    readonly List<Button> scenarioButtons = [];
    ScenarioExplanation? scenarioExplanation;
    Analysis? scenarioAnalysis;
    MobDocument? scenarioMob;
    ScenarioExplanation CurrentExplanation()
    {
        if (scenarioExplanation == null || scenarioAnalysis != Current || scenarioMob != Workspace.Mob)
        {
            scenarioExplanation = new(Current, Help, CurrentObjects(), Host.Workspace.Snapshot(), Registry);
            scenarioAnalysis = Current; scenarioMob = Workspace.Mob;
        }
        return scenarioExplanation;
    }
    void Scenario_Click(object sender, RoutedEventArgs e)
    {
        QuestAtlasPanel.Visibility = Visibility.Collapsed;
        readable = true; codeOnly = false; GraphRow.Height = new GridLength(360);
        ShowCodeArea();
        ScenarioPanel.Visibility = Visibility.Visible; GraphPanel.Visibility = Visibility.Collapsed;
        UpdateModeButtons();
        RenderScenario();
    }
    FrameworkElement ScenarioSource(int start, int end)
    {
        start = Math.Clamp(start, 0, Current.Text.Length);
        end = Math.Clamp(end, start, Current.Text.Length);
        if (end == start) end = Math.Min(Current.Text.Length, Current.Text.IndexOf('\n', start) is int at && at >= 0 ? at : Current.Text.Length);
        const int limit = 4000;
        string text = Current.Text[start..Math.Min(end, start + limit)];
        if (end - start > limit) text += "\n… Показаны первые 4000 символов. Полный фрагмент — в редакторе кода.";
        return new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    void RenderScenario()
    {
        if (ScenarioItems == null || !readable || codeOnly || ScenarioPanel.Visibility != Visibility.Visible) return;
        bool expanded = ScenarioItems.Children.OfType<Expander>().Any(e => e.Tag is string tag && tag == "details" && e.IsExpanded);
        ScenarioItems.Children.Clear(); scenarioButtons.Clear();
        var block = Selected ?? Current.Blocks.FirstOrDefault();
        if (block == null) { ScenarioItems.Children.Add(new TextBlock { Text = "Выберите этап слева. Если код неполный, исправьте отмеченные ошибки.", TextWrapping = TextWrapping.Wrap }); return; }
        var lines = ReadableScenario.Describe(Current, block, CurrentExplanation());
        var details = new StackPanel();
        foreach (var line in lines)
        {
            var target = line.Compact ? ScenarioItems : details;
            var text = new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 };
            var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10 + Math.Min(line.Depth, 8) * 18, 3, 10, 3), Padding = new Thickness(10, 8, 10, 8),
                ToolTip = "Перейти к исходной строке " + Current.LineAt(line.Offset), Foreground = line.Caution ? MalachiteTheme.Warning : MalachiteTheme.Text,
                Tag = line.Offset };
            button.Click += (_, _) => SelectScenarioAction(line.Offset);
            target.Children.Add(button); scenarioButtons.Add(button);
            target.Children.Add(new Expander { Header = "Исходный код · строка " + Current.LineAt(line.Offset),
                Foreground = MalachiteTheme.Muted, Margin = new Thickness(20 + Math.Min(line.Depth, 8) * 18, 0, 10, 4),
                Content = ScenarioSource(line.Offset, line.End) });
        }
        if (details.Children.Count > 0)
            ScenarioItems.Children.Add(new Expander { Header = "Все условия и действия · " + lines.Count(l => !l.Compact) + " строк описания",
                Foreground = MalachiteTheme.Text, Margin = new Thickness(10), Content = details, Tag = "details", IsExpanded = expanded });
        HighlightScenario();
    }
}
