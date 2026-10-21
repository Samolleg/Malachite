using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class ComfortVerification
{
    public static void Run(Action<bool, string> check, string output)
    {
        string root = Path.GetFullPath(Path.Combine(output, "comfort")); Directory.CreateDirectory(root);
        string catalogPath = Path.Combine(root, "personal.json");
        if (File.Exists(catalogPath)) File.Delete(catalogPath); // Exact private fixture only.
        var store = new MapCatalogStore(catalogPath); var second = new MapCatalogStore(catalogPath);
        string aPath = Path.Combine(root, "a.mob"), bPath = Path.Combine(root, "b.mob");
        store.Update(aPath, old => old with { Favorite = true, Note = "Проверить охрану" });
        second.Update(bPath, old => old with { Note = "Восточные ворота" });
        second.Update(aPath, old => old with { LastOpenedUtc = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc) });
        var reopened = new MapCatalogStore(catalogPath);
        check(reopened.Get(aPath).Favorite && reopened.Get(aPath).Note == "Проверить охрану" && reopened.Get(bPath).Note == "Восточные ворота", "comfort personal metadata merges independent instances and survives restart");
        byte[] validCatalog = File.ReadAllBytes(catalogPath);
        try { store.Update(aPath, old => old with { Note = new string('x', 501) }); check(false, "comfort note length bounded"); }
        catch (ArgumentException) { check(validCatalog.SequenceEqual(File.ReadAllBytes(catalogPath)), "comfort oversized note preserves last valid catalog"); }
        string brokenPath = Path.Combine(root, "broken.json"); File.WriteAllText(brokenPath, "broken fixture");
        try { new MapCatalogStore(brokenPath).Update(aPath, _ => new(true)); check(false, "comfort corrupt catalog protected"); }
        catch (IOException) { check(File.ReadAllText(brokenPath) == "broken fixture", "comfort corrupt catalog never overwritten by empty defaults"); }

        const string codeA = "DeclareScript A()\r\nScript A\r\n(\r\n  if () then\r\n  (\r\n    Sleep(5)\r\n  )\r\n)\r\nWorldScript\r\n(\r\n  A()\r\n)\r\n";
        const string codeB = "DeclareScript B()\r\nScript B(if()then(ConsoleString(\"b\")))\r\nWorldScript(B())";
        File.WriteAllBytes(aPath, MapCacheVerification.Mob(codeA)); File.WriteAllBytes(bPath, MapCacheVerification.Mob(codeB));
        byte[] sourceBytes = File.ReadAllBytes(aPath); DateTime modified = File.GetLastWriteTimeUtc(aPath);
        var host = new TestDocumentHost { CompleteSave = true };
        var view = new QuestView(host, catalogPath);
        void Click(string method) => typeof(QuestView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [view, new RoutedEventArgs()]);
        T Field<T>(string name) where T : class => (T)view.FindName(name);
        OpenedFile FileData(string path) => new(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ImmutableArray.CreateRange(File.ReadAllBytes(path))) { SourceToken = Guid.NewGuid() };
        try
        {
            var files = (List<string>)typeof(QuestView).GetField("folderFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            files.AddRange([aPath, bPath]);
            var scan = MapIndexCache.Build(files.ToArray(), [], view.Registry, new DiskIndexBrowser());
            var index = (List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            index.AddRange(scan.Maps); Click("Home_Click");
            check(scan.Maps.All(m => m.ScriptCount == 1 && m.ObjectCount == 0), "comfort index facts reflect parsed MOB instead of filename guesses");
            var list = Field<ListBox>("HomeMaps");
            check(list.Items.Cast<QuestView.HomeMap>().All(m => m.Facts == "1 скрипт"), "comfort map tiles consume indexed counts");
            Field<Button>("HomeFavorites").RaiseEvent(new(Button.ClickEvent));
            check(list.Items.Count == 1 && ((QuestView.HomeMap)list.Items[0]).Path == aPath, "comfort favorite filter lists only starred map");
            Field<Button>("HomeAll").RaiseEvent(new(Button.ClickEvent));
            Field<TextBox>("HomeSearch").Text = "Восточные";
            check(list.Items.Count == 1 && ((QuestView.HomeMap)list.Items[0]).Path == bPath, "comfort map search includes personal note");
            Field<TextBox>("HomeSearch").Clear();
            list.SelectedItem = list.Items.Cast<QuestView.HomeMap>().Single(m => m.Path == aPath);
            Field<TextBox>("HomeNote").Text = "Новая заметка";
            list.SelectedItem = list.Items.Cast<QuestView.HomeMap>().Single(m => m.Path == bPath);
            check(new MapCatalogStore(catalogPath).Get(aPath).Note == "Новая заметка", "comfort switching selected map persists note to its own path");
            view.LoadOpenedFile(FileData(aPath)); Guid a = view.DocumentId!.Value;
            view.Measure(new(1250, 650)); view.Arrange(new(0, 0, 1250, 650)); view.UpdateLayout();
            var code = Field<TextBox>("CodeEditor"); code.AppendText("\r\n// edit A"); view.RefreshAnalysis(false);
            code.Select(code.Text.IndexOf("Sleep"), 5); int selection = code.SelectionStart;
            string editedA = code.Text;
            view.LoadOpenedFile(FileData(bPath)); Guid b = view.DocumentId!.Value;
            check(a != b && host.List("malachite").Length == 2 && host.Registrations[a].SourceToken != null && host.Registrations[b].SourceToken != null, "comfort opening second MOB preserves host document identities and source tokens");
            check(host.Get(a)!.Text == editedA && host.Get(a)!.IsDirty, "comfort inactive tab keeps unsaved text in host");
            code.AppendText("\r\n// edit B"); string editedB = code.Text;
            check(view.SwitchDocument(a) && code.Text == editedA && code.SelectionStart == selection && code.SelectionLength == 5, "comfort tab switching restores exact text and selection without transaction");
            long revision = host.Get(a)!.Revision;
            view.SwitchDocument(b); view.SwitchDocument(a);
            check(host.Get(a)!.Revision == revision && host.List("malachite").Length == 2, "comfort tab switching creates no duplicate documents or phantom Undo steps");
            view.RunCommand(ModuleCommand.Undo);
            check(view.DocumentId == b && code.Text == codeB && host.Get(a)!.Text == editedA, "comfort global Undo reveals changed inactive tab and preserves other draft");
            view.RunCommand(ModuleCommand.Redo);
            check(code.Text == editedB, "comfort global Redo restores edited inactive file");
            view.SwitchDocument(a); view.SelectBlock("A", false);
            var sleepButton = Field<StackPanel>("ScenarioItems").Children.OfType<Button>().Single(x => x.Tag is int offset && offset == code.Text.IndexOf("Sleep"));
            sleepButton.RaiseEvent(new(Button.ClickEvent));
            check(Field<Border>("ScenarioPanel").Visibility == Visibility.Visible && Field<Border>("CodeBorder").Visibility == Visibility.Visible && code.CaretIndex == code.Text.IndexOf("Sleep"), "comfort action selection retains scenario and navigates exact code");
            check(Texts(Field<ContentControl>("ExplanationContent")).Any(t => t.Contains("не секунды")), "comfort selected Sleep has persistent verified parameter explanation");
            Click("ReadableOnly_Click");
            check(Field<Border>("CodeBorder").Visibility == Visibility.Collapsed && Field<RowDefinition>("GraphRow").Height.IsStar, "comfort scenario-only view uses available workspace");
            Click("Scenario_Click"); Click("ChecksToggle_Click");
            check(Field<ListBox>("Diagnostics").Visibility == Visibility.Collapsed && Field<RowDefinition>("DiagnosticsRow").Height.Value == 36, "comfort checks collapse retains summary and F5");
            Click("ChecksToggle_Click"); Click("SidebarToggle_Click"); Click("InspectorToggle_Click");
            check(Field<Border>("SidebarPanel").Visibility == Visibility.Collapsed && Field<ColumnDefinition>("SidebarColumn").Width.Value == 0 && Field<ColumnDefinition>("InspectorColumn").Width.Value == 0, "comfort module panels collapse without host UI access");
            Click("SidebarToggle_Click"); Click("InspectorToggle_Click");
            Click("Code_Click"); view.Measure(new(1000, 650)); view.Arrange(new(0, 0, 1000, 650)); view.UpdateLayout();
            code.ScrollToVerticalOffset(30); code.Select(selection, 4); double vertical = code.VerticalOffset;
            view.SwitchDocument(b); view.SwitchDocument(a);
            check(code.SelectionStart == selection && code.SelectionLength == 4 && Math.Abs(code.VerticalOffset - vertical) < 1 && Field<Border>("ScenarioPanel").Visibility == Visibility.Collapsed, "comfort tabs preserve scroll and view mode");
            byte[] recovery = view.CaptureState();
            var malformed = JsonNode.Parse(recovery)!;
            malformed["Tabs"]![0]!["State"]!["DocumentId"] = a.ToString();
            check(!view.RestoreState(System.Text.Encoding.UTF8.GetBytes(malformed.ToJsonString())).Succeeded && view.DocumentId == a && code.Text == editedA && host.List("malachite").Length == 2,
                "comfort malformed recovery leaves existing documents and input intact");
            var foreign = host.Open(new("other-module", "other-format", 2, "foreign", "other input", [], null, true));
            malformed = JsonNode.Parse(recovery)!;
            malformed["Tabs"]![0]!["State"]!["DocumentId"] = foreign.DocumentId.ToString();
            check(!view.RestoreState(System.Text.Encoding.UTF8.GetBytes(malformed.ToJsonString())).Succeeded && view.DocumentId == a && code.Text == editedA && code.SelectionLength == 4 && host.Get(foreign.DocumentId)?.Text == "other input",
                "comfort foreign host tab in recovery is rejected before changing either module input");
            var recovered = new QuestView(host, Path.Combine(root, "recovered-catalog.json"));
            check(recovered.RestoreState(recovery).Succeeded && host.List("malachite").Length == 2 && recovered.DocumentId == a, "comfort recovery reattaches existing tabs without duplicate registrations");
            check(recovered.SwitchDocument(b) && ((TextBox)recovered.FindName("CodeEditor")).Text == editedB, "comfort recovery preserves second unsaved tab"); recovered.Stop();
            var freshHost = new TestDocumentHost(); var fresh = new QuestView(freshHost, Path.Combine(root, "fresh-catalog.json"));
            check(fresh.RestoreState(recovery).Succeeded && freshHost.List("malachite").Length == 2 && fresh.HasPendingChanges, "comfort portable recovery retains both unsaved fallback documents");
            check(freshHost.Registrations.Values.All(r => r.SourceToken == null), "comfort fresh recovery does not invent file write authorization"); fresh.Stop();
            view.RunCommand(ModuleCommand.Save);
            check(!view.HasPendingChanges && host.List("malachite").All(d => !d.IsDirty), "comfort host save checkpoints every tab");
            Click("Home_Click"); Field<Button>("HomeRecent").RaiseEvent(new(Button.ClickEvent));
            check(list.Items.Count == 2 && ((QuestView.HomeMap)list.Items[0]).Path == bPath, "comfort recent maps ordered by last opening");
            check(File.ReadAllBytes(aPath).SequenceEqual(sourceBytes) && File.GetLastWriteTimeUtc(aPath) == modified, "comfort favorites notes and test-host edits never mutate source MOB");
            Render(view, Path.Combine(root, "maps.png"), 1250, 650);
            Click("HomeResume_Click"); Click("Code_Click");
            view.Measure(new(1510, 850)); view.Arrange(new(0, 0, 1510, 850)); view.UpdateLayout();
            view.SelectBlock("A", false);
            Field<StackPanel>("ScenarioItems").Children.OfType<Button>().Single(x => x.Tag is int at && at == code.Text.IndexOf("Sleep")).RaiseEvent(new(Button.ClickEvent));
            view.UpdateLayout();
            int actionLine = code.GetLineIndexFromCharacterIndex(code.CaretIndex);
            check(code.GetFirstVisibleLineIndex() <= actionLine && code.GetLastVisibleLineIndex() >= actionLine, "comfort action navigation after mode resize reveals native target line");
            check(Field<TextBlock>("DocumentSummary").Text.StartsWith("1 скрипт  /  2 вызова"), "comfort document facts use readable Russian counts");
            view.AttachSyntax(); Render(view, Path.Combine(root, "workspace.png"), 1510, 850);

            // The same form used by the product, without physical mouse/keyboard or an open window.
            view.SelectBlock("A", false); Click("AddAction_Click");
            var overlay = ((Grid)view.FindName("Surface")).Children.OfType<Grid>().Single(g => g.Name == "AddActionOverlay");
            var picker = Descendants(overlay).OfType<ListBox>().Single(c => c.Name == "ActionCommand");
            var search = Descendants(overlay).OfType<TextBox>().Single(t => t.Name == "ActionSearch");
            search.Text = "консоль"; check(picker.Items.Cast<CommandInfo>().Any(c => c.Name == "ConsoleString") && picker.Items.Count < 10, "comfort action list filters by meaning without popup capturing search input");
            search.Text = "";
            picker.SelectedItem = view.Registry.Commands["ConsoleString"];
            var argument = Descendants(overlay).OfType<TextBox>().Single(t => t.Name == "ActionArgument0"); argument.Text = "Нужно идти на восток";
            var previewButton = Descendants(overlay).OfType<Button>().Single(x => x.Name == "ActionPreviewButton");
            var apply = Descendants(overlay).OfType<Button>().Single(x => x.Name == "ActionApply");
            previewButton.RaiseEvent(new(Button.ClickEvent));
            check(apply.IsEnabled && code.Text == editedA && Descendants(overlay).OfType<TextBox>().Single(t => t.Name == "ActionPreview").Text.Contains("Нужно идти на восток"), "comfort parameter form previews action without modifying code");
            using var presentation = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Malachite headless action viewport")
                { Width = 1250, Height = 750, WindowStyle = unchecked((int)0x80000000) }); // No WS_VISIBLE; never activated.
            presentation.RootVisual = view;
            Render(view, Path.Combine(root, "add-action.png"), 1250, 750);
            var selectedAction = picker.ItemContainerGenerator.ContainerFromItem(picker.SelectedItem) as FrameworkElement;
            var selectedBounds = selectedAction?.TransformToAncestor(picker).TransformBounds(new Rect(selectedAction.RenderSize));
            File.WriteAllText(Path.Combine(root, "action-viewport.json"), System.Text.Json.JsonSerializer.Serialize(new { selected = picker.SelectedItem?.ToString(), picker.SelectedIndex, picker.ActualHeight, bounds = selectedBounds?.ToString(), container = selectedAction?.GetType().Name, scrollers = Descendants(picker).OfType<ScrollViewer>().Select(s => new { s.VerticalOffset, s.ExtentHeight, s.ViewportHeight, s.CanContentScroll }) }));
            check(selectedBounds is { } bounds && bounds.Top >= 0 && bounds.Bottom <= picker.ActualHeight, "comfort action picker reveals selected command after initial native layout");
            argument.Text = "Нужен другой текст"; check(!apply.IsEnabled, "comfort parameter edit invalidates stale action preview");
            previewButton.RaiseEvent(new(Button.ClickEvent)); apply.RaiseEvent(new(Button.ClickEvent));
            check(code.Text.Contains("ConsoleString(\"Нужен другой текст\")") && !((Grid)view.FindName("Surface")).Children.Contains(overlay), "comfort apply inserts reviewed action and closes form");
            view.RunCommand(ModuleCommand.Undo); check(code.Text == editedA, "comfort contextual action is exactly one undoable operation");
            view.RefreshAnalysis(false); view.SwitchDocument(b);
            host.RejectChanges = true; code.AppendText("\n// rejected local B"); string rejected = code.Text;
            view.SwitchDocument(a); view.SwitchDocument(b);
            check(code.Text == rejected && view.HasPendingChanges, "comfort rejected local input survives switching tabs");
            view.SwitchDocument(a);
            int saves = host.SaveAllCalls; view.RunCommand(ModuleCommand.Save);
            check(host.SaveAllCalls == saves && view.DocumentId == b && code.Text == rejected, "comfort conflict in inactive tab blocks save and reveals retained local input");
            view.SwitchDocument(a);
            try { typeof(QuestView).GetMethod("EnsureIndexedTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [bPath]); check(false, "comfort stale index in changed tab blocked"); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { check(view.DocumentId == a, "comfort stale index in changed tab blocks navigation without changing active input"); }
            host.RejectChanges = false;
        }
        finally { view.Stop(); }
        var registry = new CommandRegistry(); var analysis = ScriptAnalyzer.Analyze(codeA, registry);
        var plan = ContextAction.Build(analysis, "A", false, registry.Commands["ConsoleString"], ["\"hello\""], registry);
        check(plan.Result.Replace("\r\n", "").Contains('\n') == false && plan.Result.StartsWith("DeclareScript A()\r\n"), "comfort insertion preserves CRLF and untouched declarations");
        check(ScriptAnalyzer.Analyze(plan.Result, registry).Blocks.Single(b => b.Name == "A").Calls.Count(c => c.Name == "ConsoleString") == 1, "comfort action belongs to selected THEN only");
        var complex = ScriptAnalyzer.Analyze("Script C(if()then()if()then()) WorldScript()", registry);
        try { ContextAction.Build(complex, "C", false, registry.Commands["Sleep"], ["1"], registry); check(false, "comfort ambiguous branch rejected"); }
        catch (InvalidDataException) { check(true, "comfort ambiguous multiple branches cannot silently receive an action"); }
        const string elseCode = "DeclareScript C()\nScript C(if(IsEqual(1,1))then(ConsoleString(\"then\"))else(ConsoleString(\"else\")))\nWorldScript(C())";
        var elseAnalysis = ScriptAnalyzer.Analyze(elseCode, registry);
        var elsePlan = ContextAction.Build(elseAnalysis, "C", true, registry.Commands["Sleep"], ["5"], registry);
        var elseAfter = ScriptAnalyzer.Analyze(elsePlan.Result, registry); var cBlock = elseAfter.Blocks.Single(b => b.Name == "C");
        check(elseAfter.Slice(cBlock.ElseActions!).Contains("Sleep(5)") && !elseAfter.Slice(cBlock.Actions!).Contains("Sleep(5)") && !elsePlan.Result.Contains('\r'), "comfort ELSE insertion preserves THEN and LF style");
        var worldPlan = ContextAction.Build(elseAnalysis, "WorldScript", false, registry.Commands["ConsoleString"], ["\"world\""], registry);
        var worldAfter = ScriptAnalyzer.Analyze(worldPlan.Result, registry);
        check(worldAfter.Blocks.Single(b => b.World).Calls.Any(c => c.Name == "ConsoleString") && !worldAfter.Blocks.Single(b => b.Name == "C").Calls.Any(c => c.Start >= worldAfter.Blocks.Single(b => b.World).Start), "comfort WorldScript insertion remains in map startup block");
        try { ContextAction.Build(analysis, "A", false, registry.Commands["Sleep"], ["\"bad type\""], registry); check(false, "comfort wrong argument type rejected"); }
        catch (InvalidDataException) { check(true, "comfort wrong argument type rejected before source change"); }
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        if (parent is FrameworkElement element) element.ApplyTemplate();
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    static IEnumerable<string> Texts(DependencyObject parent) => Descendants(parent).OfType<TextBlock>().Select(t => t.Text);
    static void Render(FrameworkElement view, string path, int width, int height)
    {
        view.Measure(new(width, height)); view.Arrange(new(0, 0, width, height)); view.UpdateLayout();
        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
}
