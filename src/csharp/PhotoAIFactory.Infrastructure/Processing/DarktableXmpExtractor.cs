using System.Buffers.Binary;
using System.Text;

namespace PhotoAIFactory.Infrastructure.Processing;

internal static class DarktableXmpExtractor
{
    private static readonly byte[] JpegXmpPrefix =
        Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

    public static async Task<byte[]> FromTiffAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
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
                "Only classic TIFF is supported by the Phase 5 validator.");

        var ifdOffset = ReadU32(header.AsSpan(4, 4), little);
        stream.Seek(ifdOffset, SeekOrigin.Begin);

        var countBytes = new byte[2];
        await ReadExactAsync(stream, countBytes, cancellationToken).ConfigureAwait(false);
        var count = ReadU16(countBytes, little);

        var entry = new byte[12];
        for (var i = 0; i < count; i++)
        {
            await ReadExactAsync(stream, entry, cancellationToken).ConfigureAwait(false);
            var tag = ReadU16(entry.AsSpan(0, 2), little);
            if (tag != 700)
                continue;

            var type = ReadU16(entry.AsSpan(2, 2), little);
            var itemCount = ReadU32(entry.AsSpan(4, 4), little);
            var byteCount = checked((long)itemCount * TypeSize(type));
            if (byteCount <= 0 || byteCount > 16 * 1024 * 1024)
                throw new InvalidDataException("TIFF XMP packet size is invalid.");

            byte[] payload;
            if (byteCount <= 4)
            {
                payload = entry.AsSpan(8, (int)byteCount).ToArray();
            }
            else
            {
                var offset = ReadU32(entry.AsSpan(8, 4), little);
                var returnPosition = stream.Position;
                stream.Seek(offset, SeekOrigin.Begin);
                payload = new byte[byteCount];
                await ReadExactAsync(stream, payload, cancellationToken)
                    .ConfigureAwait(false);
                stream.Seek(returnPosition, SeekOrigin.Begin);
            }

            ValidateDarktablePacket(payload);
            return payload;
        }

        throw new InvalidDataException(
            "Darktable TIFF does not contain an embedded XMP packet (TIFF tag 700).");
    }

    public static async Task<byte[]> FromJpegAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            throw new InvalidDataException("JPEG SOI is missing.");

        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xff)
                throw new InvalidDataException("JPEG marker stream is invalid.");

            var marker = bytes[offset + 1];
            offset += 2;
            if (marker is 0xd9 or 0xda)
                break;
            if (marker is >= 0xd0 and <= 0xd7 or 0x01)
                continue;

            if (offset + 2 > bytes.Length)
                break;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(
                bytes.AsSpan(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
                throw new InvalidDataException("JPEG segment length is invalid.");

            var payloadStart = offset + 2;
            var payloadLength = segmentLength - 2;
            if (marker == 0xe1 &&
                payloadLength > JpegXmpPrefix.Length &&
                bytes.AsSpan(payloadStart, JpegXmpPrefix.Length)
                    .SequenceEqual(JpegXmpPrefix))
            {
                var packet = bytes.AsSpan(
                    payloadStart + JpegXmpPrefix.Length,
                    payloadLength - JpegXmpPrefix.Length).ToArray();
                ValidateDarktablePacket(packet);
                return packet;
            }

            offset += segmentLength;
        }

        throw new InvalidDataException(
            "Darktable JPEG does not contain an authentic embedded XMP packet.");
    }

    public static void ValidateDarktablePacket(byte[] payload)
    {
        if (payload.Length == 0)
            throw new InvalidDataException("XMP packet is empty.");

        var text = Encoding.UTF8.GetString(payload);
        if (!text.Contains("http://darktable.sf.net/", StringComparison.Ordinal) ||
            !text.Contains("darktable:history", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "XMP packet is not a Darktable processing-history packet.");
        }
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => throw new InvalidDataException($"Unsupported TIFF field type {type}.")
    };

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
