using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

// Only parsed MOB data is cached. Quest titles and briefing effects always come from the current DB.
public static class MapIndexCache
{
    const int Schema = 1;
    static string ParserVersion => typeof(ScriptAnalyzer).Module.ModuleVersionId + ":" + typeof(QuestAtlas).Module.ModuleVersionId;
    public sealed record Item(string Path, string Hash, QuestView.IndexedMap? Map, string? Error);
    public sealed record Snapshot(int Version, Item[] Items, string ParserVersion);
    public sealed record Scan(QuestView.IndexedMap[] Maps, string[] Errors, byte[] Cache, int Parsed, int Reused);

    public static Scan Build(string[] paths, byte[] cache, CommandRegistry registry, IModuleFileBrowser browser,
        Action<int, string>? progress = null, CancellationToken cancellation = default)
    {
        var previous = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (cache.Length > 0 && JsonSerializer.Deserialize<Snapshot>(cache) is { Version: Schema, Items: not null } snapshot && snapshot.ParserVersion == ParserVersion)
                foreach (var item in snapshot.Items)
                    if (item is { Path: not null, Hash: not null } && Valid(item)) previous[item.Path] = item;
        }
        catch (JsonException) { /* Cache is disposable; the source is authoritative. */ }
        var next = new List<Item>(); var maps = new List<QuestView.IndexedMap>(); var errors = new List<string>();
        int parsed = 0, reused = 0, done = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            string? hash = null;
            try
            {
                var file = browser.Read(path); cancellation.ThrowIfCancellationRequested();
                // Validate actual bytes, not timestamps, size or a cached host source token.
                hash = Convert.ToHexString(SHA256.HashData(file.Content.AsSpan()));
                Item item;
                if (previous.TryGetValue(path, out var old) && old.Hash == hash) { item = old; reused++; }
                else
                {
                    parsed++;
                    var document = Workspace.FromBytes(path, file.Content.ToArray());
                    if (document.Mob == null) throw new InvalidDataException("Файл не является MOB.");
                    var analysis = ScriptAnalyzer.Analyze(document.Text, registry); cancellation.ThrowIfCancellationRequested();
                    item = new(path, hash, Describe(document, analysis, path, hash), null);
                }
                next.Add(item);
                if (item.Map != null) maps.Add(item.Map);
                else errors.Add(item.Error!);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
            {
                string error = Path.GetFileName(path) + ": " + ex.Message; errors.Add(error);
                if (hash != null) next.Add(new(path, hash, null, error));
            }
            progress?.Invoke(++done, Path.GetFileName(path));
        }
        cancellation.ThrowIfCancellationRequested();
        return new(maps.ToArray(), errors.ToArray(), JsonSerializer.SerializeToUtf8Bytes(new Snapshot(Schema, next.ToArray(), ParserVersion)), parsed, reused);
    }

    internal static QuestView.IndexedMap Describe(Workspace document, Analysis analysis, string path, string hash)
    {
        MobObject[]? objects = null;
        try { if (document.Mob != null) objects = MobObjects.Read(document.Mob).ToArray(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or OverflowException) { }
        var questMap = QuestAtlas.Read(analysis, path, hash);
        return new(path, hash, analysis.Flags) { Quests = questMap with { Objects = objects ?? [],
            Gaps = objects == null ? questMap.Gaps.Append("Объекты MOB не прочитаны; имена объектов неизвестны.").ToArray() : questMap.Gaps },
            ScriptCount = analysis.Blocks.Count(b => !b.World), ObjectCount = objects?.Length };
    }
    static bool Valid(Item item) => item.Map == null ? !string.IsNullOrEmpty(item.Error) :
        item.Map.Path == item.Path && item.Map.Hash == item.Hash && item.Map.Flags != null &&
        item.Map.Quests is { Points: not null, Calls: not null, Briefings: not null } q && q.Path == item.Path && q.Hash == item.Hash &&
        item.Map.Flags.All(f => f != null) && q.Points.All(p => p != null) && q.Calls.All(c => c != null) && q.Briefings.All(b => b != null);
}
