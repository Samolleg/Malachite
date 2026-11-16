using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

// Records are immutable. Document-only revisions share their record array;
// a DB/text revision gets a new array and cannot reuse an obsolete index.
internal sealed class WorkspaceReferenceIndex
{
    private static readonly ConditionalWeakTable<RecordDto[], WorkspaceReferenceIndex> Cache = new();
    private static readonly WorkspaceReferenceIndex Empty = new([]);
    private readonly Dictionary<string, RecordDto[]> quests;
    private readonly Dictionary<string, RecordDto[]> texts;
    private WorkspaceReferenceIndex(IEnumerable<RecordDto> records)
    {
        quests = records.Where(r => r.Reference.TableKey == "quests.quests")
            .GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        texts = records.SelectMany(r => r.Texts.Select(t => t.ResourceKey).Distinct(StringComparer.Ordinal).Select(k => (Key: k, Record: r)))
            .GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Record).ToArray(), StringComparer.Ordinal);
    }
    internal static WorkspaceReferenceIndex For(WorkspaceSnapshot snapshot) =>
        ImmutableCollectionsMarshal.AsArray(snapshot.Records) is { } records ? Cache.GetValue(records, r => new(r)) : Empty;
    internal RecordDto[] Quests(string name) => quests.GetValueOrDefault(name) ?? [];
    internal RecordDto[] Texts(string key) => texts.GetValueOrDefault(key) ?? [];
}
