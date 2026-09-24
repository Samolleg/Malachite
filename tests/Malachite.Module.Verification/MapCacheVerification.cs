using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class MapCacheVerification
{
    public static byte[] Mob(string code)
    {
        byte[] text = MobDocument.GameEncoding.GetBytes(code + "\0");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(MobDocument.RootId); writer.Write((uint)(text.Length + 16));
        writer.Write(MobDocument.LegacyId); writer.Write((uint)(text.Length + 8)); writer.Write(text);
        return stream.ToArray();
    }
    public static void Run(Action<bool, string> check)
    {
        var browser = new MemoryBrowser(); var registry = new CommandRegistry();
        browser.Files["a.mob"] = Mob("Script A(if()then(GSSetVarMax(0, \"q.gz1g.q1\", 1)))");
        browser.Files["b.mob"] = Mob("Script B(if(GSGetVar(0, \"q.gz1g.q1\"))then(QuestComplete(0, \"q1\")))");
        MapIndexCache.Scan Scan(byte[] bytes) => MapIndexCache.Build(browser.Files.Keys.ToArray(), bytes, registry, browser);
        var first = Scan([]); var reopened = Scan(first.Cache);
        check(first.Parsed == 2 && reopened.Parsed == 0 && reopened.Reused == 2, "cache reopen uses both unchanged parsed maps");
        check(JsonSerializer.Serialize(first.Maps) == JsonSerializer.Serialize(reopened.Maps), "cache roundtrip preserves quest predicates, source offsets and flags");
        browser.Files["a.mob"] = Mob("Script A(if()then(GSSetVarMax(0, \"q.gz1g.q1\", 2)))");
        var changed = Scan(reopened.Cache);
        check(changed.Parsed == 1 && changed.Reused == 1 && changed.Maps[0].Quests!.Points.Any(p => p.WrittenValue == 2), "same-sized changed MOB invalidated by bytes despite stale browser hash");
        browser.Files.Remove("b.mob"); browser.Files["c.mob"] = Mob("WorldScript()");
        var membership = Scan(changed.Cache);
        check(membership.Parsed == 1 && membership.Reused == 1 && membership.Maps.All(m => m.Path != "b.mob"), "added MOB parsed and removed MOB excluded from durable cache");
        check(Scan([1, 2, 3]).Parsed == 2, "corrupt cache rebuilt from authoritative MOB files");
        var old = JsonSerializer.Deserialize<MapIndexCache.Snapshot>(membership.Cache)!;
        check(Scan(JsonSerializer.SerializeToUtf8Bytes(old with { ParserVersion = "old parser" })).Parsed == 2, "parser version change invalidates cache");
        browser.Files["bad.mob"] = [1, 2, 3]; var broken = Scan(membership.Cache); var repeat = Scan(broken.Cache);
        check(broken.Errors.Length == 1 && repeat.Errors.Length == 1 && repeat.Parsed == 0 && repeat.Reused == 3, "unchanged unreadable map retains truthful diagnostic without reparsing");
        browser.Files["bad.mob"] = Mob("WorldScript()");
        check(Scan(repeat.Cache).Maps.Length == 3, "repaired unreadable map is detected");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { MapIndexCache.Build(["a.mob"], [], registry, browser, cancellation: cancellation.Token); check(false, "cache scan cancellation"); }
        catch (OperationCanceledException) { check(true, "cache scan cancellation"); }

        var host = new TestDocumentHost { ExportResult = Result.Ok }; var view = TestViews.Demo(host);
        var editor = (TextBox)view.FindName("CodeEditor"); editor.AppendText("\n// copy must not save source");
        typeof(QuestView).GetMethod("SaveProject", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null);
        check(view.HasPendingChanges && host.Get(view.DocumentId!.Value)!.IsDirty, "successful project copy leaves source dirty");
        ((Button)view.FindName("SaveChangesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(host.SaveAllCalls == 1 && view.HasPendingChanges, "visible Save Changes button invokes host SaveAll and preserves failed save");
        host.CompleteSave = true;
        view.RunCommand(ModuleCommand.Save);
        check(!view.HasPendingChanges && !host.Get(view.DocumentId!.Value)!.IsDirty, "successful normal save checkpoints source through host document event");
        view.Stop();
    }
    public sealed class MemoryBrowser : IModuleFileBrowser
    {
        public Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public OpenedFile Read(string path) => new(path, "deliberately stale metadata", ImmutableArray.CreateRange(Files[path]));
        public ImmutableArray<string> SelectFolder(string extension) => Files.Keys.ToImmutableArray();
        public OpenedFile? Open(string filter) => null;
        public Result Export(string name, string filter, ReadOnlyMemory<byte> bytes) => new(ResultStatus.Cancelled);
    }
}
