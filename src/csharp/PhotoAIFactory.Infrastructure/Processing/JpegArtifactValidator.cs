using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed record ValidatedJpeg(
    string Path,
    string Sha256,
    long SizeBytes,
    int Width,
    int Height);

public static class JpegArtifactValidator
{
    public static async Task<ValidatedJpeg> ValidateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Reveal JPEG does not exist.", full);
        }

        var info = new FileInfo(full);
        if (info.Length < 16)
        {
            throw new InvalidDataException("Reveal JPEG is too small to be valid.");
        }

        var (width, height) = ReadDimensions(full);
        ValidateEndOfImage(full);

        await using var stream = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();

        return new(full, hash, info.Length, width, height);
    }

    private static void ValidateEndOfImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(-2, SeekOrigin.End);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD9)
        {
            throw new InvalidDataException("Reveal JPEG is incomplete (missing EOI).");
        }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
        {
            throw new InvalidDataException("Reveal output is not a JPEG (missing SOI).");
        }

        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[5];
        while (stream.Position < stream.Length)
        {
            int prefix;
            do { prefix = stream.ReadByte(); } while (prefix >= 0 && prefix != 0xFF);
            if (prefix < 0) break;

            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0 || marker == 0xD9) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;

            if (stream.Read(lengthBytes) != 2) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2) throw new InvalidDataException("Invalid JPEG segment length.");

            if (IsStartOfFrame(marker))
            {
                if (stream.Read(frame) != 5) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(frame[1..3]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(frame[3..5]);
                if (width <= 0 || height <= 0)
                    throw new InvalidDataException("Reveal JPEG dimensions are invalid.");
                return (width, height);
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }

        throw new InvalidDataException("JPEG dimensions could not be decoded.");
    }

    private static bool IsStartOfFrame(int marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
}
