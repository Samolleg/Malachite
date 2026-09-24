using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

// Reflection is confined to the optional SDK boundary. No new contract type is
// statically referenced, so the same module still loads with older SDK binaries.
internal sealed class DocumentBlobs
{
    readonly object store;
    readonly MethodInfo put, get;
    readonly ConditionalWeakTable<MobDocument, BlobId> ids = new();
    readonly Dictionary<string, WeakReference<MobDocument>> decoded = new();
    static readonly ConditionalWeakTable<IModuleHost, DocumentBlobs> connections = new();
    sealed record BlobId(string Value);
    DocumentBlobs(object store, Type contract)
    { this.store = store; put = contract.GetMethod("Put")!; get = contract.GetMethod("Get")!; }
    public static DocumentBlobs? Connect(IModuleHost? host)
    {
        if (host == null || !host.Capabilities.Contains("documents.blobs.v1")) return null;
        var contract = host.GetType().GetInterface("DatabaseEditor.Modules.V1.IModuleBlobHost");
        var property = contract?.GetProperty("Blobs");
        return property?.GetValue(host) is { } service ? connections.GetValue(host, _ => new(service, property.PropertyType)) : null;
    }
    static object Invoke(MethodInfo method, object target, object argument)
    {
        object result = method.Invoke(target, [argument]) ?? throw new InvalidDataException("Пустой ответ хранилища MOB.");
        if (result.GetType().GetProperty("Result")?.GetValue(result) is not Result status || !status.Succeeded)
            throw new InvalidDataException(result.GetType().GetProperty("Result")?.GetValue(result) is Result failed ? failed.Message : "Неверный ответ хранилища MOB.");
        return result;
    }
    public string Put(MobDocument mob) => ids.GetValue(mob, document =>
    {
        var result = Invoke(put, store, ImmutableArray.CreateRange(document.Original));
        var hash = result.GetType().GetProperty("Sha256")?.GetValue(result) as string;
        if (!string.Equals(hash, document.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Хранилище вернуло неверный SHA-256 MOB.");
        if (decoded.Count >= 32) decoded.Clear();
        decoded[hash!] = new(document);
        return new(hash!.ToUpperInvariant());
    }).Value;
    public MobDocument Get(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Неверный идентификатор MOB.");
        // Immutable IDs remain available for the host session, including closed documents.
        // Reuse a verified container while a view owns it; fresh sessions get a fresh cache.
        if (decoded.TryGetValue(hash, out var entry) && entry.TryGetTarget(out var cached)) return cached;
        var result = Invoke(get, store, hash);
        if (result.GetType().GetProperty("Bytes")?.GetValue(result) is not ImmutableArray<byte> bytes || bytes.IsDefault)
            throw new InvalidDataException("Исходный MOB недоступен.");
        if (!Convert.ToHexString(SHA256.HashData(bytes.AsSpan())).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Повреждён исходный MOB: SHA-256 не совпал.");
        var document = new MobDocument(bytes.ToArray());
        if (decoded.Count >= 32) decoded.Clear();
        decoded[hash] = new(document);
        ids.GetValue(document, _ => new(hash));
        return document;
    }
}
