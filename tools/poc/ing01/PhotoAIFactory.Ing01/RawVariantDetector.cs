using System.Buffers.Binary;

namespace PhotoAIFactory.Ing01;

internal sealed record RawVariant(string Classification, int MaxWidth, int MaxHeight, bool ProcessingSupported);

internal static class RawVariantDetector
{
    public static RawVariant Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        var order = reader.ReadBytes(2);
        var little = order.SequenceEqual(new byte[] { (byte)'I', (byte)'I' });
        if (!little && !order.SequenceEqual(new byte[] { (byte)'M', (byte)'M' }))
            return new RawVariant("UNKNOWN_RAW_VARIANT", 0, 0, false);
        if (ReadU16(reader, little) != 42) return new RawVariant("UNKNOWN_RAW_VARIANT", 0, 0, false);
        var first = ReadU32(reader, little);
        var visited = new HashSet<uint>();
        var pending = new Queue<uint>(); pending.Enqueue(first);
        var maxWidth = 0; var maxHeight = 0;
        while (pending.Count > 0 && visited.Count < 64)
        {
            var offset = pending.Dequeue();
            if (offset == 0 || !visited.Add(offset) || offset >= stream.Length - 2) continue;
            stream.Position = offset;
            var count = ReadU16(reader, little);
            if (count > 4096) continue;
            for (var index = 0; index < count; index++)
            {
                if (stream.Position + 12 > stream.Length) break;
                var tag = ReadU16(reader, little); var type = ReadU16(reader, little); var values = ReadU32(reader, little);
                var raw = reader.ReadBytes(4); if (raw.Length != 4) break;
                if (tag is 256 or 257)
                {
                    var value = ReadInlineValue(raw, type, little);
                    if (tag == 256) maxWidth = Math.Max(maxWidth, checked((int)value)); else maxHeight = Math.Max(maxHeight, checked((int)value));
                }
                else if (tag == 330)
                {
                    foreach (var child in ReadOffsets(stream, reader, raw, type, values, little)) pending.Enqueue(child);
                }
            }
            if (stream.Position + 4 <= stream.Length) { var next = ReadU32(reader, little); if (next != 0) pending.Enqueue(next); }
        }
        var supported = Math.Max(maxWidth, maxHeight) >= 6000;
        return new RawVariant(supported ? "FULL_SIZE_RAW" : "UNSUPPORTED_RAW_VARIANT", maxWidth, maxHeight, supported);
    }

    private static IEnumerable<uint> ReadOffsets(Stream stream, BinaryReader reader, byte[] inline, ushort type, uint count, bool little)
    {
        if (type != 4 || count == 0 || count > 32) yield break;
        if (count == 1) { yield return ReadU32(inline, little); yield break; }
        var saved = stream.Position; var offset = ReadU32(inline, little);
        if (offset < stream.Length && offset + count * 4L <= stream.Length)
        {
            stream.Position = offset;
            for (var index = 0; index < count; index++) yield return ReadU32(reader, little);
        }
        stream.Position = saved;
    }

    private static uint ReadInlineValue(byte[] bytes, ushort type, bool little) => type switch
    {
        3 => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
        4 => ReadU32(bytes, little),
        _ => 0
    };
    private static ushort ReadU16(BinaryReader reader, bool little) { var b = reader.ReadBytes(2); return little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b); }
    private static uint ReadU32(BinaryReader reader, bool little) { var b = reader.ReadBytes(4); return ReadU32(b, little); }
    private static uint ReadU32(byte[] bytes, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
