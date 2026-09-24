using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

// Optional SDK boundary, keeping the module loadable with older hosts.
internal sealed class WorkspaceMaps(object service, Type contract)
{
    public static WorkspaceMaps? Connect(IModuleHost host)
    {
        if (!host.Capabilities.Contains("workspace.maps-cache.v1")) return null;
        var property = host.GetType().GetInterface("DatabaseEditor.Modules.V1.IModuleMapsCacheHost")?.GetProperty("MapsCache");
        return property?.GetValue(host) is { } service ? new(service, property.PropertyType) : null;
    }
    async Task<object> Invoke(string name, params object[] arguments)
    {
        var task = (Task?)contract.GetMethod(name)?.Invoke(service, arguments) ?? throw new InvalidDataException("Служба индекса Maps недоступна.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new InvalidDataException("Пустой ответ службы Maps.");
    }
    public async Task<(Result Status, string[] Paths)> Discover(WorkspaceStamp stamp, CancellationToken ct)
    {
        var value = await Invoke("DiscoverAsync", stamp, ct);
        var status = (Result)value.GetType().GetProperty("Result")!.GetValue(value)!;
        var actual = (WorkspaceStamp)value.GetType().GetProperty("Stamp")!.GetValue(value)!;
        if (actual.SessionId != stamp.SessionId || actual.Generation != stamp.Generation) return (new(ResultStatus.Conflict, "База данных сменилась."), []);
        var paths = (ImmutableArray<string>)value.GetType().GetProperty("Paths")!.GetValue(value)!;
        return (status, paths.IsDefault ? [] : paths.ToArray());
    }
    public async Task<byte[]> Load(WorkspaceStamp stamp, CancellationToken ct)
    {
        var value = await Invoke("LoadAsync", stamp, ct);
        var status = (Result)value.GetType().GetProperty("Result")!.GetValue(value)!;
        if (!status.Succeeded) return [];
        var bytes = (ImmutableArray<byte>)value.GetType().GetProperty("Bytes")!.GetValue(value)!;
        return bytes.IsDefault ? [] : bytes.ToArray();
    }
    public async Task<Result> Save(WorkspaceStamp stamp, byte[] bytes, CancellationToken ct) =>
        (Result)await Invoke("SaveAsync", stamp, ImmutableArray.CreateRange(bytes), ct);
}
