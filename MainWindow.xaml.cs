using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuestStudio;

public partial class MainWindow : Window
{
    public CommandRegistry Registry { get; } = new();
    public HelpService Help { get; }
    public Workspace Workspace { get; private set; } = new();
    public Analysis Current { get; private set; } = null!;
    public ScriptBlock? Selected { get; private set; }
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(650) };
    readonly List<string> folderFiles = [];
    bool internalUpdate, dirty, cardDirty, navigating;
    SyntaxLayer? syntax;
    bool codeOnly;
    readonly List<IndexedMap> indexedMaps = [];
    int folderVersion;
    string mode = "scripts", savedText = Workspace.Demo;
    public sealed record NavRow(string Title, string Subtitle, string Kind, object Value);
    public sealed record UsageRow(string Text, int Offset, string? Path = null, string? Hash = null) { public override string ToString() => Text; }
    public sealed record IndexedMap(string Path, string Hash, List<FlagUse> Flags);

    public MainWindow()
    {
        Help = new HelpService(Registry);
        InitializeComponent();
        debounce.Tick += (_, _) => { debounce.Stop(); RefreshAnalysis(false); };
        LoadWorkspace(new Workspace(), false);
        PreviewKeyDown += Window_KeyDown;
        Closing += OnClosing;
        CodeEditor.Loaded += (_, _) => AttachSyntax();
        foreach (var editor in new[] { CodeEditor, ConditionBox, ActionBox, ElseBox }) _ = new HoverController(this, editor);
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { Check_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.F1 && !cardDirty)
        {
            if (debounce.IsEnabled) RefreshAnalysis(false);
            var help = HelpAt(CodeEditor, CodeEditor.CaretIndex);
            if (help != null) ShowHelp(help);
            e.Handled = true;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.O) { Open_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.S) { Save_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }
    void OnClosing(object? sender, CancelEventArgs e) { if (!CanLeave()) e.Cancel = true; }
    void Error(Exception ex) => MessageBox.Show(this, ex.Message, "Малахит", MessageBoxButton.OK, MessageBoxImage.Warning);
    bool ResolveCard()
    {
        if (!cardDirty) return true;
        var choice = MessageBox.Show(this, "Применить изменения карточки к коду?", "Карточка этапа", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return ApplyCard();
        cardDirty = false; CodeEditor.IsReadOnly = false; return true;
    }
    bool CanLeave()
    {
        if (!ResolveCard()) return false;
        if (!dirty) return true;
        var result = MessageBox.Show(this, "Сохранить изменения в проект перед закрытием документа?", "Изменённый документ", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        return result != MessageBoxResult.Yes || SaveProject();
    }
    public void LoadWorkspace(Workspace workspace, bool modified)
    {
        debounce.Stop(); internalUpdate = true;
        Workspace = workspace; CodeEditor.IsUndoEnabled = false; CodeEditor.Text = workspace.Text; CodeEditor.IsUndoEnabled = true; CodeEditor.IsReadOnly = false;
        mode = "scripts"; SearchBox.Text = "";
        cardDirty = false; Selected = null; savedText = modified ? "" : workspace.Text; dirty = modified;
        internalUpdate = false; RefreshAnalysis(true);
        ProjectName.Text = workspace.Name; DocumentTitle.Text = workspace.Name;
        EncodingText.Text = workspace.Mob != null ? "MOB · Windows-1251 · экспорт копии" : $"{workspace.TextEncoding.WebName} · локальный проект";
        if (Selected != null) GoTo(Selected.Start);
        SetStatus(workspace.Mob != null ? "MOB открыт. Исходный файл сохраняется без изменений." : "Учебный сценарий: замените события и условия своими.");
    }
    public void RefreshAnalysis(bool chooseFirst)
    {
        bool textChanged = Current != null && Current.Text != CodeEditor.Text;
        debounce.Stop(); Workspace.Text = CodeEditor.Text;
        try { Current = ScriptAnalyzer.Analyze(Workspace.Text, Registry); CardPanel.IsEnabled = true; }
        catch (InvalidDataException ex) { Current = new Analysis { Text = Workspace.Text }; Current.Add("error", "LIMIT", ex.Message, 0); Selected = null; CardPanel.IsEnabled = false; }
        if (syntax != null) { syntax.Update(Current); syntax.Visibility = Visibility.Visible; CodeEditor.Foreground = Brushes.Transparent; }
        var selectedName = Selected?.Name;
        Selected = selectedName == null ? null : Current.Blocks.FirstOrDefault(b => b.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        if (textChanged && !chooseFirst && !cardDirty && mode == "scripts") Selected = Current.BlockAt(CodeEditor.CaretIndex) ?? Selected;
        if (Selected == null && chooseFirst) Selected = Current.Blocks.FirstOrDefault(b => !b.World) ?? Current.Blocks.FirstOrDefault();
        DocumentSummary.Text = $"{Current.Blocks.Count(b => !b.World)} этапов  /  {Current.Calls.Count} вызовов  /  {Current.Flags.Select(f => (f.Context, f.Key)).Distinct().Count()} GS-ключей";
        int errors = Current.Issues.Count(i => i.Severity == "error"), warnings = Current.Issues.Count(i => i.Severity == "warning");
        CheckSummary.Text = $"JamaisVu · {Registry.AddonReleaseCount} команд аддона · {errors} ошибок · {warnings} предупреждений · без запуска движка";
        Diagnostics.ItemsSource = Current.Issues.Take(500).ToList();
        if (Current.Issues.Count == 0) Diagnostics.ItemsSource = new[] { new Issue("info", "CLEAN", "В рамках локальных правил замечаний нет. Результат не заменяет компиляцию движком.", 0, 1) };
        FillNavigation(); RenderGraph();
        if (!cardDirty && Selected != null && mode == "scripts") FillCard(Selected);
        dirty = Workspace.Text != savedText;
        Title = $"{(dirty ? "● " : "")}{Workspace.Name} — Малахит";
        SetStatus($"Проверка обновлена · {errors} ошибок · {warnings} предупреждений.");
    }
    void SetStatus(string text) => StatusText.Text = text;
    void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void Maximize_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Caption_Menu(object sender, MouseButtonEventArgs e) => SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    public HoverHelp? HelpAt(TextBox editor, int position)
    {
        if (position < 0 || position >= editor.Text.Length) return null;
        Analysis local;
        if (editor == CodeEditor) { if (Current.Text != editor.Text) return null; local = Current; }
        else { try { local = ScriptAnalyzer.Analyze(editor.Text, Registry); } catch (InvalidDataException) { return null; } }
        var token = local.Tokens.FirstOrDefault(t => t.Kind == "id" && t.Start <= position && t.End > position);
        return token == null ? null : Help.Symbol(Current, token.Value, local, token.Start);
    }
    public ToolTip MakeTip(HoverHelp help) => new() { Content = HelpView(help), Style = (Style)FindResource(typeof(ToolTip)) };
    public FrameworkElement HelpView(HoverHelp help)
    {
        var panel = new StackPanel { MaxWidth = 550 };
        panel.Children.Add(new TextBlock { Text = help.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(128, 222, 170)), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = help.Kind, Foreground = new SolidColorBrush(Color.FromRgb(162, 185, 171)), FontSize = 11, Margin = new Thickness(0, 4, 0, 12) });
        panel.Children.Add(new TextBlock { Text = help.Body, Foreground = new SolidColorBrush(Color.FromRgb(225, 235, 228)), FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        panel.Children.Add(new TextBlock { Text = help.Source, Foreground = new SolidColorBrush(Color.FromRgb(154, 179, 163)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 13, 0, 0) });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 610 };
    }
    void ItemTip_Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        HoverHelp? help = item.Content switch
        {
            NavRow { Kind: "block", Value: ScriptBlock block } => Help.Symbol(Current, block.Name),
            NavRow { Kind: "command", Value: CommandInfo command } => Help.Command(command.Name),
            NavRow { Kind: "flag", Value: List<FlagUse> flags } => new(flags[0].Key, "Переменная состояния GS", $"Контекст {flags[0].Context}. Найдено чтений: {flags.Count(f => !f.Write)}, записей: {flags.Count(f => f.Write)}. Нажмите, чтобы перейти к местам использования. Значение и смысл определяются кодом, а не именем ключа.", "Источник: открытый документ и индекс папки."),
            string name => Help.Symbol(Current, name),
            Issue issue => DiagnosticHelp(issue),
            UsageRow usage => new(usage.Text, "Место использования", "Нажмите для перехода к соответствующему месту в коде.", "Источник: статический индекс."),
            _ => null
        };
        if (help == null) { e.Handled = true; return; }
        item.ToolTip = MakeTip(help);
    }
    public HoverHelp DiagnosticHelp(Issue issue)
    {
        var call = Current.Calls.FirstOrDefault(c => c.Start == issue.Offset);
        if (call != null && Registry.Commands.ContainsKey(call.Name))
        {
            var explanation = Help.Command(call.Name, Current, call);
            return explanation with { Body = issue.Message + "\n\n" + explanation.Body, Source = explanation.Source + " Нажмите для перехода к строке " + issue.Line + "." };
        }
        return new(issue.Code, "Результат локальной проверки", issue.Message, "Нажмите, чтобы перейти к строке " + issue.Line + ".");
    }
    public void AttachSyntax()
    {
        var layer = AdornerLayer.GetAdornerLayer(CodeEditor);
        if (syntax != null || layer == null) return;
        syntax = new SyntaxLayer(CodeEditor); layer.Add(syntax); CodeEditor.Foreground = Brushes.Transparent;
        syntax.Update(Current);
    }
    void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Tagline == null) return;
        Tagline.Visibility = e.NewSize.Width < 1280 ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(e.NewSize.Width < 1250 ? 210 : 236);
        InspectorColumn.Width = new GridLength(e.NewSize.Width < 1250 ? 280 : 325);
        if (!codeOnly) GraphRow.Height = new GridLength(e.NewSize.Height < 800 ? 190 : 290);
        if (Current != null) RenderGraph();
    }
    void Code_Changed(object sender, TextChangedEventArgs e)
    {
        if (internalUpdate || CodeEditor == null) return;
        dirty = true; debounce.Stop(); debounce.Start(); SetStatus("Изменения в коде · анализ после ввода…");
        // Lock stale editable spans, without disabling/repainting the entire panel.
        ConditionBox.IsReadOnly = ActionBox.IsReadOnly = ElseBox.IsReadOnly = true;
        ApplyCardButton.IsEnabled = false; UseElse.IsEnabled = false;
        // Restore readable native text until the new token positions are ready.
        if (syntax != null) { syntax.Visibility = Visibility.Hidden; CodeEditor.Foreground = new SolidColorBrush(Color.FromRgb(219, 230, 223)); }
    }
    void Caret_Changed(object sender, RoutedEventArgs e)
    {
        if (CodeEditor == null || CaretLabel == null) return;
        int line = CodeEditor.GetLineIndexFromCharacterIndex(CodeEditor.CaretIndex);
        CaretLabel.Text = $"строка {line + 1}  ·  Ctrl+F поиск";
        if (internalUpdate || Current == null || cardDirty || debounce.IsEnabled) return;
        if (mode == "functions")
        {
            var call = Current.Calls.Where(c => c.Start <= CodeEditor.CaretIndex && c.End >= CodeEditor.CaretIndex).MinBy(c => c.End - c.Start);
            if (call != null && Registry.Commands.TryGetValue(call.Name, out var cmd)) ShowCommand(cmd);
        }
        else if (mode == "scripts") SyncSelectionToCaret();
    }
    void SyncSelectionToCaret()
    {
        if (Current == null || Current.Text != CodeEditor.Text || cardDirty || mode != "scripts") return;
        var block = Current.BlockAt(CodeEditor.CaretIndex);
        if (block == null || Selected?.Name == block.Name) return;
        Selected = block;
        navigating = true;
        Navigation.SelectedItem = Navigation.Items.OfType<NavRow>().FirstOrDefault(r => r.Kind == "block" && ((ScriptBlock)r.Value).Name == block.Name);
        if (Navigation.SelectedItem != null) Navigation.ScrollIntoView(Navigation.SelectedItem);
        navigating = false;
        FillCard(block); RenderGraph();
    }
    void Search_Changed(object sender, TextChangedEventArgs e) { if (Current != null) FillNavigation(); }
    void Scripts_Click(object sender, RoutedEventArgs e) { if (!ResolveCard()) return; mode = "scripts"; FillNavigation(); if (Selected != null) FillCard(Selected); }
    void Functions_Click(object sender, RoutedEventArgs e) { if (!ResolveCard()) return; mode = "functions"; FillNavigation(); }
    void Flags_Click(object sender, RoutedEventArgs e) { if (!ResolveCard()) return; mode = "flags"; FillNavigation(); }
    void FillNavigation()
    {
        if (Navigation == null || Current == null) return;
        navigating = true;
        string query = SearchBox.Text.Trim();
        var rows = new List<NavRow>();
        if (mode == "scripts")
        {
            foreach (var b in Current.Blocks)
            {
                bool match = b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (query.Length > 0 && Workspace.Text[b.Start..b.End].Contains(query, StringComparison.OrdinalIgnoreCase));
                if (match) rows.Add(new(b.Display, $"стр. {b.Line}  ·  {b.Calls.Count} вызовов", "block", b));
            }
            rows.AddRange(folderFiles.Where(p => System.IO.Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase)).Select(p => new NavRow(System.IO.Path.GetFileName(p), "MOB из открытой папки", "file", p)));
        }
        else if (mode == "functions")
        {
            rows.AddRange(Registry.Commands.Values.Where(c => (c.Name + " " + c.Note + " " + Help.Entries[c.Name].Purpose).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name).Select(c => new NavRow(c.Name, c.DebugOnly ? "Только Debug · недоступна" : c.Internal ? "Служебная · if/else" : c.OverridesNative ? "Расширена аддоном" : c.Native ? "Штатная · " + HelpService.TypeName(c.ReturnType) : "Команда аддона", "command", c)));
        }
        else rows.AddRange(Current.Flags.Concat(indexedMaps.Where(m => !m.Path.Equals(Workspace.SourcePath, StringComparison.OrdinalIgnoreCase)).SelectMany(m => m.Flags)).GroupBy(f => (f.Context, f.Key)).Where(g => g.Key.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Key.Key).Select(g => new NavRow(g.Key.Key, $"контекст {g.Key.Context} · чтение {g.Count(f => !f.Write)} / запись {g.Count(f => f.Write)}", "flag", g.ToList())));
        Navigation.ItemsSource = rows;
        if (Selected != null && mode == "scripts") Navigation.SelectedItem = rows.FirstOrDefault(r => r.Kind == "block" && ((ScriptBlock)r.Value).Name == Selected.Name);
        if (Navigation.SelectedItem != null && mode == "scripts") Navigation.ScrollIntoView(Navigation.SelectedItem);
        navigating = false;
    }
    void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (navigating || Navigation.SelectedItem is not NavRow row) return;
        if (!ResolveCard()) { FillNavigation(); return; }
        if (debounce.IsEnabled) RefreshAnalysis(false);
        switch (row.Kind)
        {
            case "block": SelectBlock(((ScriptBlock)row.Value).Name, true); break;
            case "command": ShowCommand((CommandInfo)row.Value); break;
            case "flag": ShowFlag((List<FlagUse>)row.Value); break;
            case "file": OpenPath((string)row.Value); break;
        }
    }
    public void SelectBlock(string name, bool goToCode)
    {
        if (!ResolveCard()) return;
        if (debounce.IsEnabled) RefreshAnalysis(false);
        Selected = Current.Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Selected == null) return;
        mode = "scripts"; FillNavigation(); FillCard(Selected); RenderGraph();
        if (goToCode) GoTo(Selected.Start);
    }
    void GoTo(int offset)
    {
        offset = Math.Clamp(offset, 0, CodeEditor.Text.Length);
        internalUpdate = true; CodeEditor.Focus(); CodeEditor.CaretIndex = offset;
        CodeEditor.ScrollToLine(Math.Max(0, CodeEditor.GetLineIndexFromCharacterIndex(offset) - 2)); internalUpdate = false;
        SyncSelectionToCaret();
    }
    void FillCard(ScriptBlock block)
    {
        internalUpdate = true;
        CardPanel.Visibility = Visibility.Visible; HelpPanel.Visibility = Visibility.Collapsed;
        InspectorTitle.Text = block.Display;
        InspectorTitle.ToolTip = MakeTip(Help.Script(Current, block));
        InspectorHint.Text = block.World ? "Стартовые команды карты. Редактируйте их в коде; ниже можно добавить вызов этапа." : block.CardEditable ? "Одна ветка условий. Карточка изменяет только выбранные фрагменты кода." : "Сложная структура. Правьте код: автоматическая перестройка такого Script отключена.";
        ConditionBox.Text = block.Conditions == null ? "" : Current.Slice(block.Conditions);
        ActionBox.Text = block.Actions == null ? (block.World ? Current.Slice(block.Body) : "") : Current.Slice(block.Actions);
        ElseBox.Text = block.ElseActions == null ? "" : Current.Slice(block.ElseActions);
        ElsePanel.Visibility = block.CardEditable || block.ElseActions != null ? Visibility.Visible : Visibility.Collapsed;
        UseElse.IsChecked = block.ElseActions != null; UseElse.IsEnabled = block.CardEditable;
        ElseBox.IsEnabled = UseElse.IsChecked == true;
        ConditionBox.IsReadOnly = ActionBox.IsReadOnly = ElseBox.IsReadOnly = !block.CardEditable;
        ApplyCardButton.IsEnabled = block.CardEditable;
        LinkTargets.ItemsSource = Current.Blocks.Where(b => !b.World && b.Name != block.Name && Current.Declarations.TryGetValue(b.Name, out var p) && p.Length == 0).Select(b => b.Name).ToList();
        internalUpdate = false;
    }
    void Card_Changed(object sender, TextChangedEventArgs e)
    {
        if (internalUpdate || Current == null) return;
        cardDirty = true; CodeEditor.IsReadOnly = true; SetStatus("Карточка изменена. Примените её к коду перед следующей операцией.");
    }
    void Else_Changed(object sender, RoutedEventArgs e)
    {
        if (ElseBox == null) return;
        ElseBox.IsEnabled = UseElse.IsChecked == true;
        if (internalUpdate || Current == null) return;
        cardDirty = true; CodeEditor.IsReadOnly = true;
        SetStatus("Изменение else применится вместе с карточкой. Пустой else тоже меняет порядок проверки условия.");
    }
    public bool ApplyCard()
    {
        if (Selected == null || !Selected.CardEditable) return false;
        try
        {
            var original = Workspace.Text;
            Workspace.ApplyCard(Selected, ConditionBox.Text, ActionBox.Text, ElseBox.Text, UseElse.IsChecked == true);
            var proposed = ScriptAnalyzer.Analyze(Workspace.Text, Registry);
            // Editing spans may introduce syntax errors; keep them visible, never silently repair.
            string updated = Workspace.Text; Workspace.Text = original;
            ReplaceEditor(updated); SetStatus($"Карточка применена · замечаний: {proposed.Issues.Count}. Ctrl+Z отменяет правку."); return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }
    void ApplyCard_Click(object sender, RoutedEventArgs e) { if (debounce.IsEnabled) RefreshAnalysis(false); ApplyCard(); }
    public void ReplaceEditor(string text)
    {
        internalUpdate = true; cardDirty = false; CodeEditor.IsReadOnly = false;
        int caret = CodeEditor.CaretIndex;
        CodeEditor.BeginChange(); CodeEditor.SelectAll(); CodeEditor.SelectedText = text; CodeEditor.EndChange();
        CodeEditor.Select(Math.Min(caret, text.Length), 0); internalUpdate = false;
        Workspace.Text = text; RefreshAnalysis(false);
    }
    void ShowCommand(CommandInfo command)
    {
        ShowHelp(Help.Command(command.Name));
        SignatureText.Text = command.Signature;
        UsageList.ItemsSource = Current.Calls.Where(c => c.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)).Select(c => new UsageRow($"{c.Owner} · строка {c.Line}", c.Start)).ToList();
    }
    void ShowHelp(HoverHelp help)
    {
        CardPanel.Visibility = Visibility.Collapsed; HelpPanel.Visibility = Visibility.Visible;
        InspectorTitle.Text = help.Title; InspectorTitle.ToolTip = MakeTip(help); InspectorHint.Text = help.Kind;
        SignatureText.Text = help.Title; HelpText.Text = help.Body; HelpProvenance.Text = help.Source;
        UsageList.ItemsSource = Current.Calls.Where(c => c.Name.Equals(help.Title, StringComparison.OrdinalIgnoreCase)).Select(c => new UsageRow($"{c.Owner} · строка {c.Line}", c.Start)).ToList();
    }
    void ShowFlag(List<FlagUse> uses)
    {
        var first = uses[0]; CardPanel.Visibility = Visibility.Collapsed; HelpPanel.Visibility = Visibility.Visible;
        InspectorTitle.Text = first.Key; InspectorHint.Text = "GS-переменная · буквальный ключ";
        SignatureText.Text = "Контекст: " + first.Context;
        HelpText.Text = $"Чтений: {uses.Count(f => !f.Write)}\nЗаписей: {uses.Count(f => f.Write)}\n\n" + (uses.Any(f => f.Write) ? "Переходите к каждой записи, чтобы проверить условия её выполнения." : "В открытом документе и индексе папки нет записи. Значение может приходить из БД или обработчика движка.");
        HelpProvenance.Text = "Буквальные ключи GSGetVar / GSSetVar / GSSetVarMax. Индекс папки — снимок на момент открытия. Совпадение ключей не доказывает достижимость этапа.";
        var rows = Current.Flags.Where(f => f.Context == first.Context && f.Key == first.Key).Select(f => new UsageRow($"{(f.Write ? "ЗАПИСЬ " + f.Value : "ЧТЕНИЕ")} · {f.Owner} · {f.Line}", f.Offset)).ToList();
        rows.AddRange(indexedMaps.Where(m => !m.Path.Equals(Workspace.SourcePath, StringComparison.OrdinalIgnoreCase)).SelectMany(m => m.Flags.Where(f => f.Context == first.Context && f.Key == first.Key).Select(f => new UsageRow($"{System.IO.Path.GetFileName(m.Path)} · {(f.Write ? "ЗАПИСЬ" : "ЧТЕНИЕ")} · {f.Owner}:{f.Line}", f.Offset, m.Path, m.Hash))));
        UsageList.ItemsSource = rows;
    }
    void Usage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UsageList.SelectedItem is not UsageRow row) return;
        if (row.Path != null)
        {
            try
            {
                var workspace = Workspace.Open(row.Path);
                if (workspace.Mob?.Hash != row.Hash) throw new IOException("Карта изменилась после индексации. Заново откройте папку MOB.");
                if (!CanLeave()) return;
                LoadWorkspace(workspace, false);
            }
            catch (Exception ex) { Error(ex); return; }
        }
        GoTo(row.Offset);
    }
    void Diagnostic_Changed(object sender, SelectionChangedEventArgs e) { if (Diagnostics.SelectedItem is Issue issue) GoTo(issue.Offset); }
    void Link_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolveCard() || Selected == null || LinkTargets.SelectedItem is not string target) { SetStatus("Выберите Script в списке для добавления вызова."); return; }
        try { RefreshAnalysis(false); Workspace.Link(Current, Selected!, target); ReplaceEditor(Workspace.Text); SetStatus("Вызов добавлен в конец then. Это запуск экземпляра Script."); }
        catch (Exception ex) { Error(ex); }
    }
    void AddStage_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolveCard()) return;
        RefreshAnalysis(false); string name = Workspace.AddStage(Current); ReplaceEditor(Workspace.Text); SelectBlock(name, true);
        SetStatus("Этап добавлен. Свяжите вызов с другим этапом или с WorldScript.");
    }
    void New_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeave()) return;
        folderVersion++; indexedMaps.Clear(); folderFiles.Clear(); LoadWorkspace(new() { Name = "Новая цепочка", Text = "GlobalVars\r\n(\r\n  Heroes : group\r\n)\r\n\r\nWorldScript\r\n(\r\n)\r\n" }, true);
    }
    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Сценарии и проекты|*.mob;*.txt;*.scr;*.malachite|Все файлы|*.*" };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }
    public void OpenPath(string path)
    {
        if (!CanLeave()) return;
        try { var document = Workspace.Open(path); LoadWorkspace(document, false); }
        catch (Exception ex) { Error(ex); }
    }
    async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка с MOB-картами" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (!ResolveCard()) return;
            folderFiles.Clear(); folderFiles.AddRange(Directory.EnumerateFiles(dialog.FolderName, "*.mob", SearchOption.TopDirectoryOnly).OrderBy(p => p));
            int version = ++folderVersion; var files = folderFiles.ToArray(); indexedMaps.Clear();
            mode = "scripts"; FillNavigation(); SetStatus($"Чтение GS-связей из {files.Length} MOB…");
            var result = await Task.Run(() => IndexFolder(files, Registry));
            if (version != folderVersion) return;
            indexedMaps.AddRange(result.Maps); FillNavigation();
            SetStatus($"Индекс: {result.Maps.Count} карт, {result.Errors.Count} пропущено. GS показывает связи между картами.");
            if (result.Errors.Count > 0) Diagnostics.ItemsSource = result.Errors.Select(s => new Issue("warning", "INDEX", s, 0, 1)).ToList();
        }
        catch (Exception ex) { Error(ex); }
    }
    bool SaveProject()
    {
        if (!ResolveCard()) return false;
        RefreshAnalysis(false);
        var dialog = new SaveFileDialog { Filter = "Проект Малахит|*.malachite", FileName = System.IO.Path.GetFileNameWithoutExtension(Workspace.Name) + "-" + DateTime.Now.ToString("HHmmss") + ".malachite", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return false;
        try { SafeFiles.WriteNew(dialog.FileName, Workspace.SaveProject()); savedText = Workspace.Text; dirty = false; SetStatus("Проект сохранён: " + dialog.FileName); Title = Workspace.Name + " — Малахит"; return true; }
        catch (Exception ex) { Error(ex); return false; }
    }
    void Save_Click(object sender, RoutedEventArgs e) => SaveProject();
    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolveCard()) return;
        RefreshAnalysis(false);
        var dialog = new SaveFileDialog { Filter = Workspace.Mob == null ? "Текст сценария|*.txt" : "MOB с изменённым скриптом|*.mob|Текст сценария|*.txt", FileName = System.IO.Path.GetFileNameWithoutExtension(Workspace.Name) + "-edited", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            bool mob = System.IO.Path.GetExtension(dialog.FileName).Equals(".mob", StringComparison.OrdinalIgnoreCase);
            if (mob && Current.Issues.Any(i => i.Severity == "error")) throw new InvalidOperationException("Исправьте локальные ошибки перед экспортом MOB. Проект и текст можно сохранить сейчас.");
            byte[] bytes = mob ? (Workspace.Mob ?? throw new InvalidOperationException("Для MOB-экспорта сначала откройте MOB-контейнер.")).Encode(Workspace.Text) : Workspace.ExportText();
            // Choosing a game's existing source can never overwrite it.
            SafeFiles.WriteNew(dialog.FileName, bytes); SetStatus("Экспортирован новый файл: " + dialog.FileName);
        }
        catch (Exception ex) { Error(ex); }
    }
    void Check_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolveCard()) return;
        RefreshAnalysis(false); SetStatus("Проверены доступные статические правила. Компилятор игры не запускался.");
    }
    public static (List<IndexedMap> Maps, List<string> Errors) IndexFolder(string[] files, CommandRegistry registry)
    {
        var maps = new List<IndexedMap>(); var errors = new List<string>();
        foreach (var path in files)
        {
            try { var document = Workspace.Open(path); var analysis = ScriptAnalyzer.Analyze(document.Text, registry); maps.Add(new(path, document.Mob!.Hash, analysis.Flags)); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is DecoderFallbackException) { errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
        }
        return (maps, errors);
    }
    void Mixed_Click(object sender, RoutedEventArgs e) { codeOnly = false; GraphRow.Height = new GridLength(Surface.ActualHeight < 800 ? 190 : 290); }
    void Code_Click(object sender, RoutedEventArgs e) { codeOnly = true; GraphRow.Height = new GridLength(0); }

    public void RenderGraph()
    {
        GraphCanvas.Children.Clear();
        var center = Selected ?? Current.Blocks.FirstOrDefault();
        if (center == null) return;
        var byName = Current.Blocks.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var incoming = Current.Blocks.Where(b => b != center && b.Calls.Any(c => c.Name.Equals(center.Name, StringComparison.OrdinalIgnoreCase))).Take(5).ToArray();
        var outgoing = center.Calls.Where(c => byName.ContainsKey(c.Name) && !c.Name.Equals(center.Name, StringComparison.OrdinalIgnoreCase)).Select(c => byName[c.Name]).Distinct().Take(5).ToArray();
        int height = Math.Max((int)Math.Max(140, GraphRow.Height.Value - 40), Math.Max(incoming.Length, outgoing.Length) * 102 + 20);
        GraphCanvas.Width = 712; GraphCanvas.Height = height;
        // Fine drafting grid, deliberately quieter than the actual links.
        for (int x = 12; x < 712; x += 24) for (int y = 12; y < height; y += 24)
        { var dot = new Ellipse { Width = 1.5, Height = 1.5, Fill = new SolidColorBrush(Color.FromRgb(47, 67, 55)), IsHitTestVisible = false }; Canvas.SetLeft(dot, x); Canvas.SetTop(dot, y); GraphCanvas.Children.Add(dot); }
        double centerY = Math.Max(25, height / 2.0 - 40);
        foreach (var pair in incoming.Select((b, i) => (b, i))) DrawEdge(216, 28 + pair.i * 102 + 34, 257, centerY + 34);
        foreach (var pair in outgoing.Select((b, i) => (b, i))) DrawEdge(455, centerY + 34, 498, 28 + pair.i * 102 + 34);
        foreach (var pair in incoming.Select((b, i) => (b, i))) DrawNode(pair.b, 18, 28 + pair.i * 102, false);
        DrawNode(center, 257, centerY, true);
        foreach (var pair in outgoing.Select((b, i) => (b, i))) DrawNode(pair.b, 498, 28 + pair.i * 102, false);
        if (incoming.Length + outgoing.Length == 0)
        { var note = new TextBlock { Text = "Прямых связей с другими Script не найдено", Foreground = new SolidColorBrush(Color.FromRgb(141, 166, 148)), FontSize = 11 }; Canvas.SetLeft(note, 210); Canvas.SetTop(note, centerY + 90); GraphCanvas.Children.Add(note); }
    }
    void DrawEdge(double x1, double y1, double x2, double y2)
    {
        var geometry = new PathGeometry(); var figure = new PathFigure { StartPoint = new(x1, y1), IsClosed = false };
        figure.Segments.Add(new BezierSegment(new(x1 + 25, y1), new(x2 - 25, y2), new(x2, y2), true)); geometry.Figures.Add(figure);
        var stroke = new SolidColorBrush(Color.FromRgb(91, 170, 124));
        GraphCanvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = stroke, StrokeThickness = 1.6, IsHitTestVisible = false });
        GraphCanvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(FormattableString.Invariant($"M {x2 - 6},{y2 - 4} L {x2},{y2} {x2 - 6},{y2 + 4}")), Stroke = stroke, StrokeThickness = 1.6 });
    }
    void DrawNode(ScriptBlock block, double x, double y, bool selected)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = block.World ? "ВХОД · WORLDSCRIPT" : "SCRIPT · " + block.Line, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(143, 186, 158)) });
        stack.Children.Add(new TextBlock { Text = block.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        stack.Children.Add(new TextBlock { Text = block.World ? "Запуск карты" : $"{block.Calls.Count} вызовов · {(block.CardEditable ? "карточка + код" : "код")}", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(157, 180, 164)) });
        var button = new Button { Content = stack, Width = 198, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0), Background = new SolidColorBrush(selected ? Color.FromRgb(40, 77, 56) : Color.FromRgb(30, 45, 37)), BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(101, 199, 143) : Color.FromRgb(61, 85, 69)), ToolTip = "Описание скрипта" };
        ToolTipService.SetInitialShowDelay(button, 450); ToolTipService.SetShowDuration(button, 60000);
        button.ToolTipOpening += (_, _) => button.ToolTip = MakeTip(Help.Symbol(Current, block.Name));
        button.Click += (_, _) => SelectBlock(block.Name, true); Canvas.SetLeft(button, x); Canvas.SetTop(button, y); GraphCanvas.Children.Add(button);
    }
}
