using System.Collections.Immutable;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class AutoMapsVerification
{
    public static void Run(Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var host = new MapsHost(); var view = new QuestView(host);
            host.Switch("db-a", "a.mob");
            Drain(view.SynchronizeMapsAsync());
            check(Maps(view).SequenceEqual(["a.mob"]) && host.Caches.ContainsKey("db-a"), "automatic discovery builds index and persists per-DB cache");
            ((Button)view.FindName("HomeOpen")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            check(view.Workspace.Name == "a.mob" && ((Grid)view.FindName("HomePanel")).Visibility == System.Windows.Visibility.Collapsed,
                "start screen opens actual discovered MOB through authorized host Read");
            int discovers = host.Discoveries;
            host.Reader.Current = host.Reader.Current with { Stamp = host.Reader.Current.Stamp with { Revision = 9 } };
            Drain(view.SynchronizeMapsAsync());
            check(host.Discoveries == discovers, "ordinary DB edits do not restart indexing");
            view.Stop();
            host.Switch("db-a", "a.mob"); var reopened = new QuestView(host);
            Drain(reopened.SynchronizeMapsAsync());
            check(((TextBlock)reopened.FindName("StatusText")).Text.Contains("из кэша 1 · обработано 0"), "fresh module and reopened DB reuse portable index despite new session");
            var gate = new TaskCompletionSource<WorkspaceMapsResult>(); host.Gate = gate;
            host.Switch("db-slow", "slow.mob"); var slowStamp = host.Reader.Current.Stamp;
            var slow = reopened.SynchronizeMapsAsync();
            host.Gate = null; host.Switch("db-b", "b.mob"); Drain(reopened.SynchronizeMapsAsync());
            gate.SetResult(new(Result.Ok, slowStamp, ["slow.mob"])); Drain(slow);
            check(Maps(reopened).SequenceEqual(["b.mob"]) && !host.Caches.ContainsKey("db-slow"), "late prior-DB discovery cannot replace new index or persist wrong cache");
            host.Switch("db-empty"); Drain(reopened.SynchronizeMapsAsync());
            check(Maps(reopened).Length == 0, "empty Maps clears previous database index");
            host.Switch("db-missing"); host.Missing = true; Drain(reopened.SynchronizeMapsAsync());
            check(((TextBlock)reopened.FindName("StatusText")).Text.Contains("не найдена") && Maps(reopened).Length == 0, "missing neighboring Maps reports manual alternative without stale maps");
            host.Missing = false; host.CacheFailure = true; host.Switch("db-no-cache", "a.mob"); Drain(reopened.SynchronizeMapsAsync());
            check(Maps(reopened).Length == 1 && ((TextBlock)reopened.FindName("StatusText")).Text.Contains("кэш не сохранён"), "unavailable cache storage still permits live index");
            host.CacheFailure = false; gate = new(); host.Gate = gate; host.Switch("db-stopped", "a.mob");
            var stopStamp = host.Reader.Current.Stamp; var stopping = reopened.SynchronizeMapsAsync(); reopened.Stop();
            int reads = host.Reads; gate.SetResult(new(Result.Ok, stopStamp, ["a.mob"])); Drain(stopping);
            check(host.Reads == reads && !host.Caches.ContainsKey("db-stopped"), "stopped module ignores pending automatic discovery");

            var entry = new Malachite.Module.Entry(); host.Gate = null; host.Switch("entry-a", "a.mob");
            entry.Initialize(host); entry.CreateView();
            var entryView = (QuestView)typeof(Malachite.Module.Entry).GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!;
            Until(() => !Busy(entryView));
            check(Maps(entryView).SequenceEqual(["a.mob"]), "module creation starts automatic Maps discovery");
            host.Switch("entry-b", "b.mob"); host.Reader.Notify(); Until(() => !Busy(entryView));
            check(Maps(entryView).SequenceEqual(["b.mob"]), "database change updates Maps even while module tab inactive");
            entry.Dispose();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    static string[] Maps(QuestView view) => ((List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!).Select(m => m.Path).ToArray();
    static bool Busy(QuestView view) => (bool)typeof(QuestView).GetField("folderIndexing", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
    static void Drain(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    static void Until(Func<bool> complete)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!complete())
        {
            if (clock.Elapsed.TotalSeconds > 15) throw new TimeoutException("Automatic Maps test timed out.");
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
    sealed class MapsHost : IModuleMapsCacheHost, IWorkspaceMapsCache, IModuleFileBrowser
    {
        public readonly TestReader Reader = new();
        public readonly Dictionary<string, ImmutableArray<byte>> Caches = [];
        public readonly MapCacheVerification.MemoryBrowser Browser = new();
        public TaskCompletionSource<WorkspaceMapsResult>? Gate;
        public int Discoveries, Reads;
        public bool Missing, CacheFailure;
        string[] paths = [];
        public void Switch(string db, params string[] files)
        {
            Reader.Current = new(new(Guid.NewGuid(), 1, 0), db, false, []); paths = files;
            foreach (var file in files) Browser.Files[file] = MapCacheVerification.Mob("WorldScript()");
        }
        public IWorkspaceReader Workspace => Reader;
        public IModuleFiles Files => this;
        public IWorkspaceMapsCache MapsCache => this;
        public ImmutableArray<string> Capabilities => ["workspace.maps-cache.v1"];
        public void Report(string message) { }
        public Task<WorkspaceMapsResult> DiscoverAsync(WorkspaceStamp expected, CancellationToken cancellationToken = default)
        { Discoveries++; return Gate?.Task ?? Task.FromResult(new WorkspaceMapsResult(Missing ? new(ResultStatus.Unavailable) : Result.Ok, expected, paths.ToImmutableArray())); }
        public Task<WorkspaceCacheResult> LoadAsync(WorkspaceStamp expected, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceCacheResult(Result.Ok, Caches.GetValueOrDefault(Reader.Current.DisplayPath, [])));
        public Task<Result> SaveAsync(WorkspaceStamp expected, ImmutableArray<byte> bytes, CancellationToken cancellationToken = default)
        { if (CacheFailure) return Task.FromResult(new Result(ResultStatus.Failed, "test disk full")); Caches[Reader.Current.DisplayPath] = bytes; return Task.FromResult(Result.Ok); }
        public OpenedFile Read(string path) { Reads++; return Browser.Read(path); }
        public ImmutableArray<string> SelectFolder(string extension) => paths.ToImmutableArray();
        public OpenedFile? Open(string filter) => null;
        public Result Export(string name, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
    }
}
