using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class AuditRegression
{
    public static void Lifecycle(byte[] mob, Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var host = new ClosingHost(mob);
        var view = new QuestView(host);
        try
        {
            ((Border)view.FindName("QuestAtlasPanel")).Visibility = Visibility.Visible;
            typeof(QuestView).GetMethod("Folder_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [new Button(), new RoutedEventArgs()]);
            check(host.Browser.Started.Wait(TimeSpan.FromSeconds(5)), "folder indexing started before stop");
            view.Stop(); host.Reader.Closed = true; host.Browser.Release.Set();
            // Drain the async-void handler, including posted progress and finally callbacks.
            var clock = Stopwatch.StartNew();
            do
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
            } while (clock.ElapsedMilliseconds < 300 || !host.Browser.Finished.IsSet && clock.Elapsed.TotalSeconds < 5);
            check(host.Browser.Finished.IsSet && host.Browser.Reads == 1, "stop cancels remaining MOB reads");
            check(host.Reader.AfterClose == 0, "no workspace snapshot or atlas refresh after module stop");
        }
        finally { host.Browser.Release.Set(); view.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    sealed class ClosingHost(byte[] bytes) : IModuleHost
    {
        public ImmutableArray<string> Capabilities => [];
        public ClosingReader Reader { get; } = new();
        public BlockingBrowser Browser { get; } = new(bytes);
        public IWorkspaceReader Workspace => Reader;
        public IModuleFiles Files => Browser;
        public void Report(string message) { }
    }
    sealed class ClosingReader : IWorkspaceReader
    {
        public bool Closed;
        public int AfterClose;
        public event EventHandler<WorkspaceEvent>? Changed { add { } remove { } }
        public WorkspaceSnapshot Snapshot() { if (Closed) AfterClose++; return new(new(Guid.Empty, 1, 0), "audit", false, []); }
        public Result Navigate(RecordRef reference) => Result.Ok;
    }
    sealed class BlockingBrowser(byte[] bytes) : IModuleFiles, IModuleFileBrowser
    {
        public readonly ManualResetEventSlim Started = new(), Release = new(), Finished = new();
        public int Reads;
        public OpenedFile? Open(string filter) => null;
        public ImmutableArray<string> SelectFolder(string extension) => ["a.mob", "b.mob", "c.mob"];
        public OpenedFile Read(string path)
        {
            Interlocked.Increment(ref Reads); Started.Set();
            try { if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return new(path, "audit", ImmutableArray.CreateRange(bytes)); }
            finally { Finished.Set(); }
        }
        public Result Export(string name, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
    }
}
