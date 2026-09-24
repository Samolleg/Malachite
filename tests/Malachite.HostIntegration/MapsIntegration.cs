using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.Host;
using DatabaseEditor.Modules.V1;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

internal static partial class Program
{
    static async Task CheckMaps(string[] args)
    {
        string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
        string fixture = Path.GetFullPath(args[4]); bool warm = Directory.Exists(fixture);
        string db = Path.Combine(fixture, "res", "database.res"), maps = Path.Combine(fixture, "Maps");
        if (!warm)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(db)!); Directory.CreateDirectory(maps);
            File.Copy(args[0], db);
            foreach (var name in new[] { "a", "b" })
            {
                byte[] text = Encoding.ASCII.GetBytes($"WorldScript(GSSetVarMax(0, \"q.gz1g.q0g\", 1))\0");
                using var stream = File.Create(Path.Combine(maps, name + ".mob")); using var writer = new BinaryWriter(stream);
                writer.Write(0xA000u); writer.Write((uint)(text.Length + 16)); writer.Write(0xACCEECCAu); writer.Write((uint)(text.Length + 8)); writer.Write(text);
            }
        }
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(args[2], "preferences.json")), new RecoverySnapshotStore(Path.Combine(args[2], "recovery")));
        var vm = (MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(db);
        Require(vm.HasDocument, "fixture DB opened by actual host");
        var adapter = new ModuleHostAdapter(window, vm, Path.Combine(args[2], "save-journal"));
        var loaded = ModuleLoader.Load(args[1], adapter);
        Require(loaded.Errors.Count == 0 && loaded.Modules.Count == 1, "exact package loads with final Maps-capable host");
        var module = loaded.Modules.Single().Instance;
        try
        {
            var root = module.CreateView(); module.Activate(true);
            root.Measure(new Size(1400, 850)); root.Arrange(new Rect(0, 0, 1400, 850)); root.UpdateLayout();
            var view = Children(root).OfType<FrameworkElement>().Single(v => v.GetType().Name == "QuestView");
            var type = view.GetType();
            var busy = type.GetField("folderIndexing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Ready()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while ((bool)busy.GetValue(view)!) { if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("Automatic Maps did not complete"); await Task.Delay(15); }
            }
            await Ready();
            var status = (TextBlock)view.FindName("StatusText");
            string message = status.Text;
            Require(message.Contains("Maps: 2 карт") && message.Contains(warm ? "из кэша 2 · обработано 0" : "из кэша 0 · обработано 2"), "actual host automatic sibling Maps discovery and " + (warm ? "separate-process cache reuse: " : "cold parse: ") + message);
            Require(!window.IsVisible && Application.Current.Windows.Cast<Window>().All(w => !w.IsVisible), "integration opened no visible windows");
            File.WriteAllText(Path.Combine(args[2], "maps-status.txt"), message);
            var editor = (TextBox)view.FindName("CodeEditor");
            string mobPath = Path.Combine(maps, "a.mob");
            type.GetMethod("LoadOpenedFile")!.Invoke(view, [adapter.Read(mobPath)]);
            if (!warm)
            {
                var original = File.ReadAllBytes(mobPath); editor.AppendText("\n// normal save fixture");
                ((Button)view.FindName("SaveChangesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!module.IsDirty && !File.ReadAllBytes(mobPath).SequenceEqual(original), "actual Save Changes button writes original fixture MOB and checkpoints");
                editor.AppendText("\n// export copy fixture"); var beforeCopy = File.ReadAllBytes(mobPath);
                var id = vm.ModuleDocuments.Active.Single(d => d.OwnerId == "malachite").DocumentId;
                var copy = adapter.ExportCopy(id, "malachite-project", "copy.malachite", "", (_, _, bytes) => { File.WriteAllBytes(Path.Combine(fixture, "copy.malachite"), bytes.ToArray()); return Result.Ok; });
                Require(copy.Succeeded && module.IsDirty && vm.ModuleDocuments.Get(id)!.IsDirty && File.ReadAllBytes(mobPath).SequenceEqual(beforeCopy), "real project copy preserves source bytes and source dirty flag");
                Require(module.Execute(ModuleCommand.Save) && !module.IsDirty && !File.ReadAllBytes(mobPath).SequenceEqual(beforeCopy), "Ctrl+S route saves original after exporting a project copy");
                // Reopen establishes a new workspace generation; refresh the changed map into the persisted index.
                await vm.OpenPathAsync(db); await Ready();
                Require(status.Text.Contains("из кэша 1 · обработано 1"), "same DB reopened after normal MOB save reparses only changed map: " + status.Text);
            }
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) == sourceHash, "live database remained unchanged; all writes confined to fixture MOB/copy/cache");
        }
        finally { module.Dispose(); vm.Dispose(); }
    }
}
