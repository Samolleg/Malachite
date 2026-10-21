using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuestStudio;
public partial class QuestView
{
    bool readable = true;
    void Scenario_Click(object sender, RoutedEventArgs e)
    {
        QuestAtlasPanel.Visibility = Visibility.Collapsed;
        readable = true; codeOnly = false; GraphRow.Height = new GridLength(360);
        ShowCodeArea();
        ScenarioPanel.Visibility = Visibility.Visible; GraphPanel.Visibility = Visibility.Collapsed;
        UpdateModeButtons();
        RenderScenario();
    }
    void RenderScenario()
    {
        if (ScenarioItems == null || !readable || codeOnly || ScenarioPanel.Visibility != Visibility.Visible) return;
        ScenarioItems.Children.Clear();
        var block = Selected ?? Current.Blocks.FirstOrDefault();
        if (block == null) { ScenarioItems.Children.Add(new TextBlock { Text = "Выберите этап слева. Если код неполный, исправьте отмеченные ошибки.", TextWrapping = TextWrapping.Wrap }); return; }
        DatabaseEditor.Modules.V1.WorkspaceSnapshot? snapshot = null;
        foreach (var line in ReadableScenario.Describe(Current, block, Help))
        {
            var label = line.Text;
            var outer = Current.Calls.FirstOrDefault(c => c.Start == line.Offset);
            var relatedCalls = outer == null ? [] : Current.Calls.Where(c => c.Start >= outer.Start && c.End <= outer.End &&
                (c.Name.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase) || QuestStateReference.Supports(c.Name))).ToArray();
            foreach (var related in relatedCalls)
            {
                snapshot ??= Host.Workspace.Snapshot();
                var context = DatabaseReferences.Resolve(Current, related.Start, snapshot);
                if (context?.FocusText != null)
                    label = (related == outer ? "" : label + "\n\n") + context.Title + "\n" + context.Explanation.Split('\n')[0] + "\n" + context.FocusText;
                else if (context?.Records.Length == 1)
                    label += "\nИз БД: " + string.Join(" · ", context.Records[0].Texts.Select(t => t.Title).Prepend(context.Records[0].Label).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 };
            var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10 + Math.Min(line.Depth, 8) * 18, 3, 10, 3), Padding = new Thickness(10, 8, 10, 8),
                ToolTip = "Показать соответствующую строку исходного кода", Foreground = line.Caution ? MalachiteTheme.Warning : MalachiteTheme.Text };
            button.Tag = line.Offset;
            button.Click += (_, _) => SelectScenarioAction(line.Offset);
            ScenarioItems.Children.Add(button);
        }
        HighlightScenario();
    }
}
