using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuestStudio;
public partial class QuestView
{
    void InitializeTextEditing()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("CopiesMenu") };
        void Action(string text, Action execute)
        { var item = new MenuItem { Header = text, Style = (Style)FindResource("CopiesItem") }; item.Click += (_, _) => execute(); menu.Items.Add(item); }
        Action("Отменить · Ctrl+Z", () => RunCommand(DatabaseEditor.Modules.V1.ModuleCommand.Undo));
        Action("Повторить · Ctrl+Y", () => RunCommand(DatabaseEditor.Modules.V1.ModuleCommand.Redo));
        foreach (var (text, command) in new[] { ("Вырезать · Ctrl+X", ApplicationCommands.Cut), ("Копировать · Ctrl+C", ApplicationCommands.Copy), ("Вставить · Ctrl+V", ApplicationCommands.Paste), ("Выделить всё · Ctrl+A", ApplicationCommands.SelectAll) })
            menu.Items.Add(new MenuItem { Header = text, Command = command, CommandTarget = CodeEditor, Style = (Style)FindResource("CopiesItem") });
        CodeEditor.ContextMenu = menu;
    }
    void ShowFind(bool replace)
    {
        if (HomePanel.Visibility == Visibility.Visible) { HomeSearch.Focus(); HomeSearch.SelectAll(); return; }
        QuestAtlasPanel.Visibility = Visibility.Collapsed;
        FindPanel.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        if (CodeEditor.SelectionLength > 0 && !CodeEditor.SelectedText.Contains('\n')) FindText.Text = CodeEditor.SelectedText;
        FindText.Focus(); FindText.SelectAll();
    }
    void Find_Click(object sender, RoutedEventArgs e) => ShowFind(false);
    void Replace_Click(object sender, RoutedEventArgs e) => ShowFind(true);
    void FindClose_Click(object sender, RoutedEventArgs e) { FindPanel.Visibility = Visibility.Collapsed; CodeEditor.Focus(); }
    void FindNext_Click(object sender, RoutedEventArgs e) => FindInCode(false);
    void FindPrevious_Click(object sender, RoutedEventArgs e) => FindInCode(true);
    StringComparison SearchComparison => FindCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    void FindInCode(bool backwards)
    {
        string needle = FindText.Text, text = CodeEditor.Text;
        if (needle.Length == 0 || text.Length == 0) return;
        int start = CodeEditor.SelectionStart, found;
        if (backwards)
        {
            found = start > 0 ? text.LastIndexOf(needle, start - 1, SearchComparison) : -1;
            if (found < 0) found = text.LastIndexOf(needle, SearchComparison);
        }
        else
        {
            start += CodeEditor.SelectionLength;
            found = text.IndexOf(needle, Math.Min(start, text.Length), SearchComparison);
            if (found < 0) found = text.IndexOf(needle, SearchComparison);
        }
        if (found < 0) { FindResult.Text = "Не найдено"; return; }
        CodeEditor.Select(found, needle.Length); CodeEditor.ScrollToLine(CodeEditor.GetLineIndexFromCharacterIndex(found));
        FindResult.Text = "Найдено";
    }
    void ReplaceOne_Click(object sender, RoutedEventArgs e)
    {
        if (CodeEditor.IsReadOnly || FindText.Text.Length == 0) return;
        if (CodeEditor.SelectedText.Equals(FindText.Text, SearchComparison))
            EditRange(CodeEditor.SelectionStart, CodeEditor.SelectionLength, ReplaceText.Text);
        FindInCode(false);
    }
    void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (CodeEditor.IsReadOnly || FindText.Text.Length == 0) return;
        string text = CodeEditor.Text, needle = FindText.Text; int start = 0, count = 0;
        var result = new StringBuilder();
        for (int found; (found = text.IndexOf(needle, start, SearchComparison)) >= 0;)
        { result.Append(text, start, found - start).Append(ReplaceText.Text); start = found + needle.Length; count++; }
        if (count > 0) { result.Append(text, start, text.Length - start); int caret = CodeEditor.CaretIndex; EditRange(0, text.Length, result.ToString()); CodeEditor.CaretIndex = Math.Min(caret, CodeEditor.Text.Length); }
        FindResult.Text = "Заменено: " + count;
    }
    void Find_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FindInCode(Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
        else if (e.Key == Key.Escape) { FindClose_Click(sender, e); e.Handled = true; }
    }
    // One native edit and one host history transaction, with the final selection captured.
    void EditRange(int start, int length, string replacement, bool select = false)
    {
        if (CodeEditor.IsReadOnly) return;
        internalUpdate = true;
        try { CodeEditor.BeginChange(); CodeEditor.Select(start, length); CodeEditor.SelectedText = replacement;
            CodeEditor.Select(select ? start : start + replacement.Length, select ? replacement.Length : 0); }
        finally { CodeEditor.EndChange(); internalUpdate = false; }
        Code_Changed(CodeEditor, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
        ScheduleEditorFeedback();
    }
    void Indent_Click(object sender, RoutedEventArgs e) { IndentSelection(false); CodeEditor.Focus(); }
    void Unindent_Click(object sender, RoutedEventArgs e) { IndentSelection(true); CodeEditor.Focus(); }
    public void IndentSelection(bool remove)
    {
        if (CodeEditor.IsReadOnly) return;
        string text = CodeEditor.Text; int start = CodeEditor.SelectionStart, end = start + CodeEditor.SelectionLength;
        int first = text.LastIndexOf('\n', Math.Max(0, start - 1), start);
        first = first < 0 ? 0 : first + 1;
        int last = end;
        if (end > start && text[end - 1] == '\n') last = end;
        else { int newline = text.IndexOf('\n', end); last = newline < 0 ? text.Length : newline + 1; }
        string fragment = text[first..last];
        var result = new StringBuilder(); int offset = 0;
        while (offset < fragment.Length)
        {
            int newline = fragment.IndexOf('\n', offset); int next = newline < 0 ? fragment.Length : newline + 1;
            string line = fragment[offset..next];
            if (!remove) result.Append("  ").Append(line);
            else { int skip = line.StartsWith('\t') ? 1 : line.TakeWhile(c => c == ' ').Take(2).Count(); result.Append(line.AsSpan(skip)); }
            offset = next;
        }
        if (fragment.Length == 0 && !remove) result.Append("  ");
        EditRange(first, last - first, result.ToString(), true);
    }
    void TextEditingKey(object sender, KeyEventArgs e)
    {
        if (e.Handled || CodeEditor.IsReadOnly) return;
        if (e.Key == Key.Tab && (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) && (CodeEditor.SelectionLength > 0 || Keyboard.Modifiers == ModifierKeys.Shift))
        { IndentSelection(Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        { EditRange(CodeEditor.SelectionStart, CodeEditor.SelectionLength, "  "); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            string text = CodeEditor.Text; int caret = CodeEditor.SelectionStart;
            int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
            string indent = new(text[start..caret].TakeWhile(c => c is ' ' or '\t').ToArray());
            string newline = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
            EditRange(caret, CodeEditor.SelectionLength, newline + indent); e.Handled = true;
        }
    }
}
