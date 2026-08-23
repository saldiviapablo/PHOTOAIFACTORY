using System.Buffers.Binary;

namespace PhotoAIFactory.Infrastructure.Qa;

public sealed record ImageDimensions(int Width, int Height, int BitsPerSample, int Channels);

public static class ImageHeaderInspector
{
    public static ImageDimensions Inspect(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        if (stream.Length < 4)
            throw new InvalidDataException("File is too small to be a valid image.");

        var b1 = stream.ReadByte();
        var b2 = stream.ReadByte();

        // JPEG: 0xFF, 0xD8
        if (b1 == 0xFF && b2 == 0xD8)
        {
            return InspectJpeg(stream);
        }

        // PNG: 0x89, 0x50, 0x4E, 0x47
        if (b1 == 0x89 && b2 == 0x50 && stream.ReadByte() == 0x4E && stream.ReadByte() == 0x47)
        {
            return InspectPng(stream);
        }

        throw new InvalidDataException("Unsupported or corrupt image format.");
    }

    private static ImageDimensions InspectJpeg(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];

        while (stream.Position < stream.Length)
        {
            var b = stream.ReadByte();
            if (b != 0xFF)
                continue;

            // Skip fill bytes (0xFF)
            while (b == 0xFF && stream.Position < stream.Length)
                b = stream.ReadByte();

            if (b == -1 || b == 0xD9 || b == 0xDA) // EOI or Start of Scan
                break;

            // Check if SOF marker (SOF0 = 0xC0, SOF1 = 0xC1, SOF2 = 0xC2, SOF3 = 0xC3, SOF5..SOF7, SOF9..SOF11, SOF13..SOF15)
            if (b is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (stream.Read(buffer[..2]) != 2)
                    throw new InvalidDataException("Truncated JPEG segment length.");

                var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
                if (length < 6)
                    throw new InvalidDataException("Invalid JPEG SOF length.");

                Span<byte> sofData = stackalloc byte[6];
                if (stream.Read(sofData) != 6)
                    throw new InvalidDataException("Truncated JPEG SOF header.");

                var precision = sofData[0];
                var height = BinaryPrimitives.ReadUInt16BigEndian(sofData[1..3]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(sofData[3..5]);
                var components = sofData[5];

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"Invalid JPEG dimensions: {width}x{height}");

                return new ImageDimensions(width, height, precision, components);
            }

            // Read segment length and skip
            if (stream.Read(buffer[..2]) != 2)
                break;

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
            if (segmentLength < 2)
                throw new InvalidDataException("Invalid JPEG segment length.");

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }

        throw new InvalidDataException("Could not find valid SOF marker in JPEG file.");
    }

    private static ImageDimensions InspectPng(Stream stream)
    {
        // Seek to IHDR chunk: starts at byte 16 (4 bytes length + 4 bytes 'IHDR' + 13 bytes data)
        stream.Seek(12, SeekOrigin.Begin);
        Span<byte> ihdr = stackalloc byte[17];
        if (stream.Read(ihdr) != 17)
            throw new InvalidDataException("Truncated PNG header.");

        // Check 'IHDR'
        if (ihdr[0] != (byte)'I' || ihdr[1] != (byte)'H' || ihdr[2] != (byte)'D' || ihdr[3] != (byte)'R')
            throw new InvalidDataException("PNG missing IHDR chunk.");

        var width = BinaryPrimitives.ReadInt32BigEndian(ihdr[4..8]);
        var height = BinaryPrimitives.ReadInt32BigEndian(ihdr[8..12]);
        var bitDepth = ihdr[12];
        var colorType = ihdr[13];

        int channels = colorType switch
        {
            0 => 1, // Grayscale
            2 => 3, // Truecolor (RGB)
            3 => 1, // Indexed
            4 => 2, // Grayscale with alpha
            6 => 4, // Truecolor with alpha (RGBA)
            _ => 3
        };

        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid PNG dimensions: {width}x{height}");

        return new ImageDimensions(width, height, bitDepth, channels);
    }
}
