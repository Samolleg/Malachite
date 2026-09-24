using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuestStudio;

// A lossless container adapter. Unknown chunks are copied byte for byte.
public sealed class MobDocument
{
    public const uint RootId = 0xA000, CodedId = 0xACCEECCB, LegacyId = 0xACCEECCA;
    public static readonly Encoding GameEncoding;
    static MobDocument()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        GameEncoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public sealed record Chunk(uint Id, int Offset, int Length);
    public byte[] Original { get; }
    public string Text { get; }
    public string Hash => Convert.ToHexString(SHA256.HashData(Original));
    public bool Coded => script.Id == CodedId;
    public uint Seed { get; }
    readonly Chunk root, script;
    readonly byte[] suffix;

    public static List<Chunk> Chunks(byte[] bytes, int start, int length)
    {
        var end = checked(start + length);
        if (start < 0 || end > bytes.Length) throw new InvalidDataException("Чанк выходит за границы MOB.");
        var result = new List<Chunk>();
        for (int p = start; p < end;)
        {
            if (end - p < 8) throw new InvalidDataException("Оборванный заголовок чанка.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4));
            if (size < 8 || size > end - p) throw new InvalidDataException($"Неверная длина чанка в позиции {p}.");
            result.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)), p, (int)size));
            p += (int)size;
        }
        return result;
    }
    public MobDocument(byte[] bytes)
    {
        if (bytes.Length > 128 * 1024 * 1024) throw new InvalidDataException("MOB больше 128 МиБ.");
        Original = bytes.ToArray();
        var roots = Chunks(Original, 0, Original.Length).Where(c => c.Id == RootId).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("Ожидается один ID_OBJECTDBFILE.");
        root = roots[0];
        var children = Chunks(Original, root.Offset + 8, root.Length - 8);
        var coded = children.Where(c => c.Id == CodedId).ToArray();
        var legacy = children.Where(c => c.Id == LegacyId).ToArray();
        if (coded.Length > 1 || legacy.Length > 1 || coded.Length + legacy.Length == 0)
            throw new InvalidDataException("Скриптовый чанк отсутствует или дублирован.");
        script = coded.FirstOrDefault() ?? legacy[0];
        var data = Original.AsSpan(script.Offset + 8, script.Length - 8).ToArray();
        if (Coded)
        {
            if (data.Length < 4) throw new InvalidDataException("Нет seed скрипта.");
            Seed = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = Transform(data.AsSpan(4), Seed);
        }
        int textLength = data.Length;
        while (textLength > 0 && data[textLength - 1] == 0) textLength--;
        if (data.AsSpan(0, textLength).Contains((byte)0)) throw new InvalidDataException("Нулевой байт внутри текста скрипта.");
        suffix = data.AsSpan(textLength).ToArray();
        Text = GameEncoding.GetString(data, 0, textLength);
    }
    static byte[] Transform(ReadOnlySpan<byte> bytes, uint state)
    {
        var output = new byte[bytes.Length];
        for (int i = 0; i < output.Length; i++)
        {
            state = unchecked(state * 214013 + 2531011);
            output[i] = (byte)(bytes[i] ^ (byte)(state >> 16));
        }
        return output;
    }
    public byte[] Encode(string text)
    {
        if (text == Text) return Original.ToArray();
        if (text.Contains('\0')) throw new InvalidDataException("Нулевой байт в сценарии.");
        var plain = GameEncoding.GetBytes(text).Concat(suffix).ToArray();
        var payload = Coded ? new byte[plain.Length + 4] : plain;
        if (Coded)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload, Seed);
            Transform(plain, Seed).CopyTo(payload, 4);
        }
        int newLength = checked(payload.Length + 8), delta = newLength - script.Length;
        var output = new byte[checked(Original.Length + delta)];
        Original.AsSpan(0, script.Offset).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(script.Offset), script.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(script.Offset + 4), (uint)newLength);
        payload.CopyTo(output, script.Offset + 8);
        Original.AsSpan(script.Offset + script.Length).CopyTo(output.AsSpan(script.Offset + newLength));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(root.Offset + 4), (uint)(root.Length + delta));
        var verified = new MobDocument(output);
        if (verified.Text != text) throw new InvalidDataException("Повторный разбор не совпал с текстом.");
        return output;
    }
}

public static class SafeFiles
{
    public static void WriteNew(string path, byte[] data)
    {
        // Export never overwrites a source or an existing file. Publish through an atomic rename.
        if (File.Exists(path)) throw new IOException("Файл уже существует. Выберите новое имя.");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(true); }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
