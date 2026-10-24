using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView : UserControl
{
    public DatabaseEditor.Modules.V1.IModuleHost Host { get; }
    public string Title { get; set; } = "Малахит";
    public CommandRegistry Registry { get; } = new();
    public HelpService Help { get; }
    public Workspace Workspace { get; private set; } = new();
    public Analysis Current { get; private set; } = null!;
    public ScriptBlock? Selected { get; private set; }
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(650) };
    readonly List<string> folderFiles = [];
    bool internalUpdate, dirty, cardDirty, navigating;
    SyntaxLayer? syntax;
    readonly List<HoverController> hovers = [];
    readonly List<IndexedMap> indexedMaps = [];
    int folderVersion;
    bool folderIndexing;
    string folderProgress = "";
    CancellationTokenSource? folderCancellation;
    void CancelFolderIndex()
    {
        homeRows.Clear();
        folderVersion++; folderCancellation?.Cancel(); folderCancellation = null;
        folderIndexing = false;
    }
    string mode = "scripts", savedText = Workspace.Demo;
    public sealed record NavRow(string Title, string Subtitle, string Kind, object Value);
    public sealed record UsageRow(string Text, int Offset, string? Path = null, string? Hash = null) { public override string ToString() => Text; }
    public sealed record IndexedMap(string Path, string Hash, List<FlagUse> Flags)
    {
        public QuestMap? Quests { get; init; }
        public int? ScriptCount { get; init; }
        public int? ObjectCount { get; init; }
    }

    public QuestView(DatabaseEditor.Modules.V1.IModuleHost host, string? catalogPath = null)
    {
        Host = host;
        catalog = new MapCatalogStore(catalogPath ?? MapCatalogStore.DefaultPath);
        Help = new HelpService(Registry);
        InitializeComponent();
        debounce.Tick += (_, _) => { debounce.Stop(); RefreshAnalysisInBackground(); };
        LoadWorkspace(new Workspace { Name = "", Text = "" }, false);
        documentOpen = false; ShowHome(true); EncodingText.Text = ""; SetStatus("Выберите карту, откройте файл или создайте новую цепочку.");
        PreviewKeyDown += Window_KeyDown;

        CodeEditor.Loaded += (_, _) => AttachSyntax();
        foreach (var editor in new[] { CodeEditor, ConditionBox, ActionBox, ElseBox }) hovers.Add(new HoverController(this, editor));
        InitializeDocuments();
        InitializeCompletion();
        CodeEditor.PreviewKeyDown += TextEditingKey;
        InitializeTextEditing();
        CodeEditor.SelectionChanged += NotifyDatabaseSelection;
        FolderButton.IsEnabled = Host.Files is IModuleFileBrowser;
        FolderButton.ToolTip = FolderButton.IsEnabled ? "Индекс GS-связей карт из папки, выбранной через редактор" : "Хост не поддерживает выбор папки MOB.";

    }
    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (workbench != null || templateOverlay != null)
        {
            if (e.Key == Key.Escape) { if (workbench != null) { captureComposerDraft?.Invoke(); captureComposerDraft = null; CancelWorkbenchSearch(); Surface.Children.Remove(workbench); workbench = null; } if (templateOverlay != null) { Surface.Children.Remove(templateOverlay); templateOverlay = null; } CodeEditor.Focus(); e.Handled = true; }
            return; // Form fields retain native editing shortcuts, including their own Undo.
        }
        if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey is Key.Left or Key.Right || e.Key is Key.Left or Key.Right))
        { try { NavigateHistory(e.SystemKey == Key.Left || e.Key == Key.Left ? -1 : 1); } catch (Exception ex) { Error(ex); } e.Handled = true; return; }
        if (e.Key == Key.F12 && CodeEditor.IsKeyboardFocusWithin)
        {
            if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
            var symbol = Current.Tokens.FirstOrDefault(t => t.Start <= CodeEditor.CaretIndex && t.End > CodeEditor.CaretIndex);
            if (symbol != null && Current.DeclarationRanges.TryGetValue(symbol.Value, out var declaration)) GoToCode(declaration.Start);
            e.Handled = true; return;
        }
        if (e.Handled) return;
        if (e.Key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) && activeTab != null)
        {
            int direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            SwitchDocument(openTabs[(openTabs.IndexOf(activeTab) + direction + openTabs.Count) % openTabs.Count].Id); e.Handled = true; return;
        }
        if (e.Key == Key.F3) { FindInCode(Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; return; }
        if (e.Key == Key.F5) { Check_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.F1 && !cardDirty)
        {
            if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
            var help = HelpAt(CodeEditor, CodeEditor.CaretIndex);
            if (help != null) ShowHelp(help);
            e.Handled = true;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Enter && CodeEditor.IsKeyboardFocusWithin) { NavigateSelectedDatabaseReference(); e.Handled = true; }
        if (e.Key == Key.O) { Open_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.S) { RunCommand(DatabaseEditor.Modules.V1.ModuleCommand.Save); e.Handled = true; }
        if (e.Key == Key.W && documentId != null) { CloseDocumentTab(documentId.Value); e.Handled = true; }
        if (e.Key is Key.Z or Key.Y && (FindText.IsKeyboardFocusWithin || ReplaceText.IsKeyboardFocusWithin || HomeSearch.IsKeyboardFocusWithin || HomeNote.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin)) return;
        if (e.Key == Key.Z) { RunCommand(DatabaseEditor.Modules.V1.ModuleCommand.Undo); e.Handled = true; }
        if (e.Key == Key.Y) { RunCommand(DatabaseEditor.Modules.V1.ModuleCommand.Redo); e.Handled = true; }
        if (e.Key == Key.H) { ShowFind(true); e.Handled = true; }
        if (e.Key == Key.F) { RunCommand(ModuleCommand.Find); e.Handled = true; }
    }
    void OnClosing(object? sender, CancelEventArgs e) { if (!CanLeave()) e.Cancel = true; }
    void Error(Exception ex) => MessageBox.Show(Window.GetWindow(this), ex.Message, "Малахит", MessageBoxButton.OK, MessageBoxImage.Warning);
    bool ResolveCard()
    {
        if (!cardDirty) return true;
        var choice = MessageBox.Show(Window.GetWindow(this), "Применить изменения карточки к коду?", "Карточка этапа", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return ApplyCard();
        cardDirty = false; CodeEditor.IsReadOnly = false; PublishDocument("Отбросить черновик карточки"); return true;
    }
    bool CanLeave()
    {
        if (!ResolveCard()) return false;
        if (!dirty) return true;
        var result = MessageBox.Show(Window.GetWindow(this), "Сохранить изменения в исходный документ перед закрытием?", "Изменённый документ", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        return result != MessageBoxResult.Yes || SaveChanges();
    }
    public void LoadWorkspace(Workspace workspace, bool modified)
    {
        if (documentOpen) RecordNavigation(CodeEditor.CaretIndex);
        if (workbench != null) { captureComposerDraft?.Invoke(); captureComposerDraft = null; CancelWorkbenchSearch(); Surface.Children.Remove(workbench); workbench = null; }
        CloseCurrentDocument();
        documentOpen = true; ShowHome(false);
        if (!suppressPublish) saveArtifactKind = ArtifactForPath(workspace.SourcePath);
        debounce.Stop(); internalUpdate = true;
        Workspace = workspace; CodeEditor.IsUndoEnabled = false; CodeEditor.Text = workspace.Text; CodeEditor.IsUndoEnabled = documentHost == null; CodeEditor.IsReadOnly = false;
        mode = "scripts"; SearchBox.Text = "";
        cardDirty = false; Selected = null; savedText = modified ? "" : workspace.Text; dirty = modified;
        internalUpdate = false; RefreshAnalysis(true); Code_Click(this, new RoutedEventArgs());
        ProjectName.Text = workspace.Name; DocumentTitle.Text = workspace.Name;
        EncodingText.Text = workspace.Mob != null ? "MOB · Windows-1251 · Ctrl+S — сохранить" : $"{workspace.TextEncoding.WebName} · Ctrl+S — сохранить";
        if (Selected != null) GoTo(Selected.Start);
        SetStatus(workspace.Mob != null ? "MOB открыт. После правок нажмите «Сохранить изменения» или Ctrl+S." : "Сценарий открыт. После правок нажмите «Сохранить изменения» или Ctrl+S.");
        RegisterCurrentDocument(null, modified);
    }
    public void RefreshAnalysis(bool chooseFirst)
    {
        analysisRevision++;
        debounce.Stop(); Workspace.Text = CodeEditor.Text;
        ApplyAnalysis(AnalyzeText(Workspace.Text), chooseFirst);
    }
    long analysisRevision;
    bool analysisRunning;
    Analysis AnalyzeText(string text)
    {
        try { return ScriptAnalyzer.Analyze(text, Registry); }
        catch (InvalidDataException ex) { var result = new Analysis { Text = text }; result.Add("error", "LIMIT", ex.Message, 0); return result; }
    }
    async void RefreshAnalysisInBackground()
    {
        if (stopped || internalUpdate) return;
        if (analysisRunning) { debounce.Start(); return; }
        analysisRunning = true;
        string text = CodeEditor.Text; long revision = analysisRevision; var workspace = Workspace;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => AnalyzeText(text));
            await Dispatcher.InvokeAsync(() => {
                if (!stopped && revision == analysisRevision && ReferenceEquals(workspace, Workspace) && text == CodeEditor.Text)
                { Workspace.Text = text; ApplyAnalysis(result, false); DatabaseSelectionChanged?.Invoke(this, EventArgs.Empty); }
            });
        }
        catch (Exception ex) { if (!stopped) SetStatus("Не удалось обновить анализ: " + ex.Message); }
        finally { analysisRunning = false; }
    }
    void ApplyAnalysis(Analysis result, bool chooseFirst)
    {
        bool textChanged = Current != null && Current.Text != result.Text;
        Current = result; CardPanel.IsEnabled = !result.Issues.Any(i => i.Code == "LIMIT");
        if (syntax != null) { syntax.Update(Current); syntax.Visibility = Visibility.Visible; CodeEditor.Foreground = Brushes.Transparent; }
        var selectedName = Selected?.Name;
        Selected = selectedName == null ? null : Current.Blocks.FirstOrDefault(b => b.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        if (textChanged && !chooseFirst && !cardDirty && mode == "scripts") Selected = Current.BlockAt(CodeEditor.CaretIndex) ?? Selected;
        if (Selected == null && chooseFirst) Selected = Current.Blocks.FirstOrDefault(b => !b.World) ?? Current.Blocks.FirstOrDefault();
        DocumentSummary.Text = $"{CountLabel(Current.Blocks.Count(b => !b.World), "скрипт", "скрипта", "скриптов")}  /  {CountLabel(Current.Calls.Count, "вызов", "вызова", "вызовов")}  /  {Current.Flags.Select(f => (f.Context, f.Key)).Distinct().Count()} GS-ключей";
        int errors = Current.Issues.Count(i => i.Severity == "error"), warnings = Current.Issues.Count(i => i.Severity == "warning");
        CheckSummary.Text = $"JamaisVu · {Registry.AddonReleaseCount} команд аддона · {errors} ошибок · {warnings} предупреждений · без запуска движка";
        Diagnostics.ItemsSource = Current.Issues.Take(500).ToList();
        if (Current.Issues.Count == 0) Diagnostics.ItemsSource = new[] { new Issue("info", "CLEAN", "В рамках локальных правил замечаний нет. Результат не заменяет компиляцию движком.", 0, 1) };
        FillNavigation();
        RefreshQuestAtlas();
        if (!cardDirty && Selected != null && mode == "scripts") FillCard(Selected);
        dirty = Workspace.Text != savedText;
        Title = $"{(dirty ? "● " : "")}{Workspace.Name} — Малахит";
        SetStatus($"Проверка обновлена · {errors} ошибок · {warnings} предупреждений.");
        ExplainAt(CodeEditor.CaretIndex); RefreshDocumentTabs();
    }
    void SetStatus(string text) { StatusText.Text = text; if (HomePanel?.Visibility == Visibility.Visible) UpdateHomeMaps(); }
    public HoverHelp? HelpAt(TextBox editor, int position)
    {
        if (position < 0 || position >= editor.Text.Length) return null;
        Analysis local;
        if (editor == CodeEditor) { if (Current.Text != editor.Text) return null; local = Current; }
        else { try { local = ScriptAnalyzer.Analyze(editor.Text, Registry); } catch (InvalidDataException) { return null; } }
        var objectHelp = ObjectHelp(local, position);
        if (objectHelp != null) return objectHelp;
        var token = local.Tokens.FirstOrDefault(t => t.Kind == "id" && t.Start <= position && t.End > position);
        return EnrichDatabaseHelp(local, position, token == null ? null : Help.Symbol(Current, token.Value, local, token.Start, editor == CodeEditor ? null : Selected?.Name));
    }
    public ToolTip MakeTip(HoverHelp help) => new() { Content = HelpView(help), Style = (Style)FindResource(typeof(ToolTip)) };
    public FrameworkElement HelpView(HoverHelp help)
    {
        var panel = new StackPanel { MaxWidth = 550 };
        panel.Children.Add(new TextBlock { Text = help.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = MalachiteTheme.Accent, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = help.Kind, Foreground = MalachiteTheme.Muted, FontSize = 11, Margin = new Thickness(0, 4, 0, 12) });
        panel.Children.Add(new TextBlock { Text = help.Body, Foreground = MalachiteTheme.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        panel.Children.Add(new TextBlock { Text = help.Source, Foreground = MalachiteTheme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 13, 0, 0) });
        AddDatabaseNavigation(panel, help);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 610 };
    }
    void ItemTip_Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.Content is MapQuestItem quest) { item.ToolTip = AtlasNote(quest.Proof); return; }
        if (item.Content is HomeMap map) { item.ToolTip = map.Path; return; }
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
        syntax = new SyntaxLayer(CodeEditor, EditorTokens); layer.Add(syntax); CodeEditor.Foreground = Brushes.Transparent;
        syntax.Update(Current);
    }
    void EditorArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateEditorBounds();
    void DiagnosticsPanel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateEditorBounds();
    void UpdateEditorBounds()
    {
        if (EditorArea == null || DiagnosticsRow == null) return;
        double viewport = Surface.ActualHeight - Surface.RowDefinitions[0].ActualHeight - Surface.RowDefinitions[2].ActualHeight - EditorArea.Margin.Top - EditorArea.Margin.Bottom;
        double available = viewport - EditorArea.RowDefinitions[0].ActualHeight - 13;
        CodeRow.MinHeight = Math.Min(180, Math.Max(154, available - DiagnosticsRow.Height.Value));
        DiagnosticsRow.MaxHeight = Math.Max(checksHidden ? 36 : 70, available - CodeRow.MinHeight);
    }
    void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Tagline == null) return;
        Tagline.Visibility = e.NewSize.Width < 1280 ? Visibility.Collapsed : Visibility.Visible;
        if (!sidebarHidden) SidebarColumn.Width = new GridLength(e.NewSize.Width < 1250 ? 210 : 236);
        if (!inspectorHidden) InspectorColumn.Width = new GridLength(e.NewSize.Width < 1250 ? 280 : 325);
    }
    void Code_Changed(object sender, TextChangedEventArgs e)
    {
        if (internalUpdate || CodeEditor == null) return;
        analysisRevision++; dirty = true; debounce.Stop(); debounce.Start(); SetStatus("Изменения в коде · анализ после ввода…");
        // Lock stale editable spans, without disabling/repainting the entire panel.
        ConditionBox.IsReadOnly = ActionBox.IsReadOnly = ElseBox.IsReadOnly = true;
        ApplyCardButton.IsEnabled = false; UseElse.IsEnabled = false;
        // Native glyphs stay immediately current; never paint stale syntax over edited text.
        if (syntax != null) { syntax.Visibility = Visibility.Hidden; CodeEditor.Foreground = MalachiteTheme.Text; }
        ScheduleEditorFeedback();
        PublishDocument("Ввод сценария");
        ExplainAt(CodeEditor.CaretIndex); RefreshDocumentTabs();
    }
    void Caret_Changed(object sender, RoutedEventArgs e)
    {
        if (CodeEditor == null || CaretLabel == null) return;
        int line = CodeEditor.GetLineIndexFromCharacterIndex(CodeEditor.CaretIndex);
        CaretLabel.Text = $"стр. {line + 1} · ст. {CodeEditor.CaretIndex - Math.Max(0, CodeEditor.GetCharacterIndexFromLineIndex(line)) + 1}";
        if (internalUpdate || Current == null || cardDirty || Current.Text != CodeEditor.Text) return;
        if (mode == "functions")
        {
            var call = Current.Calls.Where(c => c.Start <= CodeEditor.CaretIndex && c.End >= CodeEditor.CaretIndex).MinBy(c => c.End - c.Start);
            if (call != null && Registry.Commands.TryGetValue(call.Name, out var cmd)) ShowCommand(cmd);
        }
        else if (mode == "scripts") SyncSelectionToCaret();
        ExplainAt(CodeEditor.CaretIndex);
    }
    void SyncSelectionToCaret()
    {
        if (Current == null || Current.Text != CodeEditor.Text || cardDirty || mode != "scripts") return;
        var block = CurrentCodeContext().At(CodeEditor.CaretIndex).Block;
        if (Selected == block) return;
        Selected = block;
        navigating = true;
        Navigation.SelectedItem = Navigation.Items.OfType<NavRow>().FirstOrDefault(r => r.Kind == "block" && ((ScriptBlock)r.Value).Name == block?.Name);
        if (Navigation.SelectedItem != null) Navigation.ScrollIntoView(Navigation.SelectedItem);
        navigating = false;
        if (block != null) FillCard(block);
    }
    void Search_Changed(object sender, TextChangedEventArgs e) { if (Current != null) FillNavigation(); }
    void Scripts_Click(object sender, RoutedEventArgs e) { if (!ResolveCard()) return; mode = "scripts"; FillNavigation(); if (Selected != null) FillCard(Selected); }
    void Functions_Click(object sender, RoutedEventArgs e) { if (!ResolveCard()) return; mode = "functions"; FillNavigation(); }
    void Flags_Click(object sender, RoutedEventArgs e) { if (!ResolveCard()) return; mode = "flags"; FillNavigation(); }
    void FillNavigation()
    {
        if (Navigation == null || Current == null) return;
        UpdateHomeMaps();
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
        if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
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
        if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
        Selected = Current.Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Selected == null) return;
        mode = "scripts"; FillNavigation(); FillCard(Selected);
        if (goToCode) GoToCode(Selected.Start);
    }
    void GoToCode(int offset)
    {
        if (QuestAtlasPanel.Visibility == Visibility.Visible) Code_Click(this, new RoutedEventArgs());
        GoTo(offset);
    }
    void GoTo(int offset)
    {
        if (navigationIndex < 0 || (navigationHistory[navigationIndex].Path == (Workspace.SourcePath ?? "") && navigationHistory[navigationIndex].Name == Workspace.Name)) RecordNavigation(CodeEditor.CaretIndex);
        offset = Math.Clamp(offset, 0, CodeEditor.Text.Length);
        // Mode changes can shrink the native viewport. Lay it out before the
        // scroll request, otherwise WPF clamps against the preceding mode.
        UpdateLayout();
        internalUpdate = true; CodeEditor.Focus(); CodeEditor.CaretIndex = offset;
        CodeEditor.ScrollToLine(Math.Max(0, CodeEditor.GetLineIndexFromCharacterIndex(offset) - 2)); internalUpdate = false;
        SyncSelectionToCaret();
        RecordNavigation(offset);
        ExplainAt(offset);
    }
    void FillCard(ScriptBlock block)
    {
        explainedAnalysis = null;
        internalUpdate = true;
        CardPanel.Visibility = Visibility.Visible; HelpPanel.Visibility = Visibility.Collapsed;
        ExplanationContent.Visibility = Visibility.Visible;
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
        PublishDocument("Изменение карточки");
    }
    void Else_Changed(object sender, RoutedEventArgs e)
    {
        if (ElseBox == null) return;
        ElseBox.IsEnabled = UseElse.IsChecked == true;
        if (internalUpdate || Current == null) return;
        cardDirty = true; CodeEditor.IsReadOnly = true;
        SetStatus("Изменение else применится вместе с карточкой. Пустой else тоже меняет порядок проверки условия.");
        PublishDocument("Изменение ветки else");
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
    void ApplyCard_Click(object sender, RoutedEventArgs e) { if (Current.Text != CodeEditor.Text) RefreshAnalysis(false); ApplyCard(); }
    public void ReplaceEditor(string text)
    {
        internalUpdate = true; cardDirty = false; CodeEditor.IsReadOnly = false;
        int caret = CodeEditor.CaretIndex;
        CodeEditor.BeginChange(); CodeEditor.SelectAll(); CodeEditor.SelectedText = text; CodeEditor.EndChange();
        CodeEditor.Select(Math.Min(caret, text.Length), 0); internalUpdate = false;
        Workspace.Text = text; RefreshAnalysis(false);
        PublishDocument("Изменение сценария");
    }
    void ShowCommand(CommandInfo command)
    {
        ShowHelp(Help.Command(command.Name));
        SignatureText.Text = command.Signature;
        UsageList.ItemsSource = Current.Calls.Where(c => c.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)).Select(c => new UsageRow($"{c.Owner} · строка {c.Line}", c.Start)).ToList();
    }
    void ShowHelp(HoverHelp help)
    {
        explainedAnalysis = null;
        ExplanationContent.Visibility = Visibility.Collapsed;
        CardPanel.Visibility = Visibility.Collapsed; HelpPanel.Visibility = Visibility.Visible;
        InspectorTitle.Text = help.Title; InspectorTitle.ToolTip = MakeTip(help); InspectorHint.Text = help.Kind;
        SignatureText.Text = help.Title; HelpText.Text = help.Body; HelpProvenance.Text = help.Source;
        UsageList.ItemsSource = Current.Calls.Where(c => c.Name.Equals(help.Title, StringComparison.OrdinalIgnoreCase)).Select(c => new UsageRow($"{c.Owner} · строка {c.Line}", c.Start)).ToList();
    }
    void ShowFlag(List<FlagUse> uses)
    {
        explainedAnalysis = null;
        ExplanationContent.Visibility = Visibility.Collapsed;
        var first = uses[0]; CardPanel.Visibility = Visibility.Collapsed; HelpPanel.Visibility = Visibility.Visible;
        InspectorTitle.Text = first.Key; InspectorHint.Text = "GS-переменная · буквальный ключ";
        SignatureText.Text = "Контекст: " + first.Context;
        HelpText.Text = $"Чтений: {uses.Count(f => !f.Write)}\nЗаписей: {uses.Count(f => f.Write)}\n\n" + (uses.Any(f => f.Write) ? "Переходите к каждой записи, чтобы проверить условия её выполнения." : "В открытом документе и индексе папки нет записи. Значение может приходить из БД или обработчика движка.");
        HelpProvenance.Text = "Буквальные ключи GSGetVar / GSSetVar / GSSetVarMax / GSDelVar. Индекс папки — снимок на момент открытия. Совпадение ключей не доказывает достижимость этапа.";
        var rows = Current.Flags.Where(f => f.Context == first.Context && f.Key == first.Key).Select(f => new UsageRow($"{(f.Delete ? "УДАЛЕНИЕ" : f.Write ? "ЗАПИСЬ " + f.Value : "ЧТЕНИЕ")} · {f.Owner} · {f.Line}", f.Offset)).ToList();
        rows.AddRange(indexedMaps.Where(m => !m.Path.Equals(Workspace.SourcePath, StringComparison.OrdinalIgnoreCase)).SelectMany(m => m.Flags.Where(f => f.Context == first.Context && f.Key == first.Key).Select(f => new UsageRow($"{System.IO.Path.GetFileName(m.Path)} · {(f.Delete ? "УДАЛЕНИЕ" : f.Write ? "ЗАПИСЬ" : "ЧТЕНИЕ")} · {f.Owner}:{f.Line}", f.Offset, m.Path, m.Hash))));
        UsageList.ItemsSource = rows;
    }
    void Usage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UsageList.SelectedItem is not UsageRow row) return;
        if (row.Path != null)
        {
            try
            {
                if (Host.Files is not IModuleFileBrowser browser) throw new InvalidOperationException("Хост не поддерживает чтение связанных MOB.");
                EnsureIndexedTarget(row.Path);
                var file = browser.Read(row.Path);
                var workspace = Workspace.FromBytes(file.DisplayPath, file.Content.ToArray());
                if (workspace.Mob?.Hash != row.Hash) throw new IOException("Карта изменилась после индексации. Заново откройте папку MOB.");
                if (documentHost == null && !CanLeave()) return;
                LoadOpenedFile(file);
            }
            catch (Exception ex) { Error(ex); return; }
        }
        GoToCode(row.Offset);
    }
    void Diagnostic_Changed(object sender, SelectionChangedEventArgs e) { if (Diagnostics.SelectedItem is Issue issue) GoToCode(issue.Offset); }
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
        if (documentHost == null && !CanLeave()) return;
        if (documentHost != null) { if (openTabs.Count >= 64) { SetStatus("Закройте одну из 64 открытых вкладок."); return; } SuspendCurrentDocument(); }
        LoadWorkspace(new() { Name = "Новая цепочка", Text = "GlobalVars\r\n(\r\n  Heroes : group\r\n)\r\n\r\nWorldScript\r\n(\r\n)\r\n" }, true);
    }
    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (documentHost == null && !CanLeave()) return;
        try { var file = Host.Files.Open("Сценарии и проекты|*.mob;*.txt;*.scr;*.malachite");
            if (file is not null) LoadOpenedFile(file); }
        catch (Exception ex) { Error(ex); }
    }
    public void OpenPath(string path)
    {
        if (documentHost == null && !CanLeave()) return;
        try { if (Host.Files is not IModuleFileBrowser browser) throw new InvalidOperationException("Хост не поддерживает чтение по разрешённому пути."); LoadOpenedFile(browser.Read(path)); }
        catch (Exception ex) { Error(ex); }
    }
    async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (stopped || folderIndexing) return;
        if (Host.Files is not IModuleFileBrowser browser) { SetStatus("Хост не поддерживает выбор папки MOB."); return; }
        CancellationTokenSource? cancellation = null;
        try
        {
            if (!ResolveCard()) return;
            var selectedFiles = browser.SelectFolder(".mob");
            if (selectedFiles.IsEmpty) return;
            mapsStatus = "Папка MOB выбрана вручную на этот сеанс. При открытии БД используется соседняя Maps.";
            folderFiles.Clear(); folderFiles.AddRange(selectedFiles.OrderBy(p => p));
            int version = ++folderVersion; var files = folderFiles.ToArray();
            cancellation = new CancellationTokenSource(); folderCancellation = cancellation;
            folderIndexing = true; folderProgress = $"Индексация MOB: 0 / {files.Length}";
            atlasIndexErrors.Clear();
            mode = "scripts"; FillNavigation(); SetStatus($"Чтение GS-связей из {files.Length} MOB…");
            RefreshQuestAtlas();
            var progress = new Progress<(int Done, string Name)>(p =>
            {
                if (stopped || version != folderVersion || !folderIndexing) return;
                folderProgress = $"Индексация MOB: {p.Done} / {files.Length} · {p.Name}";
                AtlasStatus.Text = folderProgress; SetStatus(folderProgress);
            });
            var result = await Task.Run(() => IndexFolder(files, Registry, browser, (done, name) => ((IProgress<(int, string)>)progress).Report((done, name)), cancellation.Token));
            if (stopped || version != folderVersion) return;
            indexedMaps.Clear();
            indexedMaps.AddRange(result.Maps); FillNavigation();
            atlasIndexErrors.AddRange(result.Errors);
            folderIndexing = false;
            RefreshQuestAtlas();
            SetStatus($"Индекс: {result.Maps.Count} карт, {result.Errors.Count} пропущено. GS показывает связи между картами.");
            if (result.Errors.Count > 0) Diagnostics.ItemsSource = result.Errors.Select(s => new Issue("warning", "INDEX", s, 0, 1)).ToList();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!stopped) Error(ex); }
        finally
        {
            if (cancellation != null && ReferenceEquals(folderCancellation, cancellation))
            { folderCancellation = null; folderIndexing = false; if (!stopped) RefreshQuestAtlas(); }
            cancellation?.Dispose();
        }
    }
    bool SaveProject()
    {
        if (publishConflict) { Host.Report("Экспорт проекта остановлен из-за конфликта. Локальный ввод доступен в recovery."); return false; }
        if (!ResolveCard()) return false;
        RefreshAnalysis(false);
        var result = documentHost != null && documentId != null
            ? documentHost.Save.Export(documentId.Value, "malachite-project", System.IO.Path.GetFileNameWithoutExtension(Workspace.Name) + ".malachite", "Проект Малахит|*.malachite")
            : Host.Files.Export(System.IO.Path.GetFileNameWithoutExtension(Workspace.Name) + ".malachite", "Проект Малахит|*.malachite", Workspace.SaveProject());
        if (!result.Succeeded) { SetStatus(result.Message); return false; }
        SetStatus("Копия проекта .malachite сохранена. Исходный документ не изменён; для него нажмите «Сохранить изменения»."); return true;
    }
    void Save_Click(object sender, RoutedEventArgs e) => SaveProject();
    void SaveChanges_Click(object sender, RoutedEventArgs e) => SaveChanges();
    void Copies_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
    }
    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (publishConflict) { Host.Report("Экспорт остановлен из-за конфликта сценария."); return; }
        if (!ResolveCard()) return;
        RefreshAnalysis(false);
        try {
            bool mob = Workspace.Mob is not null;
            byte[] bytes = mob ? DocumentCodec.PrepareMob(Workspace) : Workspace.ExportText();
            var result = documentHost != null && documentId != null
                ? documentHost.Save.Export(documentId.Value, mob ? "mob" : "script-text", System.IO.Path.GetFileNameWithoutExtension(Workspace.Name) + "-edited" + (mob ? ".mob" : ".txt"), mob ? "MOB-копия|*.mob" : "Текст|*.txt")
                : Host.Files.Export(System.IO.Path.GetFileNameWithoutExtension(Workspace.Name) + "-edited" + (mob ? ".mob" : ".txt"), mob ? "MOB-копия|*.mob" : "Текст|*.txt", bytes);
            SetStatus(result.Succeeded ? "Экспортирована проверенная копия; исходный файл не изменён." : result.Message);
        } catch (Exception ex) { Error(ex); }
    }
    void Check_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolveCard()) return;
        RefreshAnalysis(false); SetStatus("Проверены доступные статические правила. Компилятор игры не запускался.");
    }
    public static (List<IndexedMap> Maps, List<string> Errors) IndexFolder(string[] files, CommandRegistry registry, IModuleFileBrowser browser, Action<int, string>? progress = null, CancellationToken cancellation = default)
    {
        var maps = new List<IndexedMap>(); var errors = new List<string>();
        foreach (var path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            try { var file = browser.Read(path); cancellation.ThrowIfCancellationRequested(); var document = Workspace.FromBytes(file.DisplayPath, file.Content.ToArray()); var analysis = ScriptAnalyzer.Analyze(document.Text, registry); cancellation.ThrowIfCancellationRequested(); maps.Add(MapIndexCache.Describe(document, analysis, path, document.Mob!.Hash)); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is DecoderFallbackException) { errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
            progress?.Invoke(maps.Count + errors.Count, System.IO.Path.GetFileName(path));
        }
        return (maps, errors);
    }
    void Code_Click(object sender, RoutedEventArgs e)
    {
        QuestAtlasPanel.Visibility = Visibility.Collapsed;
        CodeButton.BorderBrush = MalachiteTheme.Accent;
        UpdateEditorBounds(); ExplainAt(CodeEditor.CaretIndex);
    }
}
