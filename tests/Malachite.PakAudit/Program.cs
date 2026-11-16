using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QuestStudio;
using DatabaseEditor.Modules.V1;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var results = new List<object>(); int failures=0;
        void Check(bool ok,string name) { results.Add(new { name, passed=ok }); if(!ok) failures++; }
        try
        {
            PakWorkspaceVerification.Run(Check,args[0]);
            var view=new QuestView(new Host(),Path.Combine(args[0],"catalog.json"));
            string text="WorldScript(\r\n"+string.Concat(Enumerable.Range(0,8000).Select(i=>$"GSSetVar(0, \"key{i}\", 1)\r\n"))+")";
            view.LoadWorkspace(new Workspace { Name="performance",Text=text },false);
            view.Measure(new Size(1400,850)); view.Arrange(new Rect(0,0,1400,850)); view.UpdateLayout();
            var editor=(TextBox)view.FindName("CodeEditor"); editor.CaretIndex=text.IndexOf("key",StringComparison.Ordinal)+3;
            var samples=new List<double>();
            for(int i=0;i<20;i++) { var clock=Stopwatch.StartNew(); editor.SelectedText="a"; editor.CaretIndex++; view.UpdateLayout(); samples.Add(clock.Elapsed.TotalMilliseconds); }
            Check(editor.Text.Length==text.Length+20,"large script keystrokes retained");
            view.Stop();
            File.WriteAllText(Path.Combine(args[0],"editor-performance.json"),JsonSerializer.Serialize(new { lines=8000, samples, totalMs=samples.Sum(), maxMs=samples.Max() }));
            if(args.Length>1)
            {
                var files=Directory.GetFiles(args[1],"*.mob"); var browser=new ReadOnlyBrowser(); var registry=new CommandRegistry();
                var clock=Stopwatch.StartNew();var cold=MapIndexCache.Build(files,[],registry,browser);double coldMs=clock.Elapsed.TotalMilliseconds;
                clock.Restart();var warm=MapIndexCache.Build(files,cold.Cache,registry,browser);double warmMs=clock.Elapsed.TotalMilliseconds;
                Check(warm.Parsed==0&&warm.Reused==files.Length&&warm.Maps.Length==cold.Maps.Length,"all unchanged MOB files reuse parsed cache");
                File.WriteAllText(Path.Combine(args[0],"maps-performance.json"),JsonSerializer.Serialize(new { files=files.Length,coldMs,warmMs,cold.Parsed,warm.Reused,cacheBytes=cold.Cache.Length,errors=cold.Errors }));
            }
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); failures++; }
        File.WriteAllText(Path.Combine(args[0],"report.json"),JsonSerializer.Serialize(new { failures, checks=results },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine($"Failures: {failures}"); return failures==0?0:1;
    }
    sealed class Host : IModuleHost,IWorkspaceReader,IModuleFiles
    {
        public IWorkspaceReader Workspace=>this; public IModuleFiles Files=>this; public ImmutableArray<string> Capabilities=>[];
        private readonly WorkspaceSnapshot snapshot=new(new(Guid.NewGuid(),1,0),"database.pak",false,[]);
        public WorkspaceSnapshot Snapshot()=>snapshot;
        public event EventHandler<WorkspaceEvent>? Changed { add {} remove {} }
        public Result Navigate(RecordRef reference)=>Result.Ok;
        public void Report(string message) {}
        public OpenedFile? Open(string filter)=>null;
        public Result Export(string name,string filter,ReadOnlyMemory<byte> bytes)=>new(ResultStatus.Cancelled);
    }
    sealed class ReadOnlyBrowser : IModuleFileBrowser
    {
        public OpenedFile? Open(string filter)=>null;
        public Result Export(string name,string filter,ReadOnlyMemory<byte> bytes)=>new(ResultStatus.Cancelled);
        public ImmutableArray<string> SelectFolder(string extension)=>[];
        public OpenedFile Read(string path) { byte[] bytes=File.ReadAllBytes(path);return new(path,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),ImmutableArray.CreateRange(bytes)); }
    }
}
