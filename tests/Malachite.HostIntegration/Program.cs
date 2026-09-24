using System.IO;
using System.Collections.Immutable;
using System.Diagnostics;
using DatabaseEditor.Modules.V1;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using DatabaseEditor.Modules.Host;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

internal static partial class Program
{
 static readonly List<object> checks = [];
 static void Require(bool ok, string name) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
 [STAThread] static int Main(string[] args)
 {
  try { return Run(args); }
  catch (Exception ex)
  {
   Directory.CreateDirectory(args[2]);
   File.WriteAllText(Path.Combine(args[2], "report.json"), JsonSerializer.Serialize(new { status = "FAIL", error = ex.ToString(), checks }, new JsonSerializerOptions { WriteIndented = true }));
   return 1;
  }
 }
 static int Run(string[] args)
 {
  Directory.CreateDirectory(args[2]);
  // Load only host resources: App.xaml StartupUri would open a second, visible window.
  var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
  var markup = XDocument.Load(args[3]); XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
  var resources = new XElement(markup.Root!.Element(p + "Application.Resources")!.Element(p + "ResourceDictionary")!);
  foreach (var attribute in markup.Root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.LocalName != "xmlns"))
      resources.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:") && !attribute.Value.Contains(";assembly=") ? attribute.Value + ";assembly=EvilIslands.DatabaseEditor" : attribute.Value);
  foreach (var element in resources.Descendants().Where(e => e.Name.NamespaceName.StartsWith("clr-namespace:") && !e.Name.NamespaceName.Contains(";assembly=")))
      element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=EvilIslands.DatabaseEditor");
  app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
  var task = args.Length > 5 && args[5] == "editor-only" ? CheckEditor(args) : args.Length > 5 && args[5] == "maps-only" ? CheckMaps(args) : Check(args); task.ContinueWith(_ => app.Dispatcher.InvokeShutdown(), TaskScheduler.Default); Dispatcher.Run();
  string? error = null; try { task.GetAwaiter().GetResult(); } catch (Exception ex) { error = ex.ToString(); }
  File.WriteAllText(Path.Combine(args[2], "report.json"), JsonSerializer.Serialize(new { status = error == null ? "PASS" : "FAIL", error, checks }, new JsonSerializerOptions { WriteIndented = true }));
  return error == null ? 0 : 1;
 }
 static async Task Check(string[] args)
 {
  string initialHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
  var window = new MainWindow(new EditorPreferencesStore(Path.Combine(args[2], "preferences.json")), new RecoverySnapshotStore(Path.Combine(args[2], "recovery")));
  var vm = (MainWindowViewModel)window.DataContext;
  await vm.OpenPathAsync(args[0]);
  Require(vm.HasDocument, "real database successfully opened");
  var beforeWindows = Application.Current.Windows.Cast<Window>().ToArray();
  var adapter = new ModuleHostAdapter(window, vm); var loaded = ModuleLoader.Load(args[1], adapter);
  Require(loaded.Errors.Count == 0 && loaded.Modules.Count == 1, "real host loads independent package: " + string.Join("; ", loaded.Errors));
  var module = loaded.Modules[0].Instance;
  try
  {
   var view = module.CreateView(); module.Activate(true);
   view.Measure(new Size(1400,850)); view.Arrange(new Rect(0,0,1400,850)); view.UpdateLayout();
   var afterWindows = Application.Current.Windows.Cast<Window>().ToArray();
   File.WriteAllText(Path.Combine(args[2], "windows.json"), JsonSerializer.Serialize(new { before = beforeWindows.Select(w => new { type = w.GetType().FullName, w.IsVisible }), after = afterWindows.Select(w => new { type = w.GetType().FullName, w.IsVisible }), mainVisible = window.IsVisible }));
   Require(afterWindows.SequenceEqual(beforeWindows) && !window.IsVisible && afterWindows.All(w => !w.IsVisible), "module adds no windows and shows none");
   var editor = Children(view).OfType<TextBox>().Single(t => t.Name == "CodeEditor");
   string old = editor.Text; editor.Text += "\n// integration";
   Require(vm.ModuleDocuments.List("malachite").Any(d => d.Text == editor.Text && d.IsDirty), "real host receives last keystroke before debounce");
   Require(!editor.IsUndoEnabled, "single history owner");
   Require(adapter.Undo().Succeeded && editor.Text == old, "real global Undo updates module");
   Require(adapter.Redo().Succeeded && editor.Text.EndsWith("// integration"), "real global Redo updates module");
   var useElse = Children(view).OfType<CheckBox>().Single(t => t.Name == "UseElse"); useElse.IsChecked = true;
   Require(adapter.Undo().Succeeded && useElse.IsChecked == false, "real global Undo handles pending else");
   Require(adapter.Redo().Succeeded && useElse.IsChecked == true, "real global Redo handles pending else");
   byte[] payload = module.CaptureRecovery().ToArray(); byte[] hostState = vm.ModuleDocuments.CaptureRecovery();
   Require(vm.ModuleDocuments.RestoreRecovery(hostState).Succeeded && module.RestoreRecovery(payload).Succeeded, "host recovery and module reattach");
   Require(useElse.IsChecked == true && editor.Text.EndsWith("// integration"), "recovery retains code and pending else");
   Require(vm.ModuleDocuments.Active.Where(d => d.OwnerId == "malachite").All(d => !vm.ModuleDocuments.Prepare(d.DocumentId, "malachite-project").Result.Succeeded) && module.IsDirty, "host preparation refuses unapplied card and keeps dirty without opening save dialog");
   Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) == initialHash, "source database unchanged");
   if (args.Length > 4)
   {
       Require(adapter.Capabilities.Contains("documents.blobs.v1"), "new host advertises durable document blobs");
       string mobPath = args[4]; byte[] mobBytes = File.ReadAllBytes(mobPath);
       string mobHash = Convert.ToHexString(SHA256.HashData(mobBytes));
       var questView = Children(view).Single(c => c.GetType().Name == "QuestView");
       questView.GetType().GetMethod("LoadOpenedFile")!.Invoke(questView,
           [new OpenedFile(mobPath, mobHash, ImmutableArray.CreateRange(mobBytes))]);
       string initialCode = editor.Text;
       bool restoreOnly = args.Length > 5 && args[5] == "restore-only";
       string finalCode; byte[] portable, durable;
       if (!restoreOnly)
       {
       long allocatedBefore = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
       int maxState = 0; const int edits = 100;
       for (int i = 0; i < edits; i++)
       {
           editor.Text += "\n// audit edit " + i;
           maxState = Math.Max(maxState, vm.ModuleDocuments.Active.Single(d => d.OwnerId == "malachite").State.Length);
       }
       long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, elapsedMs = timer.ElapsedMilliseconds;
       finalCode = editor.Text;
       Require(maxState < mobBytes.Length / 4, "per-edit state excludes full original MOB");
       for (int i = 0; i < edits; i++) Require(adapter.Undo().Succeeded, "large MOB undo " + i);
       Require(editor.Text == initialCode, "100 global undo operations restore exact initial MOB script");
       for (int i = 0; i < edits; i++) Require(adapter.Redo().Succeeded, "large MOB redo " + i);
       Require(editor.Text == finalCode, "100 global redo operations restore exact edited script");
       portable = module.CaptureRecovery().ToArray(); durable = vm.ModuleDocuments.CaptureRecovery();
       File.WriteAllBytes(Path.Combine(args[2], "host-recovery.json"), durable);
       File.WriteAllBytes(Path.Combine(args[2], "module-recovery.json"), portable);
       using (var json = JsonDocument.Parse(durable))
           File.WriteAllText(Path.Combine(args[2], "memory.json"), JsonSerializer.Serialize(new { mobBytes = mobBytes.Length, edits, maxStateBytes = maxState, elapsedMs, allocatedBytes = allocated, hostRecoveryBytes = durable.Length, portableRecoveryBytes = portable.Length }));
       }
       else
       {
           portable = File.ReadAllBytes(Path.Combine(args[6], "module-recovery.json"));
           durable = File.ReadAllBytes(Path.Combine(args[6], "host-recovery.json"));
           using var recoveredJson = JsonDocument.Parse(portable);
           finalCode = recoveredJson.RootElement.GetProperty("Code").GetString()!;
       }
       var freshWindow = new MainWindow(new EditorPreferencesStore(Path.Combine(args[2], "fresh-preferences.json")), new RecoverySnapshotStore(Path.Combine(args[2], "fresh-recovery")));
       var freshVm = (MainWindowViewModel)freshWindow.DataContext;
       await freshVm.OpenPathAsync(args[0]);
       var freshAdapter = new ModuleHostAdapter(freshWindow, freshVm);
       var freshLoaded = ModuleLoader.Load(args[1], freshAdapter);
       Require(freshLoaded.Errors.Count == 0 && freshLoaded.Modules.Count == 1, "fresh host loads module for durable recovery");
       var freshModule = freshLoaded.Modules.Single().Instance;
       try
       {
           var freshRoot = freshModule.CreateView(); freshModule.Activate(true);
           Require(freshVm.ModuleDocuments.RestoreRecovery(durable).Succeeded,
               "fresh host restores blobs before validating compact document state");
           var restoredResult = freshModule.RestoreRecovery(portable);
           Require(restoredResult.Succeeded, "module recovery reattaches restored compact host document: " + restoredResult.Message);
           freshRoot.Measure(new Size(1400, 850)); freshRoot.Arrange(new Rect(0, 0, 1400, 850)); freshRoot.UpdateLayout();
           var freshEditor = Children(freshRoot).OfType<TextBox>().Single(t => t.Name == "CodeEditor");
           Require(freshEditor.Text == finalCode, "fresh recovery preserves exact last edited script");
           freshEditor.Text += "\n// after restart";
           Require(freshAdapter.Undo().Succeeded && freshEditor.Text == finalCode, "new undo after recovery resolves original MOB blob");
       }
       finally { freshModule.Dispose(); freshVm.Dispose(); }
       Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mobPath))) == mobHash, "source MOB unchanged by stress and recovery checks");
   }
  }
  finally { module.Dispose(); vm.Dispose(); }
 }
 static IEnumerable<DependencyObject> Children(DependencyObject root)
 {
  yield return root;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Children(VisualTreeHelper.GetChild(root,i))) yield return child;
 }
}
