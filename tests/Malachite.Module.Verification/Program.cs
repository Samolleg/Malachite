using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Reflection;
using DatabaseEditor.Modules.Wpf.V1;
using DatabaseEditor.Modules.V1;
using Malachite.Module;
using QuestStudio;

internal static class Program
{
 [STAThread] static int Main(string[] args)
 {
  var checks = new List<object>(); int failures = 0;
  void Check(bool ok, string name) { checks.Add(new { name, passed = ok }); if (!ok) failures++; }
  var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
  if (args.Length > 1 && args[1] == "editor-perf") { EditorPerformance.Run(args[0]); return 0; }
  Directory.CreateDirectory(args[0]);
  GeneratorRemovalVerification.Run(Check);
  WorkbenchVerification.Run(Check,args[0]);
  if(args.Length>1 && args[1]=="workbench-only") { File.WriteAllText(Path.Combine(args[0],"workbench-report.json"),JsonSerializer.Serialize(new{failures,checks},new JsonSerializerOptions{WriteIndented=true})); return failures==0?0:1; }
  TemplateVerification.Run(Check,args[0]);
  DisguiseVerification.Run(Check);
  ParameterHelpVerification.Run(Check);
  TextEditingVerification.Run(Check, args[0]);
  AtlasScrollVerification.Run(Check, args[0]);
  MapCacheVerification.Run(Check);
  HomeVerification.Run(Check, args[0]);
  AutoMapsVerification.Run(Check);
  Directory.CreateDirectory(args[0]);
  var host = new TestHost(); var view = TestViews.Demo(host);
  T Control<T>(QuestView v, string name) where T : FrameworkElement => (T)v.FindName(name);
  var code = Control<TextBox>(view, "CodeEditor");
  const string atlasA = "Script Start ( if () then ( GSSetVarMax(0, \"q.gz1g.q0g\", 1) Bridge() ) ) Script Bridge ( if () then ( Next() ) ) Script Next ( if ( GSGetVar(0, \"q.gz1g.q0g\") ) then ( GSSetVarMax(0, \"q.gz1g.q0g.2\", 1) LeaveToZone(0, \"gz2g\", 0) ) else ( GSSetVar(0, \"q.gz1g.q0g\", 3) ) )";
  const string atlasB = "Script Finish ( if ( IsEqual(GSGetVar(0, \"q.gz1g.q0g.2\"), 2) ) then ( QuestComplete(0, \"q0g\") GSSetVar(0, \"b.gz1g.endBrief\", 1) ) )";
  var atlasMapA = QuestAtlas.Read(ScriptAnalyzer.Analyze(atlasA, view.Registry), "zone1.mob", "hash1");
  var atlasMapB = QuestAtlas.Read(ScriptAnalyzer.Analyze(atlasB, view.Registry), "zone2.mob", "hash2");
  var briefRef = new RecordRef(Guid.NewGuid(), 1, "quests.briefings", Guid.NewGuid());
  var atlasSnapshot = new WorkspaceSnapshot(new(briefRef.SessionId, 1, 0), "atlas", false,
      [new(briefRef, "endBrief", "Завершение", [new(7, 0, [], new("StringList", "q.gz1g.q0g", "Завершить", false))], [new("BRIEFING endBrief", "Итоги задания", "Финальный разговор", false)])]);
  var route = QuestAtlas.Build("q0g", [atlasMapA, atlasMapB], atlasSnapshot);
  QuestRoute PredicateRoute(string write, string condition) => QuestAtlas.Build("q1g",
      [QuestAtlas.Read(ScriptAnalyzer.Analyze($"Script Write ( if () then ( {write} ) ) Script Read ( if ( {condition} ) then () )", view.Registry), "predicate.mob", "test")], atlasSnapshot with { Records = [] });
  const string readState = "GSGetVar(0, \"q.gz3g.q1g\")";
  foreach (var sample in new (string Write, string Test, bool Expected)[] {
      ("GSSetVarMax(0, \"q.gz3g.q1g\", 3)", $"IsEqual({readState}, 1)", false),
      ("GSSetVar(0, \"q.gz3g.q1g\", 3)", $"IsEqual({readState}, 3)", true),
      ("GSSetVar(0, \"q.gz3g.q1g\", 1)", $"IsEqual({readState}, 3)", false),
      ("GSSetVarMax(0, \"q.gz3g.q1g\", 1)", $"IsEqual({readState}, 3)", true),
      ("GSSetVarMax(0, \"q.gz3g.q1g\", 3)", $"IsGreater(2, {readState})", false),
      ("GSSetVar(0, \"q.gz3g.q1g\", 3)", $"Not(IsEqual({readState}, 1))", true),
      ("GSSetVarMax(0, \"q.gz3g.q1g\", 3)", $"Not(IsGreater({readState}, 2))", false),
      ("GSSetVar(0, \"q.gz3g.q1g\", GetWorldTime())", $"IsEqual({readState}, 1)", false),
      ("GSSetVar(0, \"q.gz3g.q1g\", 1)", $"UnknownWrapper(IsEqual({readState}, 1))", false) })
  {
      var sampleRoute = PredicateRoute(sample.Write, sample.Test);
      Check(sampleRoute.Points.Length == 2 && sampleRoute.Connections.Any() == sample.Expected,
          "quest predicate compatibility preserves usages: " + sample.Write + " -> " + sample.Test);
  }
  foreach (var sample in new (int Value, string Condition, bool Edge)[] {
      (1, readState, true), (0, readState, false), (0, $"Not({readState})", true),
      (1, $"Not({readState})", false), (1, $"Not(Not({readState}))", true),
      (1, $"UnknownWrapper({readState})", false) })
      Check(PredicateRoute($"GSSetVar(0, \"q.gz3g.q1g\", {sample.Value})", sample.Condition).Connections.Any() == sample.Edge,
          "bare GS condition semantics: " + sample.Value + " -> " + sample.Condition);
  var deletedFlag = ScriptAnalyzer.Analyze("WorldScript(GSDelVar(0, \"q.gz1g.audit\"))", view.Registry).Flags;
  Check(deletedFlag.Count == 1 && deletedFlag[0].Write && deletedFlag[0].Delete && deletedFlag[0].Value == "",
      "GSDelVar is indexed as deletion without invented written value");
  var dispatchMap = QuestAtlas.Read(ScriptAnalyzer.Analyze(atlasA + " Script Dispatcher ( if () then ( Start() Unrelated() ) ) Script Unrelated ( if () then ( LeaveToZone(0, \"other\", 0) ) )", view.Registry), "dispatch.mob", "test");
  var dispatchRoute = QuestAtlas.Build("q0g", [dispatchMap], atlasSnapshot with { Records = [] });
  Check(!dispatchRoute.Points.Any(p => p.Script is "Dispatcher" or "Unrelated") && dispatchRoute.Points.Any(p => p.Script == "Bridge"), "shared startup dispatcher excluded while bridge between quest usages retained");
  Check(dispatchRoute.Connections.Where(e => e.Certain).All(e => dispatchRoute.Points.First(p => p.Id == e.From).CallTarget != null), "direct script arrows originate only at actual call sites");
  if (args.Length > 5)
  {
    using var liveBriefings = JsonDocument.Parse(File.ReadAllText(args[5]));
    int linked = 0;
    foreach (var row in liveBriefings.RootElement.EnumerateArray())
    {
      var fields = row.GetProperty("links").EnumerateObject().Select(f => new FieldDto(int.Parse(f.Name), 0, [], new ValueDto("StringList", string.Join("; ", f.Value.EnumerateArray().Select(v => v.GetString()!)), "live", false))).ToImmutableArray();
      var record = new RecordDto(briefRef with { RecordId = Guid.NewGuid() }, row.GetProperty("name").GetString()!, "", fields, []);
      foreach (var key in fields.SelectMany(f => f.Value.ExactValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
      {
        if (QuestAtlas.QuestKey(key) is not { } quest) continue;
        var liveRoute = QuestAtlas.Build(quest, [], atlasSnapshot with { Records = [record] });
        Check(liveRoute.Points.Any(p => p.Record == record.Reference && p.Key == key), "live briefing quest effect: " + record.Name + " / " + key); linked++;
      }
    }
    Check(linked > 0, "installed QDB briefing effects covered");
  }
  Check(route.Points.Any(p => p.Script == "Bridge") && route.Connections.Any(e => e.Certain), "quest route includes intermediate Script calls");
  Check(route.Points.Any(p => p.Conditions.Contains("ИНАЧЕ")), "quest route preserves else context");
  Check(route.Connections.Any(e => !e.Certain && route.Points.First(p => p.Id == e.From).Map == "zone1.mob" && route.Points.First(p => p.Id == e.To).Map == "zone2.mob"), "quest route cross-map GS dependency is explicitly uncertain");
  Check(route.Points.Any(p => p.Record == briefRef && p.Action.Contains("Завершение")), "quest route includes QDB completion briefing");
  Check(route.Points.Any(p => p.Key == "b.gz1g.endBrief") && route.Connections.Any(e => e.Reason.Contains("Состояние брифинга")), "briefing state linked to DB record");
  Check(route.Points.Any(p => p.Action.StartsWith("LeaveToZone") && p.Conditions.Contains("не приравнивается")), "zone transition retained without guessing MOB name");
  var otherContext = QuestAtlas.Read(ScriptAnalyzer.Analyze("WorldScript( GSGetVar(1, \"q.gz1g.q0g.2\") )", view.Registry), "other.mob", "hash3");
  var separate = QuestAtlas.Build("q0g", [atlasMapA, otherContext], atlasSnapshot with { Records = [] });
  Check(!separate.Connections.Any(e => separate.Points.First(p => p.Id == e.To).Map == "other.mob"), "different player GS contexts are not connected");
  host.Reader.Current = atlasSnapshot;
  view.LoadWorkspace(new Workspace { SourcePath = "zone1.mob", Text = atlasA }, false);
  Control<Border>(view, "QuestAtlasPanel").Visibility = Visibility.Visible; view.RefreshQuestAtlas();
  Check(Control<ListBox>(view, "AtlasQuests").Items.Count == 1 && Control<StackPanel>(view, "AtlasNodes").Children.Count > 0, "quest atlas renders current-map inventory");
  view.Measure(new Size(1510, 922)); view.Arrange(new Rect(0, 0, 1510, 922)); view.UpdateLayout();
  var atlasBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); atlasBitmap.Render(view);
  var atlasPng = new PngBitmapEncoder(); atlasPng.Frames.Add(BitmapFrame.Create(atlasBitmap));
  using (var atlasStream = File.Create(Path.Combine(args[0], "quest-atlas.png"))) atlasPng.Save(atlasStream);
  view.NavigateQuestPoint(atlasMapA.Points[0]);
  Check(code.CaretIndex == atlasMapA.Points[0].Offset && Control<Border>(view, "QuestAtlasPanel").Visibility == Visibility.Collapsed, "quest point opens exact current source position");
  var indirectBrief = new RecordDto(briefRef, "n8_9", "Рыцарь", [new(6, 0, [], new("StringList", "q.gz12k.qk10k;q.gz12k.qk10k.1", "", false))], [new("BRIEFING n8_9", "Познакомиться с Рыцарем", "", false)]);
  host.Reader.Current = atlasSnapshot with { Records = [indirectBrief] };
  view.LoadWorkspace(new Workspace { SourcePath = "zone1.mob", Text = atlasA + " WorldScript ( GSSetVarMax(0, \"b.merc9.n8_9\", 1) )" }, false);
  Control<Border>(view, "QuestAtlasPanel").Visibility = Visibility.Visible; view.RefreshQuestAtlas();
  var questList = Control<ListBox>(view, "AtlasQuests");
  Check(questList.Items.Count == 1 && Control<CheckBox>(view, "AtlasIndirect").IsChecked == false, "indirect quests hidden by default");
  Control<CheckBox>(view, "AtlasIndirect").IsChecked = true;
  Check(questList.Items.Groups?.Count == 2 && questList.SelectedItem?.ToString()?.Contains("q0g") == true, "direct and briefing-only quests separated, direct selected first");
  questList.SelectedItem = questList.Items.Cast<object>().First(q => q.ToString()!.Contains("qk10k"));
  Check(!Control<TextBlock>(view, "AtlasStatus").Text.Contains('\n') && Control<TextBlock>(view, "AtlasStatus").Text.Length < 100, "atlas heading status remains compact");
  var reason = (Control<StackPanel>(view, "AtlasNodes").Children.OfType<Border>().First().Child as TextBlock)?.Text ?? "";
  Check(reason.Contains("b.merc9.n8_9") && reason.Contains("Познакомиться с Рыцарем") && reason.Contains("q.gz12k.qk10k.1") && reason.Contains("После прохождения"), "indirect explanation preserves full state key, briefing title, quest and subobjective effects");
  view.Measure(new Size(1510, 922)); view.Arrange(new Rect(0, 0, 1510, 922)); view.UpdateLayout();
  var indirectBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); indirectBitmap.Render(view);
  var indirectPng = new PngBitmapEncoder(); indirectPng.Frames.Add(BitmapFrame.Create(indirectBitmap));
  using (var stream = File.Create(Path.Combine(args[0], "indirect-briefing.png"))) indirectPng.Save(stream);
  Control<CheckBox>(view, "AtlasIndirect").IsChecked = false;
  Check(questList.Items.Count == 1 && questList.SelectedItem?.ToString()?.Contains("q0g") == true, "hiding selected indirect quest selects remaining direct quest");
  var indexList = (List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
  var secondMap = QuestAtlas.Read(ScriptAnalyzer.Analyze("WorldScript ( GSSetVar(0, \"q.gz2g.remoteQuest\", 1) )", view.Registry), "remote.mob", "remote");
  indexList.Add(new("remote.mob", "remote", []) { Quests = secondMap });
  view.RefreshQuestAtlas();
  Control<CheckBox>(view, "AtlasIndirect").IsChecked = true;
  var atlasRestored = TestViews.Demo(new TestHost());
  var legacyAtlasState = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(view.CaptureState()).Replace("\"AllMaps\":false", "\"AllMaps\":true"));
  var atlasRestoredResult = atlasRestored.RestoreState(legacyAtlasState);
  var restoredIndex = (List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(atlasRestored)!;
  Check(atlasRestoredResult.Succeeded && restoredIndex.Count == indexList.Count && restoredIndex.Last().Quests!.Points.Length == secondMap.Points.Length &&
      atlasRestored.FindName("AtlasAllMaps") == null && Control<CheckBox>(atlasRestored, "AtlasIndirect").IsChecked == true,
      "full recovery retains map index and quest filters without file reads");
  Check(!Control<ListBox>(atlasRestored, "AtlasQuests").Items.Cast<object>().Any(q => q.ToString()!.Contains("remoteQuest")), "legacy AllMaps recovery cannot enable remote inventory");
  atlasRestored.Stop();
  Control<CheckBox>(view, "AtlasIndirect").IsChecked = false;
  view.RefreshQuestAtlas();
  Check(questList.Items.Count == 1 && !questList.Items.Cast<object>().Any(q => q.ToString()!.Contains("remoteQuest")), "current-map inventory excludes remote-only quests");
  Check(questList.Items.Count == 1, "current-map scope removes remote-only quest");
  indexList.Clear();
  typeof(QuestView).GetField("folderIndexing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, true);
  typeof(QuestView).GetField("folderProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, "Индексация MOB: 1 / 153");
  view.RefreshQuestAtlas();
  Check(Control<TextBlock>(view, "AtlasStatus").Text == "Индексация MOB: 1 / 153", "pending index shows progress instead of missing-folder instruction");
  typeof(QuestView).GetField("folderIndexing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, false);
  view.RefreshQuestAtlas();
  Check(Control<TextBlock>(view, "AtlasStatus").Text.Contains("Текущая карта"), "atlas remains in current-map scope without index");
  host.Reader.Current = new(new(Guid.Empty, 1, 0), "test", false, []);
  view.LoadWorkspace(new Workspace(), false);
  var lexical = EditorLexical.Scan("/* block\ncomment ( */ GSSetVar(0, \"a,b)\", 1) // tail");
  Check(lexical.Count(s => s.Kind == "comment") == 2 && lexical.Any(s => s.Kind == "string" && s.Value.Contains("a,b)")), "live lexical colors distinguish block comments and quoted punctuation");
  var nestedInput = "GSSetVar(0, \"key\", IsGreater(2, ";
  Check(EditorLexical.ArgumentAt(EditorLexical.Scan(nestedInput), nestedInput.Length) == new ArgumentHint("IsGreater", 1), "signature detects incomplete nested call argument");
  var outerInput = "GSSetVar(0, \"key\", IsGreater(2, 1), ";
  Check(EditorLexical.ArgumentAt(EditorLexical.Scan(outerInput), outerInput.Length) == new ArgumentHint("GSSetVar", 3), "signature restores outer argument after nested call");
  var bracketInput = "GSGetVar(0, \"(key)\")";
  Check(EditorLexical.BracketPair(EditorLexical.Scan(bracketInput), bracketInput.Length) == (bracketInput.Length - 1, 8), "bracket matching ignores parentheses inside strings");
  code.Text = "GSSetV"; code.CaretIndex = code.Text.Length;
  Check(view.CompletionCandidates(true).Any(c => c.Name == "GSSetVarMax"), "completion offers native GS commands");
  view.InsertCompletion("GSSetVarMax");
  Check(code.Text == "GSSetVarMax()" && code.CaretIndex == "GSSetVarMax(".Length, "completion inserts balanced call with caret inside");
  Check(Control<TextBlock>(view, "ArgumentHelp").Text.Contains("Аргумент 1"), "signature help updates immediately after completion");
  code.Text = "GSSetV(0, \"x\", 1)"; code.CaretIndex = 6; view.CompletionCandidates(true); view.InsertCompletion("GSSetVar");
  Check(code.Text == "GSSetVar(0, \"x\", 1)", "completion preserves existing call and arguments");
  code.Text = "// GSSet"; code.CaretIndex = code.Text.Length;
  Check(view.CompletionCandidates(true).Count == 0, "completion suppressed inside comment");
  code.Text = "ConsoleString(\"GSSet"; code.CaretIndex = code.Text.Length;
  Check(view.CompletionCandidates(true).Count == 0, "completion suppressed inside unfinished string");
  var completionHost = new TestDocumentHost(); var completionView = TestViews.Demo(completionHost);
  var completionCode = Control<TextBox>(completionView, "CodeEditor");
  completionCode.Text = "GSSetV"; completionCode.CaretIndex = completionCode.Text.Length;
  completionView.CompletionCandidates(true); completionView.InsertCompletion("GSSetVar");
  completionView.RunCommand(ModuleCommand.Undo);
  Check(completionCode.Text == "GSSetV", "completion is one host undo operation");
  completionView.RunCommand(ModuleCommand.Redo);
  Check(completionCode.Text == "GSSetVar()", "host redo restores completed call");
  completionView.Stop();
  view.LoadWorkspace(new Workspace(), false);
  code.Text = "// Комментарий\r\nScript Test\r\n(\r\n if ( IsGreater(GSGetVar(0, \"q.gz1g.q0g.2\"), 1) )\r\n then (\r\n  /* Комментарий в несколько строк\r\n     и скобки ( ) */\r\n  GSSetVarMax(0, \"q.gz1g.q0g.2\", 1)\r\n )\r\n)";
  view.RefreshAnalysis(false); view.Measure(new Size(1510, 922)); view.Arrange(new Rect(0, 0, 1510, 922)); view.UpdateLayout();
  Control<Border>(view, "QuestAtlasPanel").Visibility = Visibility.Collapsed;
  view.AttachSyntax(); code.CaretIndex = code.Text.IndexOf("GSSetVarMax(", StringComparison.Ordinal) + "GSSetVarMax(".Length;
  ((RowDefinition)view.FindName("GraphRow")).Height = new GridLength(0); view.UpdateLayout();
  code.ScrollToHome(); view.UpdateLayout();
  var syntaxBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); syntaxBitmap.Render(view);
  var syntaxPng = new PngBitmapEncoder(); syntaxPng.Frames.Add(BitmapFrame.Create(syntaxBitmap));
  using (var syntaxStream = File.Create(Path.Combine(args[0], "syntax-editor.png"))) syntaxPng.Save(syntaxStream);
  code.AppendText("\r\n// immediate edit");
  Check(System.Windows.Documents.AdornerLayer.GetAdornerLayer(code)?.GetAdorners(code)?.OfType<SyntaxLayer>().Single().Visibility == Visibility.Hidden && code.Foreground != Brushes.Transparent, "native text remains current before syntax feedback");
  view.LoadWorkspace(new Workspace(), false);
  const string scenarioSource = "Script Story ( if ( IsGreater(GSGetVar(0, \"visits\"), 1) ) then ( KillScript() if ( Not(IsEqual(1, 0)) ) then ( Sleep(5) ) else ( Mystery(9) ) ) else ( QuestComplete(0, \"quest\") ) )";
  var scenarioAnalysis = ScriptAnalyzer.Analyze(scenarioSource, view.Registry);
  var scenario = ReadableScenario.Describe(scenarioAnalysis, scenarioAnalysis.Blocks.Single(), view.Help);
  Check(scenario.Count(x => x.Text.StartsWith("ИНАЧЕ")) == 2, "readable scenario preserves nested else branches");
  Check(scenario.Any(x => x.Text.Contains("visits") && x.Text.Contains("больше 1")), "readable scenario expands nested GS expression");
  Check(scenario.Any(x => x.Text.Contains("5 игровых сегментов")), "readable sleep keeps segment units");
  Check(scenario.Any(x => x.Text.Contains("Оставшиеся действия этой ветки продолжатся")), "readable KillScript is not return");
  Check(scenario.Any(x => x.Caution && x.Text.Contains("Mystery")), "unknown scenario command remains visible");
  Check(scenarioAnalysis.Text == scenarioSource && scenario.All(x => x.Offset >= 0 && x.Offset < scenarioSource.Length), "readable view preserves source and valid navigation offsets");
  Check(Control<Border>(view, "ScenarioPanel").Visibility == Visibility.Visible && Control<StackPanel>(view, "ScenarioItems").Children.Count > 0, "readable scenario is default embedded view");
  view.LoadWorkspace(new Workspace { Text = scenarioSource }, false);
  int sleepRow = scenario.ToList().FindIndex(row => row.Offset == scenarioSource.IndexOf("Sleep(5)", StringComparison.Ordinal));
  Control<StackPanel>(view, "ScenarioItems").Children.OfType<Button>().ElementAt(sleepRow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  Check(code.CaretIndex == scenarioSource.IndexOf("Sleep(5)", StringComparison.Ordinal) && code.SelectionLength == 0 && code.Text == scenarioSource, "scenario click targets exact source without selecting or changing code");
  view.LoadWorkspace(new Workspace { Text = scenarioSource }, false);
  view.Measure(new Size(1510, 922)); view.Arrange(new Rect(0, 0, 1510, 922)); view.UpdateLayout();
  var scenarioBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); scenarioBitmap.Render(view);
  var scenarioPng = new PngBitmapEncoder(); scenarioPng.Frames.Add(BitmapFrame.Create(scenarioBitmap));
  using (var scenarioStream = File.Create(Path.Combine(args[0], "readable-scenario.png"))) scenarioPng.Save(scenarioStream);
  var dbRef = new RecordRef(Guid.NewGuid(), 1, "quests.quests", Guid.NewGuid());
  var dbRecord = new RecordDto(dbRef, "testQuest", "Задание", [], [new("QUEST testQuest", "Живая цель", "Описание цели", true)]);
  host.Reader.Current = new(new(dbRef.SessionId, 1, 1), "fixture", true, [dbRecord]);
  var dbCode = "QuestComplete(0, \"testQuest\")";
  code.Text = dbCode; view.RefreshAnalysis(false);
  var dbHelp = view.HelpAt(code, dbCode.IndexOf("testQuest", StringComparison.Ordinal));
  Check(dbHelp != null && dbHelp.Body.Contains("Живая цель") && dbHelp.Body.Contains("не сохранён"), "DB hover resolves typed quest key and unsaved text");
  var helpPanel = (StackPanel)((ScrollViewer)view.HelpView(dbHelp!)).Content;
  helpPanel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  Check(host.Reader.LastNavigation == dbRef, "DB hover button navigates exact host reference");
  code.CaretIndex = dbCode.IndexOf("testQuest", StringComparison.Ordinal);
  view.NavigateSelectedDatabaseReference();
  Check(host.Reader.LastNavigation == dbRef && host.Reader.NavigationCount == 2, "direct DB navigation uses current caret reference");
  DatabaseContext? ResolveDb(string text, int position = 0) => DatabaseReferences.Resolve(ScriptAnalyzer.Analyze(text, view.Registry), position, host.Reader.Current);
  Check(ResolveDb("QuestComplete(0, \"absent\")") is { Records.Length: 0 } missing && missing.Explanation.Contains("не найден"), "missing quest is scoped to current snapshot");
  Check(ResolveDb("QuestComplete(0, key)") is { Records.Length: 0 } dynamicKey && dynamicKey.Explanation.Contains("вычисляется"), "dynamic key is not guessed");
  Check(ResolveDb("GetObjectByID(\"testQuest\")", 17) == null, "MOB object ID is not a quest record");
  Check(ResolveDb("GSGetVar(0, \"testQuest\")", 16) == null, "GS key is not a quest record");
  Check(ResolveDb("ConsoleString(\"QUEST testQuest\")", 19) is { Records.Length: 1 } textMatch && textMatch.Explanation.Contains("не доказывает"), "exact text resource match does not invent function semantics");
  host.Reader.Current = host.Reader.Current with { Records = [dbRecord, dbRecord with { Reference = dbRef with { RecordId = Guid.NewGuid() } }] };
  Check(ResolveDb(dbCode) is { Records.Length: 2 }, "duplicate quest keys remain ambiguous");
  host.Reader.Current = host.Reader.Current with { Records = [dbRecord with { Texts = [new("QUEST testQuest", "Изменённый текст", "", false)] }] };
  Check(view.HelpAt(code, 0)!.Body.Contains("Изменённый текст"), "DB hover reads latest workspace edits");
  host.Reader.Current = host.Reader.Current with { Stamp = new(dbRef.SessionId, 2, 0), Records = [] };
  Check(ResolveDb(dbCode) is { Records.Length: 0 }, "workspace replacement drops old references");
  host.Reader.Current = new(new(Guid.Empty, 1, 0), "test", false, []);
  var questText = new TextDto("quest q0g", "Тестовый квест", "Описание\r\n#subobj  1\r\nПервый\r\nОдин\r\n#subobj  2\r\nВторой\r\nТекст второго\r\n#subobj  3\r\nТретий\r\nТри", true);
  if (args.Length > 4)
  {
    using var live = JsonDocument.Parse(File.ReadAllText(args[4]));
    var rawText = live.RootElement.GetProperty("text").GetString()!;
    int newline = rawText.IndexOf('\n');
    questText = new("quest q0g", rawText[..newline].TrimEnd('\r'), rawText[(newline + 1)..], false);
  }
  host.Reader.Current = new(new(dbRef.SessionId, 1, 1), "quest fixture", false, [dbRecord with { Name = "q0g", Texts = [questText] }]);
  const string gsSource = "GSSetVarMax(0, \"q.gz1g.q0g.2\", 1)";
  var gsContext = ResolveDb(gsSource)!;
  Check(gsContext.Records.Length == 1 && gsContext.Title.Contains(questText.Title) && gsContext.Title.Contains("Подпункт 2"), "GS quest maps zone quest and subobjective");
  Check(gsContext.FocusText != null && !gsContext.FocusText.Contains("#subobj") && !gsContext.FocusText.Contains("Текст второго\r\n#subobj"), "subobjective description ends before next command");
  Check(gsContext.Explanation.Contains("максимум") && gsContext.Explanation.Contains("не понижается"), "GS max does not reactivate completed state");
  Check(ResolveDb("GSSetVar(0, \"q.gz1g.q0g\", 3)")!.Explanation.Contains("провален"), "GS direct write explains failed state");
  Check(ResolveDb("GSGetVar(0, \"q.gz1g.q0g.2\")")!.Explanation.Contains("неизвестно"), "GS read does not invent runtime state");
  Check(ResolveDb("GSDelVar(0, \"q.gz1g.q0g.2\")")!.Explanation.Contains("вернёт 0"), "GS deletion describes absent value");
  Check(ResolveDb("GSSetVar(0, \"q.gz1g.q0g.2\", 19)")!.Explanation.Contains("нестандартное"), "GS nonstandard state not guessed");
  Check(ResolveDb("GSSetVar(0, \"q.gz1g.q0g.404\", 1)")!.FocusText!.Contains("отсутствует"), "missing subobjective explicitly unresolved");
  Check(ResolveDb("GSSetVar(0, \"q.gz1g.q0g.2.extra\", 1)") == null, "malformed quest key not treated as subobjective");
  Check(ResolveDb("SSetVarMax(0, \"q.gz1g.q0g.2\", 1)") == null, "unregistered misspelling is not aliased");
  if (args.Length > 4) Check(gsContext.Title.Contains("Найти дорогу к людям") && gsContext.Title.Contains("Пойти вперед") && gsContext.FocusText!.Contains("За развалинами"), "installed texts resolve actual q0g subobjective2");
  view.LoadWorkspace(new Workspace { Text = "Script QuestStage ( if () then ( " + gsSource + " ) )" }, false);
  Check(Control<StackPanel>(view, "ScenarioItems").Children.OfType<Button>().Any(b => ((TextBlock)b.Content).Text.Contains(questText.Title)), "readable scenario uses quest state names");
  view.Measure(new Size(1510, 922)); view.Arrange(new Rect(0, 0, 1510, 922)); view.UpdateLayout();
  var questBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); questBitmap.Render(view);
  var questPng = new PngBitmapEncoder(); questPng.Frames.Add(BitmapFrame.Create(questBitmap));
  using (var questStream = File.Create(Path.Combine(args[0], "quest-state.png"))) questPng.Save(questStream);
  host.Reader.Current = host.Reader.Current with { Records = [dbRecord with { Name = "q0g", Texts = [questText with { Description = "#subobj 2\nOne\n#subobj 2\nTwo" }] }] };
  Check(ResolveDb(gsSource)!.FocusText!.Contains("неоднозначна"), "duplicate subobjective markers remain ambiguous");
  host.Reader.Current = new(new(Guid.Empty, 1, 0), "test", false, []);
  view.LoadWorkspace(new Workspace(), false);
  code.AppendText("\r\n// pending before debounce");
  var recovery = view.CaptureState();
  var restored = TestViews.Demo(host);
  Check(restored.RestoreState(recovery).Succeeded && Control<TextBox>(restored, "CodeEditor").Text == code.Text, "recovery includes last keystroke before debounce");
  Check(restored.HasPendingChanges, "recovered unsaved code stays dirty");
  var card = TestViews.Demo(host);
  Control<CheckBox>(card, "UseElse").IsChecked = true;
  Control<TextBox>(card, "ElseBox").Text = "Sleep(2)";
  var cardRestored = TestViews.Demo(host);
  Check(cardRestored.RestoreState(card.CaptureState()).Succeeded && Control<CheckBox>(cardRestored, "UseElse").IsChecked == true, "pending else checkbox survives recovery");
  Check(cardRestored.ApplyCard() && cardRestored.Current.Blocks.Any(b => b.ElseActions != null), "restored pending card applies else branch");
  string before = code.Text;
  Check(!view.RestoreState("{\"Version\":999}"u8.ToArray()).Succeeded && code.Text == before, "unsupported recovery preserves current document");
  Check(!view.RestoreState("{}"u8.ToArray()).Succeeded && code.Text == before, "invalid recovery preserves current document");
  var entry = new Entry(); entry.Initialize(host); var entryView = (Grid)entry.CreateView();
  Check(entryView.Children.OfType<TabControl>().Count() == 0 && entryView.Children[0] is QuestView, "embedded root has direct QuestView without tab chrome");
  var relatedPanel = entryView.Children.OfType<Border>().Single();
  Check(relatedPanel.Visibility == Visibility.Collapsed, "related data is closed by default");
  Check(!entry.Execute(ModuleCommand.Find), "inactive module does not consume keyboard commands");
  entry.Activate(true); Check(entry.Execute(ModuleCommand.Find), "active module routes command");
  var recordRef = new RecordRef(Guid.NewGuid(), 1, "quests.briefings", Guid.NewGuid());
  host.Reader.Current = new(new(recordRef.SessionId, 1, 1), "test", true,
      [new(recordRef, "scene", "Сцена", [], [new("BRIEFING scene", "Несохранённая реплика", "Описание", true)])]);
  host.Reader.Notify();
  Control<Button>((QuestView)entryView.Children[0], "RelatedDataButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  Check(relatedPanel.Visibility == Visibility.Visible, "compact related data button opens panel");
  var relatedList = ((DockPanel)relatedPanel.Child).Children.OfType<ListBox>().Single();
  Check(relatedList.Items.Count == 1 && relatedList.Items[0].ToString()!.Contains("Несохранённая реплика") && relatedList.Items[0].ToString()!.Contains("[не сохранено]"), "related panel uses live unsaved host text");
  var entryCode = Control<TextBox>((QuestView)entryView.Children[0], "CodeEditor");
  entryCode.Text = dbCode; entryCode.CaretIndex = 0;
  host.Reader.Current = new(new(dbRef.SessionId, 1, 2), "fixture", true, [dbRecord]); host.Reader.Notify();
  Check(relatedList.Items.Count == 1 && relatedList.Items[0].ToString()!.Contains("Живая цель"), "selected call panel resolves quest");
  host.Reader.Current = host.Reader.Current with { Records = [dbRecord with { Texts = [new("QUEST testQuest", "Обновлено без сохранения", "Описание", true)] }] }; host.Reader.Notify();
  Check(relatedList.Items[0].ToString()!.Contains("Обновлено без сохранения"), "visible context panel follows host edits");
  entryView.Measure(new Size(1510, 922)); entryView.Arrange(new Rect(0, 0, 1510, 922)); entryView.UpdateLayout();
  var dbBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); dbBitmap.Render(entryView);
  var dbPng = new PngBitmapEncoder(); dbPng.Frames.Add(BitmapFrame.Create(dbBitmap));
  using (var dbStream = File.Create(Path.Combine(args[0], "database-links.png"))) dbPng.Save(dbStream);
  host.Reader.Current = host.Reader.Current with { Stamp = new(recordRef.SessionId, 2, 0), Records = [] }; host.Reader.Notify();
  Check(relatedList.Items.Count == 0, "workspace generation change removes stale related records");
  ((DockPanel)((DockPanel)relatedPanel.Child).Children[0]).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  Check(relatedPanel.Visibility == Visibility.Collapsed, "related data panel closes without losing editor");
  entry.Dispose(); entry.Dispose(); Check(!entry.Execute(ModuleCommand.Find), "disposed module does not consume commands");
  Check(host.Reader.Subscribers == 0, "dispose unsubscribes host events");
  var documentHost = new TestDocumentHost(); var shared = TestViews.Demo(documentHost);
  var sharedCode = Control<TextBox>(shared, "CodeEditor"); string original = sharedCode.Text;
  Check(!sharedCode.IsUndoEnabled, "embedded code uses host history only");
  sharedCode.AppendText("\r\n// host edit");
  Check(documentHost.Get(shared.DocumentId!.Value)!.Text == sharedCode.Text, "keystroke published immediately to host");
  shared.RunCommand(ModuleCommand.Undo);
  Check(sharedCode.Text == original, "host undo restores code without republishing");
  shared.RunCommand(ModuleCommand.Redo);
  Check(sharedCode.Text.EndsWith("// host edit"), "host redo restores code");
  Control<CheckBox>(shared, "UseElse").IsChecked = true;
  shared.RunCommand(ModuleCommand.Undo);
  Check(Control<CheckBox>(shared, "UseElse").IsChecked == false, "host undo restores pending else checkbox");
  shared.RunCommand(ModuleCommand.Redo);
  Check(Control<CheckBox>(shared, "UseElse").IsChecked == true, "host redo restores pending else checkbox");
  Check(shared.ApplyCard() && shared.Current.Blocks.Any(b => b.ElseActions != null), "host session applies recovered card");
  shared.RunCommand(ModuleCommand.Save);
  Check(shared.HasPendingChanges && documentHost.SaveAllCalls == 1, "unsupported SaveAll does not clear pending changes");
  var codec = new DocumentCodec(); var snapshot = documentHost.Get(shared.DocumentId.Value)!;
  Check(codec.Validate(snapshot).Succeeded && codec.Prepare(snapshot, "malachite-project").Result.Succeeded, "codec validates and prepares project through host boundary");
  var formatHost = new TestDocumentHost(); var formatView = TestViews.Demo(formatHost);
  Check(formatHost.Registrations[formatView.DocumentId!.Value].SaveArtifactKind == "malachite-project", "new document registers project save kind");
  var sourceToken = Guid.NewGuid();
  formatView.LoadOpenedFile(new("sample.scr", "test", ImmutableArray.CreateRange(System.Text.Encoding.UTF8.GetBytes("WorldScript()"))) { SourceToken = sourceToken });
  Check(formatView.SaveArtifactKind == "script-text" && formatHost.Registrations[formatView.DocumentId!.Value].SourceToken == sourceToken, "opened script preserves source token and text save kind");
  var formatRecovery = formatView.CaptureState(); var freshHost = new TestDocumentHost(); var freshView = TestViews.Demo(freshHost);
  Check(freshView.RestoreState(formatRecovery).Succeeded && freshHost.Registrations[freshView.DocumentId!.Value].SaveArtifactKind == "script-text" && freshHost.Registrations[freshView.DocumentId.Value].SourceToken == null, "recovery without host document registers text kind without invented source permission");
  formatView.LoadOpenedFile(new("sample.malachite", "test", ImmutableArray.CreateRange(new Workspace { SourcePath = "original.mob" }.SaveProject())));
  Check(formatView.SaveArtifactKind == "malachite-project" && formatHost.Registrations[formatView.DocumentId!.Value].SaveArtifactKind == "malachite-project", "project uses opened extension instead of embedded source path");
  Control<TextBox>(formatView, "CodeEditor").AppendText("\n// edit"); formatView.RunCommand(ModuleCommand.Undo);
  Check(formatView.SaveArtifactKind == "malachite-project", "undo retains save kind");
  formatView.Stop(); freshView.Stop();
  var currentRecovery = shared.CaptureState(); var reattached = TestViews.Demo(documentHost);
  var replacedTemporary = documentHost.Get(reattached.DocumentId!.Value)!;
  documentHost.Close(replacedTemporary.DocumentId, replacedTemporary.Revision, true);
  Check(reattached.RestoreState(currentRecovery).Succeeded && reattached.DocumentId == shared.DocumentId, "recovery reattaches host identity after temporary document was replaced");
  reattached.Stop();
  documentHost.RejectChanges = true; sharedCode.AppendText("\r\n// keep conflict");
  Check(sharedCode.Text.EndsWith("// keep conflict") && !documentHost.Get(shared.DocumentId.Value)!.Text.EndsWith("// keep conflict"), "rejected host transaction preserves local input");
  var conflictRecovery = shared.CaptureState(); var conflictView = TestViews.Demo(host);
  Check(conflictView.RestoreState(conflictRecovery).Succeeded && Control<TextBox>(conflictView, "CodeEditor").Text.EndsWith("// keep conflict"), "recovery includes rejected input");
  shared.Stop(); conflictView.Stop();
  var registry = new CommandRegistry(); var help = new HelpService(registry);
  AddonVerification.Run(registry, help, Check, args[0]);
  if (args.Length > 1 && args[1] != "-")
  {
   var excluded = new List<string>(); int maps = 0;
   var folderPaths = Directory.GetFiles(args[1], "*.mob");
   var folderClock = System.Diagnostics.Stopwatch.StartNew();
   int lastProgress = 0;
   var folderResult = QuestView.IndexFolder(folderPaths, registry, new DiskIndexBrowser(), (done, name) => lastProgress = done);
   Check(folderResult.Maps.Count > 1 && lastProgress == folderPaths.Length && folderResult.Maps.Count + folderResult.Errors.Count == folderPaths.Length, "full folder index completes with progress for every selected MOB");
   Check(folderResult.Maps.All(m => m.Quests != null), "full folder index retains quest models for atlas");
   var cacheClock = System.Diagnostics.Stopwatch.StartNew();
   var initialCache = MapIndexCache.Build(folderPaths, [], registry, new DiskIndexBrowser());
   long coldMs = cacheClock.ElapsedMilliseconds; cacheClock.Restart();
   var warmCache = MapIndexCache.Build(folderPaths, initialCache.Cache, registry, new DiskIndexBrowser());
   Check(initialCache.Maps.Length == folderResult.Maps.Count && warmCache.Parsed == 0 && warmCache.Reused == folderPaths.Length,
       "actual complete Maps directory reopens without reparsing any unchanged source including known unreadable map");
   Check(JsonSerializer.Serialize(initialCache.Maps) == JsonSerializer.Serialize(warmCache.Maps), "actual MOB cache preserves complete quest graph across reopen");
   File.WriteAllText(Path.Combine(args[0], "maps-cache.json"), JsonSerializer.Serialize(new { coldMs, warmMs = cacheClock.ElapsedMilliseconds, cacheBytes = initialCache.Cache.Length, initialCache.Parsed, warmCache.Reused, maps = warmCache.Maps.Length, errors = warmCache.Errors }));
   File.WriteAllText(Path.Combine(args[0], "folder-index.json"), JsonSerializer.Serialize(new { selected = folderPaths.Length, indexed = folderResult.Maps.Count, errors = folderResult.Errors, elapsedMs = folderClock.ElapsedMilliseconds, lastProgress }));
   var questMaps = new List<QuestMap>();
   foreach (string path in Directory.GetFiles(args[1], "*.mob"))
   {
    var bytes = File.ReadAllBytes(path);
    try
    {
     var mob = new MobDocument(bytes); var analysis = ScriptAnalyzer.Analyze(mob.Text, registry);
     var atlasMap = QuestAtlas.Read(analysis, path, mob.Hash);
     questMaps.Add(atlasMap);
     Check(atlasMap.Points.All(p => p.Offset >= 0 && p.Offset < mob.Text.Length), "quest index valid source offsets: " + Path.GetFileName(path));
     Check(bytes.SequenceEqual(mob.Encode(mob.Text)), "module core no-op roundtrip: " + Path.GetFileName(path));
     Check(!analysis.Issues.Any(i => i.Code == "UNKNOWN"), "module catalog coverage: " + Path.GetFileName(path)); maps++;
    }
    catch (InvalidDataException ex)
    {
     excluded.Add(Path.GetFileName(path) + ": " + ex.Message);
     Check(Path.GetFileName(path).Equals("ZoneMainMenuNew.mob", StringComparison.OrdinalIgnoreCase), "only known internal-NUL map excluded");
    }
    Check(SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(bytes)), "source unchanged: " + Path.GetFileName(path));
   }
   var realRoute = QuestAtlas.Build("q1g", questMaps, atlasSnapshot with { Records = [] });
   var failedWrites = realRoute.Points.Where(p => p.Maximum && p.WrittenValue == 3 && p.Key == "q.gz3g.q1g").ToArray();
   var activeReads = realRoute.Points.Where(p => p.Key == "q.gz3g.q1g" && p.Predicate == new QuestPredicate("=", 1)).ToArray();
   Check(failedWrites.Length > 0 && activeReads.Length > 0, "real white-wolf quest reproduces max3 and equal1 usages across indexed maps");
   Check(!realRoute.Connections.Any(e => failedWrites.Any(p => p.Id == e.From) && activeReads.Any(p => p.Id == e.To)), "real white-wolf failure never points to active-state check");
   File.WriteAllText(Path.Combine(args[0], "white-wolf-route.json"), JsonSerializer.Serialize(realRoute, new JsonSerializerOptions { WriteIndented = true }));
   File.WriteAllText(Path.Combine(args[0], "maps.json"), JsonSerializer.Serialize(new { maps, excluded }));
   var actualHost = new TestDocumentHost(); var actual = TestViews.Demo(actualHost);
   string mapPath = Path.Combine(args[1], "zone6.mob"); var mapBytes = File.ReadAllBytes(mapPath);
   AuditRegression.Lifecycle(mapBytes, Check);
   var atlasFileHost = new AtlasFileHost(); var atlasView = TestViews.Demo(atlasFileHost);
   var atlasMob = new MobDocument(mapBytes);
   atlasFileHost.Browser.File = new(mapPath, "test", ImmutableArray.CreateRange(atlasMob.Encode(atlasMob.Text + "\n//changed after indexing")));
   var destinationPoint = new QuestPoint("test", "q0g", "", "0", mapPath, atlasMob.Hash, "target", 10, 1, "test", "", false);
   var beforeNavigation = Control<TextBox>(atlasView, "CodeEditor").Text;
   atlasView.NavigateQuestPoint(destinationPoint);
   Check(Control<TextBox>(atlasView, "CodeEditor").Text == beforeNavigation && atlasFileHost.Message.Contains("изм"), "stale MOB hash blocks route navigation without losing document");
   atlasFileHost.Browser.File = new(mapPath, "test", ImmutableArray.CreateRange(mapBytes));
   atlasView.NavigateQuestPoint(destinationPoint);
   Check(atlasView.Workspace.SourcePath == mapPath && Control<TextBox>(atlasView, "CodeEditor").CaretIndex == 10, "route navigation opens verified MOB at source offset");
   atlasView.Stop();
   actual.LoadOpenedFile(new(mapPath, Convert.ToHexString(SHA256.HashData(mapBytes)), ImmutableArray.CreateRange(mapBytes)));
   Check(actual.SaveArtifactKind == "mob" && actualHost.Registrations[actual.DocumentId!.Value].SaveArtifactKind == "mob", "opened MOB registers MOB save kind");
   var call = actual.Current.Calls.First(c => c.Name == "CreateVoiceFX" && c.Line == 912);
   var editor = Control<TextBox>(actual, "CodeEditor"); editor.Select(call.Arguments[3].Start, call.Arguments[4].End - call.Arguments[3].Start);
   editor.SelectedText = "232312сц, ауц"; actual.RefreshAnalysis(false);
   Control<ListBox>(actual, "Diagnostics").SelectedItem = actual.Current.Issues.First(i => i.Line == 912 && i.Code == "INVALID_NUMBER");
   Check(actual.Selected?.Name == call.Owner, "embedded zone6 diagnostic selects owning script");
   Check(actual.Current.Issues.Any(i => i.Line == 912 && i.Code == "INVALID_NUMBER"), "embedded zone6 malformed number diagnosed");
   var invalidSnapshot = actualHost.Get(actual.DocumentId!.Value)!;
   Check(!new DocumentCodec().Prepare(invalidSnapshot, "mob").Result.Succeeded,
       "host codec refuses malformed MOB script on common save path");
   bool rejectedMob = false;
   try { DocumentCodec.PrepareMob(actual.Workspace); } catch (InvalidDataException) { rejectedMob = true; }
   Check(rejectedMob, "standalone MOB export uses same validation gate");
   actual.RunCommand(ModuleCommand.Undo);
   Check(editor.Text == actual.Workspace.Mob!.Text, "embedded zone6 host undo restores original");
   var validPrepared = new DocumentCodec().Prepare(actualHost.Get(actual.DocumentId!.Value)!, "mob");
   Check(validPrepared.Result.Succeeded && validPrepared.Bytes.SequenceEqual(mapBytes), "valid MOB common save preserves exact no-op bytes");
   var projectBytes = actual.Workspace.SaveProject();
   var projectView = TestViews.Demo(actualHost); projectView.LoadOpenedFile(new("embedded.malachite", "test", ImmutableArray.CreateRange(projectBytes)));
   Check(projectView.Workspace.Mob != null && projectView.SaveArtifactKind == "malachite-project", "project containing actual MobBase saves as project, never as MOB");
   projectView.Stop();
   actual.Measure(new Size(1510, 922)); actual.Arrange(new Rect(0, 0, 1510, 922)); actual.UpdateLayout();
   var bmp = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); bmp.Render(actual);
   var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using (var stream = File.Create(Path.Combine(args[0], "embedded.png"))) png.Save(stream);
   actual.Stop();
  }
  if (args.Length > 6)
  {
      var portableView = TestViews.Demo(new TestHost());
      Check(portableView.RestoreState(File.ReadAllBytes(args[6])).Succeeded && portableView.Workspace.Mob != null &&
          Control<TextBox>(portableView, "CodeEditor").Text.EndsWith("// audit edit 99"),
          "large MOB self-contained recovery works without host blob service");
      portableView.Stop();
  }
  foreach (var v in new[] { view, restored, card, cardRestored }) v.Stop();
  if (args.Length > 3)
  {
   var loader = Assembly.LoadFrom(Path.Combine(args[2], "Modules.Host.dll")).GetType("DatabaseEditor.Modules.Host.ModuleLoader", true)!;
   var loadResult = loader.GetMethod("Load")!.Invoke(null, [args[3], new TestDocumentHost()])!;
   var errors = ((IEnumerable<string>)loadResult.GetType().GetProperty("Errors")!.GetValue(loadResult)!).ToArray();
   Check(errors.Length == 0, "real host loader: " + string.Join("; ", errors));
   var modules = (System.Collections.IEnumerable)loadResult.GetType().GetProperty("Modules")!.GetValue(loadResult)!;
   int count = 0;
   foreach (object loaded in modules)
   {
    var instance = (IWpfEditorModule)loaded.GetType().GetProperty("Instance")!.GetValue(loaded)!;
    var moduleView = instance.CreateView(); instance.Activate(true);
    moduleView.Measure(new Size(1510, 922)); moduleView.Arrange(new Rect(0, 0, 1510, 922)); moduleView.UpdateLayout();
    Check(moduleView is not Window && instance.Execute(ModuleCommand.Find), "separately loaded package creates embedded view and routes commands");
    Check(instance.RestoreRecovery(instance.CaptureRecovery()).Succeeded, "separately loaded package recovery roundtrip");
    var bmp = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); bmp.Render(moduleView);
    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using (var stream = File.Create(Path.Combine(args[0], "package-view.png"))) png.Save(stream);
    var embeddedView = (FrameworkElement)((Grid)moduleView).Children[0];
    ((Button)embeddedView.FindName("RelatedDataButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    moduleView.Measure(new Size(1510, 922)); moduleView.Arrange(new Rect(0, 0, 1510, 922)); moduleView.UpdateLayout();
    var relatedBitmap = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); relatedBitmap.Render(moduleView);
    var relatedPng = new PngBitmapEncoder(); relatedPng.Frames.Add(BitmapFrame.Create(relatedBitmap));
    using (var stream = File.Create(Path.Combine(args[0], "package-related.png"))) relatedPng.Save(stream);
    instance.Dispose(); count++;
   }
   Check(count == 1, "real loader found exactly one Malachite module");
  }
  Directory.CreateDirectory(args[0]);
  File.WriteAllText(Path.Combine(args[0], "report.json"), JsonSerializer.Serialize(new { status = failures == 0 ? "PASS" : "FAIL", failures, checks }, new JsonSerializerOptions { WriteIndented = true }));
  app.Shutdown(); return failures == 0 ? 0 : 1;
 }
}
internal sealed class TestHost : IModuleHost
{
 public TestReader Reader { get; } = new();
 public IWorkspaceReader Workspace => Reader;
 public IModuleFiles Files { get; } = new TestFiles();
 public ImmutableArray<string> Capabilities => ["workspace.read.v1", "navigation.record.v1", "files.export-copy.v1"];
 public void Report(string message) { }
}
internal sealed class AtlasFileHost : IModuleHost
{
 public AtlasBrowser Browser { get; } = new();
 public string Message = "";
 public IWorkspaceReader Workspace { get; } = new TestReader();
 public IModuleFiles Files => Browser;
 public ImmutableArray<string> Capabilities => [];
 public void Report(string message) => Message = message;
}
internal sealed class AtlasBrowser : IModuleFileBrowser
{
 public OpenedFile File = new("", "", []);
 public OpenedFile Read(string path) => path == File.DisplayPath ? File : throw new IOException("not selected");
 public ImmutableArray<string> SelectFolder(string extension) => [File.DisplayPath];
 public OpenedFile? Open(string filter) => null;
 public Result Export(string name, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
}
internal sealed class TestReader : IWorkspaceReader
{
 EventHandler<WorkspaceEvent>? changed;
 public int Subscribers => changed?.GetInvocationList().Length ?? 0;
 public event EventHandler<WorkspaceEvent>? Changed { add => changed += value; remove => changed -= value; }
 public WorkspaceSnapshot Current { get; set; } = new(new(Guid.Empty, 1, 0), "test", false, []);
 public void Notify() => changed?.Invoke(this, new(Current.Stamp, "changed"));
 public WorkspaceSnapshot Snapshot() => Current;
 public RecordRef? LastNavigation { get; private set; }
 public int NavigationCount { get; private set; }
 public Result Navigate(RecordRef reference) { NavigationCount++; LastNavigation = reference; return reference.SessionId == Current.Stamp.SessionId && reference.Generation == Current.Stamp.Generation ? Result.Ok : new(ResultStatus.Conflict, "stale"); }
}
internal sealed class TestFiles : IModuleFiles
{
 public OpenedFile? Open(string filter) => null;
 public Result Export(string name, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
}
internal sealed class TestDocumentHost : IModuleDocumentHost, IModuleDocuments, IModuleTransactions, IModuleSave
{
 readonly Dictionary<Guid, DocumentSnapshot> documents = [];
 public Dictionary<Guid, DocumentRegistration> Registrations { get; } = [];
 readonly Stack<DocumentSnapshot> undo = [], redo = [];
 public IWorkspaceReader Workspace { get; } = new TestReader();
 public IModuleFiles Files { get; } = new TestFiles();
 public ImmutableArray<string> Capabilities => ["workspace.read.v1", "navigation.record.v1", "files.export-copy.v1", "documents.session.v1", "transactions.v1", "saves.artifact-kind.v1"];
 public IModuleDocuments Documents => this;
 public IModuleTransactions Transactions => this;
 public IModuleSave Save => this;
 public event EventHandler<DocumentEvent>? Changed;
 public bool RejectChanges; public int SaveAllCalls; public bool CompleteSave; public Result ExportResult = new(ResultStatus.Cancelled);
 public void Report(string message) { }
 public Result RegisterCodec(string owner, string format, int version, IDocumentCodec codec) => Result.Ok;
 public DocumentSnapshot Open(DocumentRegistration r) { var d = new DocumentSnapshot(Guid.NewGuid(), r.OwnerId, r.FormatId, r.StateVersion, r.DisplayName, 0, r.Text, r.State, r.InitiallyDirty, "test"); documents.Add(d.DocumentId, d); Registrations[d.DocumentId] = r; return d; }
 public DocumentSnapshot? Get(Guid id) => documents.GetValueOrDefault(id);
 public Result Close(Guid id, long revision, bool discardChanges) { if (!documents.TryGetValue(id, out var d) || d.Revision != revision) return new(ResultStatus.Conflict); documents.Remove(id); undo.Clear(); redo.Clear(); return Result.Ok; }
 public ImmutableArray<DocumentSnapshot> List(string owner) => documents.Values.Where(d => d.OwnerId == owner).ToImmutableArray();
 public Result Apply(ChangeSet change)
 {
  var op = (UpdateDocument)change.Operations.Single(); var before = documents[op.DocumentId];
  if (RejectChanges || op.ExpectedRevision != before.Revision) return new(ResultStatus.Conflict, "test conflict");
  undo.Push(before); redo.Clear(); Publish(before with { Revision = before.Revision + 1, Text = op.Text, State = op.State, IsDirty = true }); return Result.Ok;
 }
 void Publish(DocumentSnapshot d) { documents[d.DocumentId] = d; Changed?.Invoke(this, new(d.DocumentId, d.Revision, "changed")); }
 public Result Undo() => Move(undo, redo);
 public Result Redo() => Move(redo, undo);
 Result Move(Stack<DocumentSnapshot> from, Stack<DocumentSnapshot> to)
 { if (from.Count == 0) return new(ResultStatus.Unavailable); var d = from.Pop(); var current = documents[d.DocumentId]; to.Push(current); Publish(d with { Revision = current.Revision + 1 }); return Result.Ok; }
 public Result Export(Guid id, string kind, string name, string filter) => ExportResult;
 public SaveReport SaveAll() { SaveAllCalls++; if (CompleteSave) { foreach (var d in documents.Values.ToArray()) Publish(d with { IsDirty = false }); return new([], true); } return new([new(Guid.Empty, "", "Unsupported", "not implemented")], false); }
}

internal sealed class DiskIndexBrowser : IModuleFiles, IModuleFileBrowser
{
 public OpenedFile? Open(string filter) => null;
 public ImmutableArray<string> SelectFolder(string extension) => [];
 public OpenedFile Read(string path) { var bytes = File.ReadAllBytes(path); return new(path, Convert.ToHexString(SHA256.HashData(bytes)), ImmutableArray.CreateRange(bytes)); }
 public Result Export(string name, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
}
