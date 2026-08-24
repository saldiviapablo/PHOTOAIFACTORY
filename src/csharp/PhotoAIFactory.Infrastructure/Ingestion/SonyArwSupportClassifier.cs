using System.Buffers.Binary;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Infrastructure.Ingestion;

/// <summary>
/// Conservative V1 admission classifier derived from the ING-01 A7 IV fixtures.
/// It is not a general RAW decoder. Unknown/corrupt layouts are routed to review.
/// </summary>
public sealed class SonyArwSupportClassifier : IRawSupportClassifier
{
    private const int FullSizeMinimumLongEdge = 4000;

    public Task<RawSupportInfo> ClassifyAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var dimensions = InspectTiffDimensions(path, cancellationToken);
            var longEdge = Math.Max(dimensions.Width, dimensions.Height);
            if (longEdge >= FullSizeMinimumLongEdge)
            {
                return Task.FromResult(new RawSupportInfo(
                    RawSupportStatus.SupportedFullSize,
                    dimensions.Width,
                    dimensions.Height,
                    "FULL_SIZE_RAW"));
            }

            if (longEdge > 0)
            {
                return Task.FromResult(new RawSupportInfo(
                    RawSupportStatus.UnsupportedReduced,
                    dimensions.Width,
                    dimensions.Height,
                    "UNSUPPORTED_REDUCED_RAW"));
            }

            return Task.FromResult(Unknown(dimensions.Width, dimensions.Height));
        }
        catch (Exception ex) when (
            ex is IOException or EndOfStreamException or InvalidDataException or OverflowException)
        {
            return Task.FromResult(Unknown(0, 0));
        }
    }

    private static RawSupportInfo Unknown(int width, int height) =>
        new(RawSupportStatus.Unknown, width, height, "UNKNOWN_RAW_VARIANT");

    private static (int Width, int Height) InspectTiffDimensions(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var order = ReadBytesExact(reader, 2);
        var little = order[0] == (byte)'I' && order[1] == (byte)'I';
        var big = order[0] == (byte)'M' && order[1] == (byte)'M';
        if (!little && !big)
        {
            throw new InvalidDataException("ARW/TIFF byte order is invalid.");
        }

        if (ReadU16(reader, little) != 42)
        {
            throw new InvalidDataException("ARW/TIFF magic is invalid.");
        }

        var pending = new Queue<uint>();
        pending.Enqueue(ReadU32(reader, little));
        var visited = new HashSet<uint>();
        var maxWidth = 0;
        var maxHeight = 0;

        while (pending.Count > 0 && visited.Count < 64)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = pending.Dequeue();
            if (offset == 0 || !visited.Add(offset) || offset > stream.Length - 2)
            {
                continue;
            }

            stream.Position = offset;
            var count = ReadU16(reader, little);
            if (count > 4096)
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                if (stream.Position + 12 > stream.Length)
                {
                    break;
                }

                var tag = ReadU16(reader, little);
                var type = ReadU16(reader, little);
                var values = ReadU32(reader, little);
                var inline = ReadBytesExact(reader, 4);

                if (tag is 256 or 257)
                {
                    var value = ReadInlineValue(inline, type, little);
                    if (value <= int.MaxValue)
                    {
                        if (tag == 256) maxWidth = Math.Max(maxWidth, (int)value);
                        else maxHeight = Math.Max(maxHeight, (int)value);
                    }
                }
                else if (tag == 330)
                {
                    foreach (var child in ReadOffsets(stream, reader, inline, type, values, little))
                    {
                        pending.Enqueue(child);
                    }
                }
            }

            if (stream.Position + 4 <= stream.Length)
            {
                var next = ReadU32(reader, little);
                if (next != 0)
                {
                    pending.Enqueue(next);
                }
            }
        }

        return (maxWidth, maxHeight);
    }

    private static IEnumerable<uint> ReadOffsets(
        Stream stream,
        BinaryReader reader,
        byte[] inline,
        ushort type,
        uint count,
        bool little)
    {
        if (type != 4 || count == 0 || count > 32)
        {
            yield break;
        }

        if (count == 1)
        {
            yield return ReadU32(inline, little);
            yield break;
        }

        var saved = stream.Position;
        var offset = ReadU32(inline, little);
        if (offset < stream.Length && offset + count * 4L <= stream.Length)
        {
            stream.Position = offset;
            for (var index = 0; index < count; index++)
            {
                yield return ReadU32(reader, little);
            }
        }
        stream.Position = saved;
    }

    private static uint ReadInlineValue(byte[] bytes, ushort type, bool little) => type switch
    {
        3 => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes),
        4 => ReadU32(bytes, little),
        _ => 0
    };

    private static ushort ReadU16(BinaryReader reader, bool little)
    {
        var bytes = ReadBytesExact(reader, 2);
        return little
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadU32(BinaryReader reader, bool little) =>
        ReadU32(ReadBytesExact(reader, 4), little);

    private static uint ReadU32(byte[] bytes, bool little) =>
        little
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static byte[] ReadBytesExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count
            ? bytes
            : throw new EndOfStreamException();
    }
}
