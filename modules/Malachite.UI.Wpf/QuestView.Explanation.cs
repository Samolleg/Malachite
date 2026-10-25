using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuestStudio;

public partial class QuestView
{
    ScenarioExplanation? scenarioExplanation;
    Analysis? scenarioAnalysis;
    MobDocument? scenarioMob;
    CodeContextIndex? codeContext;
    Analysis? contextAnalysis;
    int explanationOffset = -1;
    CodeContextIndex CurrentCodeContext()
    {
        if (codeContext == null || contextAnalysis != Current)
        { codeContext = new(Current); contextAnalysis = Current; }
        return codeContext;
    }
    ScenarioExplanation CurrentExplanation()
    {
        if (scenarioExplanation == null || scenarioAnalysis != Current || scenarioMob != Workspace.Mob)
        {
            scenarioExplanation = new(Current, Help, CurrentObjects(), Host.Workspace.Snapshot(), Registry);
            scenarioAnalysis = Current; scenarioMob = Workspace.Mob;
        }
        return scenarioExplanation;
    }
    FrameworkElement ExplanationSource(int start, int end)
    {
        start = Math.Clamp(start, 0, Current.Text.Length); end = Math.Clamp(end, start, Current.Text.Length);
        const int limit = 4000;
        string text = Current.Text[start..Math.Min(end, start + limit)];
        if (end - start > limit) text += "\n… Показаны первые 4000 символов. Полный фрагмент — в редакторе кода.";
        return new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static FrameworkElement ScenarioView(IEnumerable<ScenarioLine> lines)
    {
        var panel = new StackPanel();
        foreach (var line in lines)
        {
            if (line.Kind == "separator")
            {
                panel.Children.Add(new Border { BorderBrush = MalachiteTheme.Border, BorderThickness = new(0, 0, 0, 1), Margin = new(0, 9, 0, 9) });
                continue;
            }
            bool heading = line.Kind == "heading", note = line.Kind == "note";
            panel.Children.Add(new TextBlock
            {
                Text = (heading || note ? "" : "• ") + line.Text, ToolTip = line.Tooltip.Length == 0 ? null : line.Tooltip,
                TextWrapping = TextWrapping.Wrap, FontSize = note ? 12 : 14, LineHeight = note ? 18 : 22,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = note ? MalachiteTheme.Muted : heading ? MalachiteTheme.Accent : MalachiteTheme.Text,
                Margin = new(line.Indent * 16 + (heading ? 0 : 12), heading ? 7 : 0, 0, note ? 5 : 3)
            });
        }
        return panel;
    }
    void ExplainAt(int offset)
    {
        if (ExplanationContent == null || Current == null || internalUpdate || cardDirty) return;
        ExplanationContent.Visibility = Visibility.Visible; HelpPanel.Visibility = Visibility.Collapsed;
        if (Current.Text != CodeEditor.Text)
        {
            explanationOffset = -1;
            InspectorTitle.Text = "Код изменён"; InspectorTitle.ToolTip = null;
            InspectorHint.Text = "Объяснение обновится после ввода…";
            if (!explanationWaiting) ExplanationContent.Content = null;
            explanationWaiting = true; explainedAnalysis = null;
            return;
        }
        var index = CurrentCodeContext(); var position = index.At(offset);
        // The location cache must include the call, not just a selected Script.
        int focus = position.Call?.Start ?? position.Start;
        if (explainedAnalysis == Current && explainedLine == position.Line && explainedFocus == focus && !explanationWaiting) return;
        if (Selected != position.Block)
        {
            Selected = position.Block;
            if (Selected != null) FillCard(Selected);
        }
        var panel = new StackPanel();
        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            LineHeight = 22, Foreground = MalachiteTheme.Text, Margin = new Thickness(0, 0, 0, 8) };
        var call = position.Call;
        explanationOffset = call?.Start ?? position.Start;
        string title = call?.Name ?? position.Construction?.Kind ?? "Строка " + position.Line;
        string description = index.LineDescription(position, CurrentExplanation());
        HoverHelp? help = null;
        if (call != null)
        {
            help = EnrichDatabaseHelp(Current, call.Start, Current.Declarations.ContainsKey(call.Name)
                ? Help.Symbol(Current, call.Name, Current, call.Start) : Help.Command(call.Name, Current, call));
        }
        else
        {
            var construction = position.Construction;
            string raw = Current.Text[position.Start..position.End].Trim();
            string word = raw.Split([' ', '\t', '('], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (construction != null || new[] { "Script", "WorldScript", "if", "then", "else" }.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                title = construction?.Kind ?? word;
                help = Help.Symbol(Current, title);
            }
        }
        InspectorTitle.Text = title; InspectorTitle.ToolTip = null;
        InspectorHint.Text = "Строка " + position.Line + (position.Block == null ? " · вне Script" : " · " + position.Block.Display);
        var sentence = Note(description);
        sentence.ToolTip = call == null ? null : CurrentExplanation().Tooltip(call, position.Condition);
        panel.Children.Add(sentence);
        if (position.Block is { } block)
        {
            var summary = position.Rule == null ? index.StageLines(block, CurrentExplanation()) : index.RuleLines(position.Rule, CurrentExplanation());
            panel.Children.Add(new TextBlock { Text = "ЭТАП · " + block.Display, Foreground = MalachiteTheme.Accent, Margin = new Thickness(0, 10, 0, 8) });
            panel.Children.Add(ScenarioView(summary));
        }
        AddNavigatorQuestText(panel, position);
        AddAtlasContext(panel, position);
        int sourceStart = call?.Start ?? position.Start, sourceEnd = call?.End ?? position.End;
        panel.Children.Add(new Expander { Header = "Исходный код · строка " + position.Line,
            Content = ExplanationSource(sourceStart, sourceEnd), Foreground = MalachiteTheme.Muted,
            IsExpanded = description.StartsWith("не объяснено:", StringComparison.Ordinal) });
        if (help != null) panel.Children.Add(new Expander { Header = "Справка и данные БД", Content = HelpView(help), Foreground = MalachiteTheme.Muted });
        ExplanationContent.Content = panel;
        explainedAnalysis = Current; explainedLine = position.Line; explainedFocus = focus; explanationWaiting = false;
    }
}
