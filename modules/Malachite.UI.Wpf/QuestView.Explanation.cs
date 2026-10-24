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
        CardPanel.Visibility = position.Block == null ? Visibility.Collapsed : Visibility.Visible;
        var panel = new StackPanel();
        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            LineHeight = 22, Foreground = MalachiteTheme.Text, Margin = new Thickness(0, 0, 0, 8) };
        var call = position.Call;
        explanationOffset = call?.Start ?? position.Start;
        string title = call?.Name ?? position.Construction?.Kind ?? "Строка " + position.Line;
        string description;
        HoverHelp? help = null;
        if (call != null)
        {
            description = CurrentExplanation().Explain(call, position.Condition);
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
                description = help?.Body ?? "Объяснение пока недоступно";
                if (construction?.Name != null) description += "\nОбъявлен Script «" + construction.Name + "».";
            }
            else description = "Объяснение пока недоступно";
        }
        InspectorTitle.Text = title; InspectorTitle.ToolTip = null;
        InspectorHint.Text = "Строка " + position.Line + (position.Block == null ? " · вне Script" : " · " + position.Block.Display);
        panel.Children.Add(Note(description));
        if (position.Block is { } block)
        {
            string summary = position.Rule == null ? index.StageSummary(block, CurrentExplanation()) : index.RuleSummary(position.Rule, CurrentExplanation());
            panel.Children.Add(new TextBlock { Text = "ЭТАП · " + block.Display, Foreground = MalachiteTheme.Accent, Margin = new Thickness(0, 10, 0, 8) });
            panel.Children.Add(Note(summary));
            panel.Children.Add(new TextBlock { Text = "По исходному коду, не по состоянию игры. Соседние ЕСЛИ — независимые проверки.",
                TextWrapping = TextWrapping.Wrap, Foreground = MalachiteTheme.Muted, Margin = new Thickness(0, 0, 0, 8) });
        }
        int sourceStart = call?.Start ?? position.Start, sourceEnd = call?.End ?? position.End;
        panel.Children.Add(new Expander { Header = "Исходный код · строка " + position.Line,
            Content = ExplanationSource(sourceStart, sourceEnd), Foreground = MalachiteTheme.Muted,
            IsExpanded = description.StartsWith("Объяснение пока недоступно", StringComparison.Ordinal) });
        if (help != null) panel.Children.Add(new Expander { Header = "Справка и данные БД", Content = HelpView(help), Foreground = MalachiteTheme.Muted });
        ExplanationContent.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 480 };
        explainedAnalysis = Current; explainedLine = position.Line; explainedFocus = focus; explanationWaiting = false;
    }
}
