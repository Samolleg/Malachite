using System.IO;
using System.Text.Json;

namespace QuestStudio;

public sealed record MapCatalogEntry(bool Favorite = false, string Note = "", DateTime? LastOpenedUtc = null);

// Personal metadata never enters MOB, project exports or the database.
public sealed class MapCatalogStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Malachite", "map-catalog.json");
    sealed record Catalog(int Version, Dictionary<string, MapCatalogEntry> Entries);
    readonly string path;
    Dictionary<string, MapCatalogEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; private set; }
    public MapCatalogStore(string path) { this.path = path; Read(); }
    static string Key(string path) => Path.GetFullPath(path);
    public MapCatalogEntry Get(string map) => entries.GetValueOrDefault(Key(map)) ?? new();
    void Read()
    {
        Error = null;
        try
        {
            if (!File.Exists(path)) { entries = new(StringComparer.OrdinalIgnoreCase); return; }
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Слишком большой каталог личных пометок.");
            var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllBytes(path));
            if (catalog is not { Version: 1, Entries: not null }) throw new InvalidDataException("Неизвестный формат личных пометок.");
            if (catalog.Entries.Any(p => !Path.IsPathFullyQualified(p.Key) || p.Value?.Note == null || p.Value.Note.Length > 500)) throw new InvalidDataException("Некорректные личные пометки.");
            entries = new(catalog.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Error = "Личные пометки не прочитаны: " + ex.Message; }
    }
    public void Update(string map, Func<MapCatalogEntry, MapCatalogEntry> change)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        using var writeLock = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Read();
        if (Error != null) throw new IOException(Error);
        string key = Key(map);
        var next = change(Get(map));
        if (next.Note.Length > 500) throw new ArgumentException("Заметка должна содержать не больше 500 символов.");
        var updated = new Dictionary<string, MapCatalogEntry>(entries, StringComparer.OrdinalIgnoreCase) { [key] = next };
        string temporary = Path.Combine(directory, ".map-catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Catalog(1, updated));
            if (bytes.Length > 8 * 1024 * 1024) throw new IOException("Каталог личных пометок превышает 8 МБ.");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
            entries = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
