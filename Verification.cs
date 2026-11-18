using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuestStudio;

public static class Verification
{
    static readonly List<string> Checks = [];
    static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("FAILED: " + name); Checks.Add(name); }
    static void Reject(Action action, string name)
    {
        bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } catch (EncoderFallbackException) { failed = true; } catch (IOException) { failed = true; }
        Require(failed, name);
    }
    static byte[] Chunk(uint id, byte[] body)
    {
        var bytes = new byte[body.Length + 8]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, id); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length); body.CopyTo(bytes, 8); return bytes;
    }
    static byte[] Fixture(string text, bool coded)
    {
        byte[] plain = MobDocument.GameEncoding.GetBytes(text).Concat(new byte[] { 0 }).ToArray();
        if (coded)
        {
            uint seed = 123456789; var payload = new byte[plain.Length + 4]; BinaryPrimitives.WriteUInt32LittleEndian(payload, seed);
            for (int i = 0; i < plain.Length; i++) { seed = unchecked(seed * 214013 + 2531011); payload[i + 4] = (byte)(plain[i] ^ ((seed >> 16) & 255)); } plain = payload;
        }
        return Chunk(0x1234, [1, 2, 3]).Concat(Chunk(MobDocument.RootId, Chunk(0x12345678, [9, 4, 0, 7]).Concat(Chunk(coded ? MobDocument.CodedId : MobDocument.LegacyId, plain)).Concat(Chunk(0xDEAD, [1, 0, 255])).ToArray())).Concat(Chunk(0xABCD, [6, 7])).ToArray();
    }
    static string NonScriptFingerprint(byte[] bytes)
    {
        var data = new List<byte>();
        foreach (var c in MobDocument.Chunks(bytes, 0, bytes.Length))
        {
            if (c.Id == MobDocument.RootId)
                foreach (var child in MobDocument.Chunks(bytes, c.Offset + 8, c.Length - 8).Where(c => c.Id != MobDocument.CodedId && c.Id != MobDocument.LegacyId)) data.AddRange(bytes.AsSpan(child.Offset, child.Length).ToArray());
            else data.AddRange(bytes.AsSpan(c.Offset, c.Length).ToArray());
        }
        return Convert.ToHexString(SHA256.HashData(data.ToArray()));
    }
    public static int Run(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "verification";
        Directory.CreateDirectory(output);
        var stopwatch = Stopwatch.StartNew(); var registry = new CommandRegistry();
        var help = new HelpService(registry);
        string[] extensions = ["PlayAnimationLoop", "StopAnimationLoop", "ZoneIceSync", "ZoneIceFreeze"];
        Require(registry.NativeCount == 214 + extensions.Length && extensions.All(registry.Commands.ContainsKey),
            "registry: original native entries and four project extensions before addon overrides");
        AddonVerification.Run(registry, help, Require, output);
        foreach (var voice in new[] { "CreateVoiceFX", "CreateVoiceFXWait" })
        {
            foreach (var argsText in new[] { "GetObjectByID(\"334\"),\"line.wav\",100", "GetObjectByID(\"334\"),\"line.wav\",100,10,30" })
            {
                var valid = ScriptAnalyzer.Analyze($"WorldScript({voice}({argsText}))", registry);
                Require(valid.Issues.All(i => i.Severity != "error" && i.Code != "UNKNOWN"), "voice: supported form " + voice + argsText);
                Require(help.Command(voice, valid, valid.Calls.First()).Kind == "Известная команда аддона", "voice: shared registered hover " + voice);
            }
            foreach (var argsText in new[] { "GetObjectByID(\"334\"),\"line.wav\"", "GetObjectByID(\"334\"),\"line.wav\",100,10", "GetObjectByID(\"334\"),\"line.wav\",100,10,30,40" })
                Require(ScriptAnalyzer.Analyze($"WorldScript({voice}({argsText}))", registry).Issues.Any(i => i.Code == "ARITY"), "voice: reject unsupported arity " + voice + argsText);
            Require(ScriptAnalyzer.Analyze($"WorldScript({voice}(123,\"line.wav\",100))", registry).Issues.Any(i => i.Code == "TYPE"), "voice: object type checked " + voice);
            Require(ScriptAnalyzer.Analyze($"WorldScript({voice}(GetObjectByID(\"334\"),\"line.wav\",101))", registry).Issues.Any(i => i.Code == "VOICE_VOLUME"), "voice: volume checked " + voice);
            Require(ScriptAnalyzer.Analyze($"WorldScript({voice}(GetObjectByID(\"334\"),\"line.wav\",100,30,10))", registry).Issues.Any(i => i.Code == "VOICE_DISTANCE"), "voice: distance checked " + voice);
        }
        Require(ScriptAnalyzer.Analyze("WorldScript(ConsoleFloat(IsMLooted(GetObjectByID(\"3\"))))", registry).Issues.All(i => i.Severity != "error" && i.Code != "UNKNOWN"), "addon: IsMLooted returns float and accepts object");
        foreach (var command in registry.Commands.Values)
        {
            var entry = help.Entries[command.Name];
            Require(entry.Purpose.Length >= 20 && entry.Arguments.Length == command.Parameters.Length, "hover: purpose and all argument roles: " + command.Name);
            Require(help.Command(command.Name).Body.Contains("Результат:"), "hover: return value explained: " + command.Name);
        }
        Require(help.Command("SetDirectionToObject").Body.Contains("не установлена"), "hover: uncertain historical semantics not invented");
        Require(help.Command("UnknownAddon").Body.Contains("может быть командой аддона"), "hover: unknown extensions get explicit explanation boundary");
        var a = ScriptAnalyzer.Analyze(Workspace.Demo, registry);
        Require(a.Blocks.Count == 4 && a.Blocks.Count(b => b.CardEditable) == 3, "demo: world and three editable stages");
        Require(!a.Issues.Any(i => i.Severity == "error"), "demo: static rules clean");
        Require(a.Calls.Any(c => c.Owner == "FindLetter" && c.Name == "ReturnLetter"), "graph: owner and actual target");
        Require(a.Flags.Count == 6, "GS: six literal read/write references");
        foreach (var b in a.Blocks) Require(help.Script(a, b).Body.Length > 50, "hover: generated script explanation: " + b.Name);
        Require(help.Script(a, a.Blocks.Single(b => b.Name == "FindLetter")).Body.Contains("равно 1") && help.Script(a, a.Blocks.Single(b => b.Name == "FindLetter")).Body.Contains("Запустить Script ReturnLetter"), "hover: explains actual conditions and script launches");
        var branches = ScriptAnalyzer.Analyze("DeclareScript Branch(x:float) Script Branch(if(IsGreater(x,10))then(KillScript() if(IsLess(x,13))then(Sleep(2))else(Sleep(3))) if(IsEqual(x,0))then(Sleep(4)))", registry);
        var branchHelp = help.Script(branches, branches.Blocks.Single());
        Require(branchHelp.Body.Contains("больше 10") && branchHelp.Body.Contains("меньше 13") && branchHelp.Body.Contains("Иначе:") && branchHelp.Body.Contains("равно 0"), "hover: nested else and independent later if both preserved");
        Require(branchHelp.Body.Contains("действия этой ветки продолжатся") && branchHelp.Body.Contains("Подождать 4") && branchHelp.Body.Contains("если экземпляр ещё не снят KillScript"), "hover: KillScript preserves current actions but gates later top-level checks");
        var negative = ScriptAnalyzer.Analyze("WorldScript( ConsoleFloat(GetObjectByID(334)) Sleep() )", registry);
        Require(negative.Issues.Count(i => i.Code == "TYPE") == 2 && negative.Issues.Any(i => i.Code == "ARITY"), "nested return and literal types plus argument count");
        Require(ScriptAnalyzer.Analyze("WorldScript( ConsoleString(\"Script Fake() // ( )\") ) // Script nope()", registry).Blocks.Count == 1, "strings and comments do not create blocks");
        Require(ScriptAnalyzer.Analyze("WorldScript( ConsoleString(\"Acks\\Mogo\\x.wav\") )", registry).Issues.All(i => i.Severity != "error"), "backslashes preserved, not invented escapes");
        Require(ScriptAnalyzer.Analyze("WorldScript( Sleep(1)", registry).Issues.Any(i => i.Code == "PAREN"), "unclosed parentheses rejected");
        Require(ScriptAnalyzer.Analyze("DeclareScript A() Script A(if() Sleep(1))", registry).Issues.Any(i => i.Code == "THEN"), "if requires then body");
        Require(ScriptAnalyzer.Analyze("WorldScript( ConsoleString(\"missing) ", registry).Issues.Any(i => i.Code == "STRING"), "unclosed strings rejected");
        Require(ScriptAnalyzer.Analyze("GlobalVars(Heroes:group) WorldScript(For(GetObjectByID(\"3\"), Heroes)(KillScript()))", registry).Issues.Any(i => i.Code == "GLOBAL"), "iterator global argument contract");
        Require(ScriptAnalyzer.Analyze("WorldScript( FutureAddon(1) )", registry).Issues.Any(i => i.Code == "UNKNOWN" && i.Severity == "warning"), "unknown addon is uncertainty, not categorical error");
        Require(ScriptAnalyzer.Analyze("DeclareScript A() Script A(if() then(KillScript()) if() then(Sleep(1))) WorldScript(A())", registry).Blocks.Single(b => b.Name == "A").CardEditable == false, "complex source remains code-only");
        Require(ScriptAnalyzer.Analyze("WorldScript(ConsoleString(\"😀\"))", registry).Issues.Any(i => i.Code == "ENCODING"), "unrepresentable literal reported");
        var workspace = new Workspace();
        workspace.ApplyCard(a.Blocks.Single(b => b.Name == "FindLetter"), "\n  GSGetVar(0, \"other\")\n", "\n  KillScript()\n", "");
        Require(workspace.Text.Contains("\"other\"") && workspace.Text.Contains("Script ReturnLetter"), "card edit changes target and preserves next stage");
        var fresh = new Workspace(); string stage = fresh.AddStage(a); var afterAdd = ScriptAnalyzer.Analyze(fresh.Text, registry);
        Require(afterAdd.Declarations.ContainsKey(stage) && afterAdd.Blocks.Any(b => b.Name == stage), "stage creates declaration and body");
        fresh.Link(afterAdd, afterAdd.Blocks.Single(b => b.Name == "ReturnLetter"), stage);
        Require(ScriptAnalyzer.Analyze(fresh.Text, registry).Calls.Any(c => c.Owner == "ReturnLetter" && c.Name == stage), "authoring adds real call edge");
        foreach (bool coded in new[] { false, true })
        {
            byte[] input = Fixture("// Русский текст\r\n" + Workspace.Demo.Replace("\n", "\r\n"), coded);
            var mob = new MobDocument(input);
            Require(mob.Encode(mob.Text).SequenceEqual(input), $"container {coded}: exact no-op including seed/null/unknown chunks");
            string updated = mob.Text + "\r\n// Правка с буквой ё\r\n";
            byte[] encoded = mob.Encode(updated);
            Require(new MobDocument(encoded).Text == updated && NonScriptFingerprint(input) == NonScriptFingerprint(encoded), $"container {coded}: roundtrip and unchanged non-script bytes");
            Require(new MobDocument(encoded).Seed == mob.Seed, $"container {coded}: seed preserved");
            Reject(() => mob.Encode(mob.Text + "😀"), $"container {coded}: non-CP1251 export refused");
            Reject(() => new MobDocument(input[..^1]), $"container {coded}: truncation rejected");
            var project = new Workspace { Mob = mob, Text = updated, Name = "Тест", TextEncoding = MobDocument.GameEncoding };
            string path = System.IO.Path.Combine(output, coded ? "coded.malachite" : "legacy.malachite");
            File.WriteAllBytes(path, project.SaveProject()); var loaded = Workspace.Open(path);
            Require(loaded.Text == updated && loaded.Mob!.Original.SequenceEqual(input), $"project {coded}: recover draft and exact MOB base");
        }
        Reject(() => new MobDocument(Chunk(MobDocument.RootId, Chunk(MobDocument.LegacyId, [65]).Concat(Chunk(MobDocument.LegacyId, [66])).ToArray())), "duplicate script refused");
        string export = System.IO.Path.Combine(output, "immutable.txt"); File.WriteAllText(export, "original");
        Reject(() => SafeFiles.WriteNew(export, [1, 2]), "export cannot overwrite existing file");
        Require(File.ReadAllText(export) == "original", "existing export bytes preserved");
        var mapResults = new List<object>();
        if (args.Length > 1)
        {
            foreach (string path in Directory.EnumerateFiles(args[1], "*.mob").OrderBy(p => p))
            {
                byte[] bytes = File.ReadAllBytes(path); string hash = Convert.ToHexString(SHA256.HashData(bytes));
                try
                {
                    var mob = new MobDocument(bytes);
                    Require(mob.Encode(mob.Text).SequenceEqual(bytes), "real no-op: " + System.IO.Path.GetFileName(path));
                    byte[] edited = mob.Encode(mob.Text + "\r\n// Malachite in-memory verification\r\n");
                    Require(NonScriptFingerprint(bytes) == NonScriptFingerprint(edited), "real non-script preservation: " + System.IO.Path.GetFileName(path));
                    Analysis? parsed = null;
                    if (true)
                    {
                        parsed = ScriptAnalyzer.Analyze(mob.Text, registry);
                        Require(parsed.Issues.All(i => i.Code != "UNKNOWN"), "full installed map command coverage: " + System.IO.Path.GetFileName(path));
                        foreach (var call in parsed.Calls.Where(c => c.Name is "CreateVoiceFX" or "CreateVoiceFXWait" or "IsMLooted"))
                            Require(!parsed.Issues.Any(i => i.Code == "UNKNOWN" && i.Offset == call.Start), "real addon recognized: " + System.IO.Path.GetFileName(path) + ":" + call.Line);
                        foreach (var block in parsed.Blocks) Require(help.Script(parsed, block).Body.Length > 25, "real script help: " + System.IO.Path.GetFileName(path) + "/" + block.Name);
                        AddonVerification.WriteParityFixture(output, System.IO.Path.GetFileNameWithoutExtension(path), parsed);
                    }
                    Require(hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "real source unchanged: " + System.IO.Path.GetFileName(path));
                    mapResults.Add(new { file = System.IO.Path.GetFileName(path), sha256 = hash, status = "roundtrip", blocks = parsed?.Blocks.Count, calls = parsed?.Calls.Count, errors = parsed?.Issues.Count(i => i.Severity == "error"), unknown = parsed?.Issues.Where(i => i.Code == "UNKNOWN").ToList(), issues = parsed?.Issues.Take(20).ToList() });
                }
                catch (InvalidDataException ex) { mapResults.Add(new { file = System.IO.Path.GetFileName(path), sha256 = hash, status = "explicit-rejection", reason = ex.Message }); }
            }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        var elseWorkspace = new Workspace { Text = "DeclareScript A() Script A(if(IsEqual(1,1))then(Sleep(1))) WorldScript(A())" };
        window.LoadWorkspace(elseWorkspace, false); window.SelectBlock("A", false);
        window.UseElse.IsChecked = true; window.ElseBox.Text = "Sleep(2)"; window.ApplyCard();
        Require(window.Current.Blocks.Single(b => b.Name == "A").ElseActions != null && window.Current.Issues.All(i => i.Severity != "error"), "WPF: add else using card");
        Render(window, 1510, 922, System.IO.Path.Combine(output, "malachite-else.png"));
        window.UseElse.IsChecked = false; window.ApplyCard();
        Require(window.Current.Blocks.Single(b => b.Name == "A").ElseActions == null, "WPF: remove else using card");
        window.LoadWorkspace(new Workspace(), false);
        window.WindowSurface.Measure(new Size(1510, 922)); window.WindowSurface.Arrange(new Rect(0, 0, 1510, 922)); window.WindowSurface.UpdateLayout();
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        Require(window.WindowStyle == WindowStyle.None && chrome.CaptionHeight == 32 && chrome.GlassFrameThickness == new Thickness(0), "chrome: native white title replaced with client-drawn graphite caption");
        Require(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(window.MinimizeButton) && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(window.MaximizeButton) && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(window.CloseButton), "chrome: window buttons receive input inside draggable caption");
        window.SelectBlock("FindLetter", true);
        window.ConditionBox.Text = "\n    IsEqual(GSGetVar(0, \"q.letter.found\"), 2)\n  ";
        Require(window.ApplyCard(), "WPF: actual card Apply handler");
        Require(window.CodeEditor.Text.Contains("\"q.letter.found\"), 2)"), "WPF: code reflects card");
        Require(window.CodeEditor.CanUndo, "WPF: edit exposes undo");
        window.CodeEditor.Undo(); window.RefreshAnalysis(false);
        Require(window.CodeEditor.Text == Workspace.Demo, "WPF: one undo restores card transaction");
        window.LoadWorkspace(new(), false); window.SelectBlock("FindLetter", true);
        Render(window, 1510, 890, System.IO.Path.Combine(output, "malachite-preview.png"));
        int hoverOffset = window.CodeEditor.Text.IndexOf("GSGetVar(0, \"q.letter.found\"", StringComparison.Ordinal) + 3;
        window.CodeEditor.ScrollToLine(window.CodeEditor.GetLineIndexFromCharacterIndex(hoverOffset)); window.WindowSurface.UpdateLayout();
        var charBox = window.CodeEditor.GetRectFromCharacterIndex(hoverOffset, true);
        int hit = window.CodeEditor.GetCharacterIndexFromPoint(new Point(charBox.X + 2, charBox.Y + charBox.Height / 2), false);
        var hitHelp = window.HelpAt(window.CodeEditor, hit);
        Require(hitHelp?.Title == "GSGetVar" && hitHelp.Body.Contains("q.letter.found"), "hover: native text hit-test after vertical scrolling and actual arguments");
        Require(window.HelpAt(window.CodeEditor, window.CodeEditor.Text.IndexOf("// Учебная", StringComparison.Ordinal) + 4) == null, "hover: comment text does not become a function");
        Require(window.HelpAt(window.ConditionBox, window.ConditionBox.Text.IndexOf("IsEqual", StringComparison.Ordinal) + 2)?.Title == "IsEqual", "hover: condition-card field lookup");
        Require(window.HelpAt(window.ActionBox, window.ActionBox.Text.IndexOf("ReturnLetter", StringComparison.Ordinal) + 2)?.Kind.Contains("Script") == true, "hover: card script reference resolves to actual body");
        RenderHelp(window, help.Command("IsGreater"), System.IO.Path.Combine(output, "hover-function.png"));
        RenderHelp(window, branchHelp, System.IO.Path.Combine(output, "hover-branches.png"));
        Render(window, 1120, 740, System.IO.Path.Combine(output, "malachite-compact.png"));
        Require(window.GraphCanvas.Children.OfType<System.Windows.Controls.Button>().Count() == 3, "WPF: selected stage and true neighbors rendered");
        Button(window, "＋  Добавить этап").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Require(window.Current.Blocks.Any(b => b.Name == "Stage1"), "WPF: add-stage button writes declaration and definition");
        window.SelectBlock("ReturnLetter", false); window.LinkTargets.SelectedItem = "Stage1";
        Button(window, "＋ Добавить вызов").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Require(window.Current.Calls.Any(c => c.Name == "Stage1" && c.Owner == "ReturnLetter"), "WPF: link button writes call and refreshes graph");
        window.CodeEditor.Undo(); window.RefreshAnalysis(false);
        Require(!window.Current.Calls.Any(c => c.Name == "Stage1"), "WPF: linked call is one undo transaction");
        Button(window, "Функции").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.SearchBox.Text = "Sleep"; window.Navigation.SelectedIndex = 0;
        Require(window.SignatureText.Text.StartsWith("Sleep(") && window.HelpText.Text.Contains("сегмент"), "WPF: registry search and documented explanation");
        Render(window, 1510, 890, System.IO.Path.Combine(output, "malachite-reference.png"));
        window.SearchBox.Text = "CreateVoiceFXWait"; window.Navigation.SelectedIndex = 0;
        Require(window.HelpText.Text.Contains("ждёт") && window.SignatureText.Text.Contains("arg5"), "WPF: addon function catalogue has voice explanation and both arities");
        Render(window, 1510, 890, System.IO.Path.Combine(output, "malachite-addon.png"));
        window.SearchBox.Text = "";
        Button(window, "GS").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.Navigation.SelectedIndex = 0;
        Require(window.UsageList.Items.Count > 0, "WPF: GS read/write navigation");
        if (args.Length > 1)
        {
            var folderIndex = MainWindow.IndexFolder(Directory.GetFiles(args[1], "*.mob"), registry);
            Require(folderIndex.Maps.Count == mapResults.Count - folderIndex.Errors.Count, "folder: every readable map indexed with explicit exclusions");
            Require(folderIndex.Maps.SelectMany(m => m.Flags.Select(f => (m.Path, f.Key, f.Context))).GroupBy(x => (x.Key, x.Context)).Any(g => g.Select(x => x.Path).Distinct().Count() > 1), "folder: real cross-map GS references found");
            var real = Workspace.Open(System.IO.Path.Combine(args[1], "zone1.mob"));
            window.LoadWorkspace(real, false);
            Require(real.Mob!.Original.SequenceEqual(real.Mob.Encode(window.CodeEditor.Text)), "WPF: real MOB opening is byte-identical through editor text");
            Require(window.Selected != null && window.InspectorTitle.Text == window.Selected.Display && window.CardPanel.Visibility == Visibility.Visible, "WPF: switching document clears stale GS inspector");
            Render(window, 1510, 890, System.IO.Path.Combine(output, "malachite-real-mob.png"));
            var basecamp = Workspace.Open(System.IO.Path.Combine(args[1], "basecam.mob"));
            window.LoadWorkspace(basecamp, false); window.SelectBlock("VCheck#0#334", true);
            Require(window.Selected?.Name == "VCheck#0#334", "hover: exact script from owner screenshot found in installed basecam");
            var actual = window.Help.Script(window.Current, window.Selected!);
            Require(actual.Body.Contains("больше 10") && actual.Body.Contains("меньше 13") && actual.Body.Contains("VTriger#0#335") && actual.Body.Contains("VTriger#0#336"), "hover: owner script explains time conditions and both target branches");
            File.WriteAllText(System.IO.Path.Combine(output, "basecam-VCheck334-explanation.txt"), actual.PlainText);
            RenderHelp(window, actual, System.IO.Path.Combine(output, "hover-basecam-script.png"));
            Render(window, 1510, 890, System.IO.Path.Combine(output, "malachite-basecam.png"));
        }
        File.WriteAllText(System.IO.Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { status = "PASS", checks = Checks.Count, elapsedSeconds = stopwatch.Elapsed.TotalSeconds, checksPassed = Checks, maps = mapResults, limitations = new[] { "No original engine compilation", "No gameplay or pointer interaction", "No QDB/RES cross-resource linkage", "Static checks are partial and registry-specific" } }, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown(); return 0;
    }
    static System.Windows.Controls.Button Button(DependencyObject root, string label)
    {
        if (root is MainWindow window) root = window.WindowSurface;
        if (root is System.Windows.Controls.Button b && b.Content is string content && content == label) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Button(VisualTreeHelper.GetChild(root, i), label); } catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(label);
    }
    static void Render(MainWindow window, int width, int height, string path)
    {
        var visual = window.WindowSurface;
        visual.Measure(new Size(width, height)); visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout();
        window.AttachSyntax(); visual.UpdateLayout();
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(path); png.Save(stream);
    }
    static void RenderHelp(MainWindow window, HoverHelp help, string path)
    {
        var border = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(29, 48, 39)), BorderBrush = new SolidColorBrush(Color.FromRgb(90, 151, 120)), BorderThickness = new Thickness(1), Padding = new Thickness(18), Child = window.HelpView(help), Width = 585 };
        border.Measure(new Size(585, 680)); var size = new Size(585, Math.Min(680, border.DesiredSize.Height)); border.Arrange(new Rect(size)); border.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
