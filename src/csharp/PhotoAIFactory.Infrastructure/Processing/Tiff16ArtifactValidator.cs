using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PhotoAIFactory.Infrastructure.Processing;

internal sealed record ValidatedTiff16(
    string Sha256,
    long SizeBytes,
    int Width,
    int Height,
    int BitsPerSample,
    int Channels,
    byte[] AuthenticXmp);

internal static class Tiff16ArtifactValidator
{
    public static async Task<ValidatedTiff16> ValidateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidDataException("TIFF output is missing or empty.");

        await using var stream = new FileStream(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var header = new byte[8];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var little = header[0] == (byte)'I' && header[1] == (byte)'I';
        var big = header[0] == (byte)'M' && header[1] == (byte)'M';
        if (!little && !big)
            throw new InvalidDataException("TIFF byte order marker is invalid.");
        if (ReadU16(header.AsSpan(2, 2), little) != 42)
            throw new InvalidDataException(
                "Pass 1 requires a classic TIFF output.");

        var ifdOffset = ReadU32(header.AsSpan(4, 4), little);
        stream.Seek(ifdOffset, SeekOrigin.Begin);
        var countBytes = new byte[2];
        await ReadExactAsync(stream, countBytes, cancellationToken).ConfigureAwait(false);
        var count = ReadU16(countBytes, little);

        int? width = null;
        int? height = null;
        int? channels = null;
        int[]? bits = null;

        var entry = new byte[12];
        for (var i = 0; i < count; i++)
        {
            await ReadExactAsync(stream, entry, cancellationToken).ConfigureAwait(false);
            var tag = ReadU16(entry.AsSpan(0, 2), little);
            if (tag is not (256 or 257 or 258 or 277))
                continue;

            var type = ReadU16(entry.AsSpan(2, 2), little);
            var itemCount = ReadU32(entry.AsSpan(4, 4), little);
            var values = await ReadUnsignedValuesAsync(
                stream,
                entry,
                type,
                itemCount,
                little,
                cancellationToken).ConfigureAwait(false);

            switch (tag)
            {
                case 256:
                    width = checked((int)values.Single());
                    break;
                case 257:
                    height = checked((int)values.Single());
                    break;
                case 258:
                    bits = values.Select(value => checked((int)value)).ToArray();
                    break;
                case 277:
                    channels = checked((int)values.Single());
                    break;
            }
        }

        if (width is null or <= 0 || height is null or <= 0)
            throw new InvalidDataException("TIFF dimensions are invalid.");
        if (channels is not (3 or 4))
            throw new InvalidDataException(
                $"Pass 1 TIFF must have 3 or 4 channels, got {channels?.ToString() ?? "missing"}.");
        if (bits is null ||
            bits.Length == 0 ||
            bits.Any(value => value != 16))
        {
            throw new InvalidDataException(
                "Pass 1 TIFF must be 16 bits per sample on every channel.");
        }

        var xmp = await DarktableXmpExtractor.FromTiffAsync(
            full, cancellationToken).ConfigureAwait(false);

        stream.Seek(0, SeekOrigin.Begin);
        var sha = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();

        return new(
            sha,
            info.Length,
            width.Value,
            height.Value,
            16,
            channels.Value,
            xmp);
    }

    private static async Task<uint[]> ReadUnsignedValuesAsync(
        FileStream stream,
        byte[] entry,
        ushort type,
        uint count,
        bool little,
        CancellationToken cancellationToken)
    {
        if (type is not (3 or 4))
            throw new InvalidDataException(
                $"TIFF tag uses unsupported numeric type {type}.");

        var typeSize = type == 3 ? 2 : 4;
        var bytesNeeded = checked((int)(count * (uint)typeSize));
        if (bytesNeeded <= 0 || bytesNeeded > 4096)
            throw new InvalidDataException("TIFF numeric tag is unexpectedly large.");

        byte[] bytes;
        if (bytesNeeded <= 4)
        {
            bytes = entry.AsSpan(8, bytesNeeded).ToArray();
        }
        else
        {
            var offset = ReadU32(entry.AsSpan(8, 4), little);
            var returnPosition = stream.Position;
            stream.Seek(offset, SeekOrigin.Begin);
            bytes = new byte[bytesNeeded];
            await ReadExactAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
            stream.Seek(returnPosition, SeekOrigin.Begin);
        }

        var countInt = checked((int)count);
        var result = new uint[countInt];
        for (var i = 0; i < countInt; i++)
        {
            var slice = bytes.AsSpan(i * typeSize, typeSize);
            result[i] = type == 3
                ? ReadU16(slice, little)
                : ReadU32(slice, little);
        }
        return result;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> bytes, bool little) =>
        little
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadU32(ReadOnlySpan<byte> bytes, bool little) =>
        little
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer[read..],
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException();
            read += count;
        }
    }
}
