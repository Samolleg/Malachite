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
    bool internalUpdate, dirty;
    RecoveryState? legacyCard;
    bool cardDirty => legacyCard?.CardDirty == true;
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
    string mode = "quests", savedText = Workspace.Demo;
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
        documentOpen = false; ShowHome(true); EncodingText.Text = ""; SetStatus("Выберите карту или откройте файл.");
        PreviewKeyDown += Window_KeyDown;

        CodeEditor.Loaded += (_, _) => AttachSyntax();
        hovers.Add(new HoverController(this, CodeEditor));
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
        var choice = MessageBox.Show(Window.GetWindow(this), "Восстановлен неприменённый черновик старой карточки. Перенести его в код? Нет — отбросить черновик; Отмена — сохранить его в recovery.", "Карточка этапа", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return RestoreLegacyCard();
        legacyCard = null; CodeEditor.IsReadOnly = false; PublishDocument("Отбросить черновик карточки"); return true;
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
        CloseCurrentDocument();
        documentOpen = true; ShowHome(false);
        if (!suppressPublish) saveArtifactKind = ArtifactForPath(workspace.SourcePath);
        debounce.Stop(); internalUpdate = true;
        Workspace = workspace; CodeEditor.IsUndoEnabled = false; CodeEditor.Text = workspace.Text; CodeEditor.IsUndoEnabled = documentHost == null; CodeEditor.IsReadOnly = false;
        mode = "quests"; ResetNavigator(); SearchBox.Text = "";
        legacyCard = null; Selected = null; savedText = modified ? "" : workspace.Text; dirty = modified;
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
        Current = result;
        if (syntax != null) { syntax.Update(Current); syntax.Visibility = Visibility.Visible; CodeEditor.Foreground = Brushes.Transparent; }
        var selectedName = Selected?.Name;
        Selected = selectedName == null ? null : Current.Blocks.FirstOrDefault(b => b.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        if (textChanged && !chooseFirst && !cardDirty) Selected = Current.BlockAt(CodeEditor.CaretIndex) ?? Selected;
        if (Selected == null && chooseFirst) Selected = Current.Blocks.FirstOrDefault(b => !b.World) ?? Current.Blocks.FirstOrDefault();
        DocumentSummary.Text = $"{CountLabel(Current.Blocks.Count(b => !b.World), "скрипт", "скрипта", "скриптов")}  /  {CountLabel(Current.Calls.Count, "вызов", "вызова", "вызовов")}  /  {Current.Flags.Select(f => (f.Context, f.Key)).Distinct().Count()} GS-ключей";
        int errors = Current.Issues.Count(i => i.Severity == "error"), warnings = Current.Issues.Count(i => i.Severity == "warning");
        CheckSummary.Text = $"JamaisVu · {Registry.AddonReleaseCount} команд аддона · {errors} ошибок · {warnings} предупреждений · без запуска движка";
        Diagnostics.ItemsSource = Current.Issues.Take(500).ToList();
        if (Current.Issues.Count == 0) Diagnostics.ItemsSource = new[] { new Issue("info", "CLEAN", "В рамках локальных правил замечаний нет. Результат не заменяет компиляцию движком.", 0, 1) };
        FillNavigation();
        RefreshQuestAtlas();
        if (!cardDirty && Selected != null) FillCard(Selected);
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
        if (item.Content is NavigatorRow nav) { item.ToolTip = AtlasNote(nav.Tooltip); return; }
        if (item.Content is MapQuestItem quest) { item.ToolTip = AtlasNote(quest.Proof); return; }
        if (item.Content is HomeMap map) { item.ToolTip = map.Path; return; }
        HoverHelp? help = item.Content switch
        {
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
        SidebarColumn.Width = new GridLength(e.NewSize.Width < 1250 ? 250 : 310);
        InspectorColumn.Width = new GridLength(e.NewSize.Width < 1250 ? 280 : 350);
    }
    void Code_Changed(object sender, TextChangedEventArgs e)
    {
        if (internalUpdate || CodeEditor == null) return;
        analysisRevision++; dirty = true; debounce.Stop(); debounce.Start(); SetStatus("Изменения в коде · анализ после ввода…");
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
        SyncSelectionToCaret();
        ExplainAt(CodeEditor.CaretIndex);
    }
    void SyncSelectionToCaret()
    {
        if (Current == null || Current.Text != CodeEditor.Text || cardDirty) return;
        var block = CurrentCodeContext().At(CodeEditor.CaretIndex).Block;
        if (Selected == block) return;
        Selected = block;
        if (block != null) FillCard(block);
    }
    public void SelectBlock(string name, bool goToCode)
    {
        if (!ResolveCard()) return;
        if (Current.Text != CodeEditor.Text) RefreshAnalysis(false);
        Selected = Current.Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Selected == null) return;
        FillCard(Selected);
        if (goToCode) GoToCode(Selected.Start);
    }
    void GoToCode(int offset)
    {
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
        HelpPanel.Visibility = Visibility.Collapsed;
        ExplanationContent.Visibility = Visibility.Visible;
    }
    // Only recovery from older versions can contain a pending card.
    // Keep its text until the owner transfers it to the code or discards it.
    bool RestoreLegacyCard()
    {
        if (legacyCard is not { } draft) return true;
        try
        {
            var block = Current.Blocks.FirstOrDefault(b => b.Name == draft.SelectedName);
            if (block == null || !block.CardEditable)
                throw new InvalidDataException("Старый черновик не соответствует Script. Он сохранён в recovery.");
            var original = Workspace.Text;
            string updated;
            try
            {
                Workspace.ApplyCard(block, draft.Conditions, draft.Actions, draft.Otherwise, draft.UseElse);
                updated = Workspace.Text;
            }
            finally { Workspace.Text = original; }
            ReplaceEditor(updated);
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }
    public void ReplaceEditor(string text)
    {
        internalUpdate = true; legacyCard = null; CodeEditor.IsReadOnly = false;
        int caret = CodeEditor.CaretIndex;
        CodeEditor.BeginChange(); CodeEditor.SelectAll(); CodeEditor.SelectedText = text; CodeEditor.EndChange();
        CodeEditor.Select(Math.Min(caret, text.Length), 0); internalUpdate = false;
        Workspace.Text = text; RefreshAnalysis(false);
        PublishDocument("Изменение сценария");
    }
    void ShowHelp(HoverHelp help)
    {
        explainedAnalysis = null;
        ExplanationContent.Visibility = Visibility.Collapsed;
        HelpPanel.Visibility = Visibility.Visible;
        InspectorTitle.Text = help.Title; InspectorTitle.ToolTip = MakeTip(help); InspectorHint.Text = help.Kind;
        SignatureText.Text = help.Title; HelpText.Text = help.Body; HelpProvenance.Text = help.Source;
        UsageList.ItemsSource = Current.Calls.Where(c => c.Name.Equals(help.Title, StringComparison.OrdinalIgnoreCase)).Select(c => new UsageRow($"{c.Owner} · строка {c.Line}", c.Start)).ToList();
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
            FillNavigation(); SetStatus($"Чтение GS-связей из {files.Length} MOB…");
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
        UpdateEditorBounds(); ExplainAt(CodeEditor.CaretIndex);
    }
}
