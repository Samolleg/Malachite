using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuestStudio;
public partial class QuestView
{
    readonly Popup completionPopup = new() { StaysOpen = false, AllowsTransparency = true };
    readonly ListBox completionList = new() { MaxHeight = 250, MinWidth = 330, MaxWidth = 600, Focusable = false };
    string? lexicalText;
    List<EditorSpan> lexicalTokens = [];
    List<EditorSpan> EditorTokens()
    {
        var text = CodeEditor.Text;
        if (!ReferenceEquals(lexicalText, text) && lexicalText != text)
        { lexicalText = text; lexicalTokens = EditorLexical.Scan(text); }
        return lexicalTokens;
    }
    readonly System.Windows.Threading.DispatcherTimer editorFeedback = new() { Interval = TimeSpan.FromMilliseconds(120) };
    bool feedbackRunning;
    void ScheduleEditorFeedback()
    {
        editorFeedback.Stop(); editorFeedback.Start();
    }
    async void RefreshEditorFeedback(object? sender, EventArgs e)
    {
        editorFeedback.Stop();
        if (stopped || internalUpdate) return;
        if (feedbackRunning) { editorFeedback.Start(); return; }
        feedbackRunning = true;
        var text = CodeEditor.Text;
        try
        {
            var tokens = await System.Threading.Tasks.Task.Run(() => EditorLexical.Scan(text));
            await Dispatcher.InvokeAsync(() => {
                if (stopped || CodeEditor.Text != text) return;
                lexicalText = text; lexicalTokens = tokens;
                if (syntax != null) { syntax.UpdateText(text); syntax.Visibility = Visibility.Visible; CodeEditor.Foreground = Brushes.Transparent; }
                UpdateSignature();
                if (CodeEditor.IsKeyboardFocusWithin && !acceptingCompletion) ShowCompletion(false);
            });
        }
        catch (Exception ex)
        { if (!stopped) await Dispatcher.InvokeAsync(() => SetStatus("Подсветка не обновлена: " + ex.Message)); }
        finally { feedbackRunning = false; }
    }
    int completionStart, completionEnd;
    bool acceptingCompletion;
    public sealed record CompletionItem(string Name, string Description)
    { public override string ToString() => Name + " — " + Description; }
    void InitializeCompletion()
    {
        editorFeedback.Tick += RefreshEditorFeedback;
        completionPopup.PlacementTarget = CodeEditor; completionPopup.Placement = PlacementMode.Relative;
        completionList.Style = (Style)Resources[typeof(ListBox)];
        completionList.ItemContainerStyle = (Style)Resources[typeof(ListBoxItem)];
        completionList.Background = MalachiteTheme.Popup;
        completionList.Foreground = MalachiteTheme.Text;
        completionPopup.Child = new Border { Background = MalachiteTheme.Popup, Child = completionList, BorderThickness = new(1), BorderBrush = MalachiteTheme.StrongBorder, Padding = new(5) };
        completionList.PreviewMouseLeftButtonUp += (_, e) => { AcceptCompletion(); e.Handled = true; };
        CodeEditor.PreviewKeyDown += CompletionKey;
        CodeEditor.TextChanged += CompletionTextChanged;
        CodeEditor.SelectionChanged += CompletionCaretChanged;
        CodeEditor.LostKeyboardFocus += CompletionFocusLost;
        CodeEditor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CompletionScrolled));
        Unloaded += CompletionUnloaded;
    }
    void CompletionUnloaded(object sender, RoutedEventArgs e) => completionPopup.IsOpen = false;
    void CompletionFocusLost(object sender, KeyboardFocusChangedEventArgs e) { if (!completionList.IsMouseOver) completionPopup.IsOpen = false; }
    void CompletionScrolled(object sender, ScrollChangedEventArgs e) { if (e.VerticalChange != 0 || e.HorizontalChange != 0) completionPopup.IsOpen = false; }
    void CompletionCaretChanged(object sender, RoutedEventArgs e) { if (!acceptingCompletion) completionPopup.IsOpen = false; if (!internalUpdate && lexicalText == CodeEditor.Text) UpdateSignature(); }
    void CompletionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (internalUpdate) return;
        completionPopup.IsOpen = false;
        ScheduleEditorFeedback();
    }
    public IReadOnlyList<CompletionItem> CompletionCandidates(bool explicitRequest)
    {
        var text = CodeEditor.Text; int caret = CodeEditor.CaretIndex;
        if (!explicitRequest && lexicalText != text) return [];
        var spans = EditorTokens();
        var blocked = spans.FirstOrDefault(s => s.Start < caret && s.End >= caret && s.Kind is "string" or "comment");
        if (blocked != null) return [];
        var token = spans.FirstOrDefault(s => s.Kind == "id" && s.Start < caret && s.End >= caret);
        completionStart = token?.Start ?? caret; completionEnd = token?.End ?? caret;
        string prefix = text[completionStart..caret];
        if (!explicitRequest && prefix.Length < 2) return [];
        var names = Registry.Commands.Keys.Concat(Current.Declarations.Keys).Concat(new[] { "if", "then", "else", "Script", "DeclareScript", "WorldScript", "GlobalVars" });
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(80)
            .Select(n => new CompletionItem(n, Help.Entries.TryGetValue(n, out var entry) ? entry.Purpose : "Конструкция или объявленный скрипт")).ToArray();
    }
    void ShowCompletion(bool explicitRequest)
    {
        var items = CompletionCandidates(explicitRequest);
        completionList.ItemsSource = items; completionList.SelectedIndex = items.Count > 0 ? 0 : -1;
        if (items.Count == 0 || CodeEditor.IsReadOnly) { completionPopup.IsOpen = false; return; }
        var rect = CodeEditor.GetRectFromCharacterIndex(CodeEditor.CaretIndex);
        completionPopup.HorizontalOffset = rect.IsEmpty ? 10 : rect.X;
        completionPopup.VerticalOffset = rect.IsEmpty ? 25 : rect.Bottom + 5;
        completionPopup.IsOpen = true;
    }
    public void InsertCompletion(string name)
    {
        if (CodeEditor.IsReadOnly) return;
        acceptingCompletion = true;
        try
        {
            var tail = CodeEditor.Text[completionEnd..].TrimStart();
            bool parentheses = (Registry.Commands.ContainsKey(name) || Current.Declarations.ContainsKey(name)) && !tail.StartsWith('(');
            CodeEditor.Select(completionStart, completionEnd - completionStart);
            CodeEditor.SelectedText = name + (parentheses ? "()" : "");
            CodeEditor.CaretIndex = completionStart + name.Length + (parentheses ? 1 : 0);
            completionPopup.IsOpen = false; CodeEditor.Focus(); UpdateSignature();
        }
        finally { acceptingCompletion = false; }
    }
    void AcceptCompletion() { if (completionList.SelectedItem is CompletionItem item) InsertCompletion(item.Name); }
    void CompletionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { ShowCompletion(true); e.Handled = true; return; }
        if (!completionPopup.IsOpen) return;
        if (e.Key == Key.Escape) { completionPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up) { completionList.SelectedIndex = Math.Clamp(completionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, completionList.Items.Count - 1); completionList.ScrollIntoView(completionList.SelectedItem); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Tab && Keyboard.Modifiers == ModifierKeys.None) { AcceptCompletion(); e.Handled = true; }
    }
    void UpdateSignature()
    {
        if (ArgumentHelp == null) return;
        var hint = EditorLexical.ArgumentAt(EditorTokens(), CodeEditor.CaretIndex);
        if (hint != null && Registry.Commands.TryGetValue(hint.Name, out var command))
        {
            Help.Entries.TryGetValue(command.Name, out var entry);
            string argument = entry != null && hint.Index < entry.Arguments.Length ? entry.Arguments[hint.Index] : "проверьте число аргументов";
            ArgumentHelp.Text = command.Signature + "\n▶ Аргумент " + (hint.Index + 1) + ": " + argument;
        }
        else if (hint != null && Current.Declarations.TryGetValue(hint.Name, out var parameters))
        {
            string signature = hint.Name + "(" + string.Join(", ", parameters.Select(p => p.Name + ": " + p.Type)) + ")";
            ArgumentHelp.Text = signature + "\n▶ " + (hint.Index < parameters.Length
                ? "Аргумент " + (hint.Index + 1) + ": " + parameters[hint.Index].Name + " — " + HelpService.TypeName(parameters[hint.Index].Type)
                : "Проверьте число аргументов: объявлено " + parameters.Length);
        }
        else ArgumentHelp.Text = "Ctrl+Пробел — список команд · Enter/Tab — вставить · Esc — закрыть";
    }
    void StopCompletion()
    {
        editorFeedback.Stop(); editorFeedback.Tick -= RefreshEditorFeedback;
        completionPopup.IsOpen = false;
        CodeEditor.PreviewKeyDown -= CompletionKey; CodeEditor.TextChanged -= CompletionTextChanged;
        CodeEditor.SelectionChanged -= CompletionCaretChanged; CodeEditor.LostKeyboardFocus -= CompletionFocusLost;
        CodeEditor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CompletionScrolled));
        Unloaded -= CompletionUnloaded;
    }
}
