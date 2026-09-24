using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;
using DatabaseEditor.Modules.Host;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

internal static partial class Program
{
    static async Task CheckEditor(string[] args)
    {
        var window = new MainWindow(new EditorPreferencesStore(Path.Combine(args[2],"prefs.json")),new RecoverySnapshotStore(Path.Combine(args[2],"recovery")));
        var vm=(MainWindowViewModel)window.DataContext;
        await vm.OpenPathAsync(args[0]); Require(vm.HasDocument,"actual host opens database for editor integration");
        var adapter=new ModuleHostAdapter(window,vm); var loaded=ModuleLoader.Load(args[1],adapter);
        Require(loaded.Errors.Count==0 && loaded.Modules.Count==1,"actual host loads editor module");
        var module=loaded.Modules[0].Instance;
        try
        {
            var root=module.CreateView();module.Activate(true);
            var view=Children(root).OfType<FrameworkElement>().First(e=>e.GetType().Name=="QuestView");
            var bytes=File.ReadAllBytes(args[4]);var hash=Convert.ToHexString(SHA256.HashData(bytes));
            view.GetType().GetMethod("LoadOpenedFile")!.Invoke(view,[new OpenedFile(args[4],hash,ImmutableArray.CreateRange(bytes))]);
            view.GetType().GetMethod("Code_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[view,new RoutedEventArgs()]);
            root.Measure(new Size(1400,850));root.Arrange(new Rect(0,0,1400,850));root.UpdateLayout();
            view.GetType().GetMethod("AttachSyntax")!.Invoke(view,null);
            var editor=Children(root).OfType<TextBox>().Single(e=>e.Name=="CodeEditor");string initial=editor.Text;
            int position=initial.IndexOf('\n')+1;
            editor.Select(position,0);editor.SelectedText="// benchmark\r\n";editor.Select(position+3,0);
            var samples=new List<double>();long allocated=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<40;i++)
            {
                int at=editor.SelectionStart;
                var timer=Stopwatch.StartNew();editor.Select(at,0);editor.SelectedText="x";editor.Select(at+1,0);root.UpdateLayout();timer.Stop();samples.Add(timer.Elapsed.TotalMilliseconds);
                await Task.Delay(16);
            }
            long memory=GC.GetAllocatedBytesForCurrentThread()-allocated;
            string final=editor.Text;
            Require(vm.ModuleDocuments.Active.Single(d=>d.OwnerId=="malachite").Text==final,"every key reaches actual host save/recovery state");
            var beat=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(16)};int ticks=0;beat.Tick+=(_,_)=>ticks++;beat.Start();
            var analysisWait=Stopwatch.StartNew();
            object current;
            do { await Task.Delay(50); current=view.GetType().GetProperty("Current")!.GetValue(view)!; }
            while ((string)current.GetType().GetProperty("Text")!.GetValue(current)! != final && analysisWait.ElapsedMilliseconds < 10000);
            beat.Stop();
            File.WriteAllText(Path.Combine(args[2],"editor-performance.json"),JsonSerializer.Serialize(new {chars=initial.Length,edits=samples.Count,totalMs=samples.Sum(),maxMs=samples.Max(),allocatedBytes=memory,dispatcherTicks=ticks,analysisWaitMs=analysisWait.ElapsedMilliseconds,samples},new JsonSerializerOptions {WriteIndented=true}));
            Require((string)current.GetType().GetProperty("Text")!.GetValue(current)! == final,"background analysis catches latest actual-host text");
            Require(ticks>5,"dispatcher remains responsive during background feedback and analysis");
            Require(adapter.Undo().Succeeded && editor.Text.Length==final.Length-1,"actual host undo restores one insertion");
            Require(adapter.Redo().Succeeded && editor.Text==final,"actual host redo restores edited script");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[4])))==hash,"MOB unchanged by editor integration");
            Require(!window.IsVisible,"integration opens no visible application");
        }
        finally {module.Dispose();vm.Dispose();}
    }
}

