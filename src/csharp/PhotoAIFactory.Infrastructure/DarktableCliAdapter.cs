using System.Globalization;
using PhotoAIFactory.Application;

namespace PhotoAIFactory.Infrastructure;

public sealed class DarktableCliAdapter(string darktableCliPath, ProcessRunner runner) : IDarktableCli
{
    private readonly string _path = darktableCliPath;
    private readonly ProcessRunner _runner = runner;

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        ProcessExecutionResult result;
        try
        {
            result = await _runner.RunAsync(
                _path, ["--version"], TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("darktable-cli --version timed out.", ex);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"darktable-cli --version failed: {result.StdErr}");
        }

        return string.IsNullOrWhiteSpace(result.StdOut)
            ? result.StdErr.Trim()
            : result.StdOut.Trim();
    }

    public async Task<ProcessExecutionResult> ExportAsync(
        DarktableExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("Darktable input not found", request.InputPath);
        }

        if (request.XmpPath is not null && !File.Exists(request.XmpPath))
        {
            throw new FileNotFoundException("XMP not found", request.XmpPath);
        }

        if (request.JpegQuality is int quality && (quality < 5 || quality > 100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "Darktable JPEG quality must be between 5 and 100.");
        }

        if (request.TiffBitsPerSample is int requestedTiffBits &&
            requestedTiffBits is not (8 or 16 or 32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "Darktable TIFF bit depth must be 8, 16 or 32.");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);

        var args = new List<string> { NormalizePath(request.InputPath) };
        if (!string.IsNullOrWhiteSpace(request.XmpPath))
        {
            args.Add(NormalizePath(request.XmpPath!));
        }

        args.Add(NormalizePath(request.OutputPath));
        args.Add("--hq");
        args.Add(request.HighQuality ? "true" : "false");

        if (request.MaxWidth is not null)
        {
            args.Add("--width");
            args.Add(request.MaxWidth.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (request.MaxHeight is not null)
        {
            args.Add("--height");
            args.Add(request.MaxHeight.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(request.Style))
        {
            args.Add("--style");
            args.Add(request.Style!);
        }

        if (request.ApplyCustomPresets is bool applyCustomPresets)
        {
            args.Add("--apply-custom-presets");
            args.Add(applyCustomPresets ? "true" : "false");
        }

        if (!string.IsNullOrWhiteSpace(request.IccType))
        {
            args.Add("--icc-type");
            args.Add(request.IccType!);
        }

        args.Add("--verbose");

        if (request.JpegQuality is int jpegQuality)
        {
            args.Add("--core");
            AddCorePath(args, "--configdir", request.ConfigDirectory, createDirectory: true);
            AddCorePath(args, "--cachedir", request.CacheDirectory, createDirectory: true);
            if (!string.IsNullOrWhiteSpace(request.LibraryPath))
            {
                args.Add("--library");
                args.Add(string.Equals(request.LibraryPath, ":memory:", StringComparison.Ordinal)
                    ? request.LibraryPath
                    : NormalizePath(request.LibraryPath));
            }
            args.Add("--conf");
            args.Add(
                $"plugins/imageio/format/jpeg/quality={jpegQuality.ToString(CultureInfo.InvariantCulture)}");
        }

        if (request.JpegQuality is null &&
            (request.TiffBitsPerSample is not null ||
             request.TiffWriteRgb is not null ||
             !string.IsNullOrWhiteSpace(request.ConfigDirectory) ||
             !string.IsNullOrWhiteSpace(request.CacheDirectory) ||
             !string.IsNullOrWhiteSpace(request.LibraryPath)))
        {
            args.Add("--core");
            AddCorePath(
                args, "--configdir", request.ConfigDirectory, createDirectory: true);
            AddCorePath(
                args, "--cachedir", request.CacheDirectory, createDirectory: true);
            if (!string.IsNullOrWhiteSpace(request.LibraryPath))
            {
                args.Add("--library");
                args.Add(string.Equals(
                        request.LibraryPath, ":memory:", StringComparison.Ordinal)
                    ? request.LibraryPath
                    : NormalizePath(request.LibraryPath));
            }
            if (request.TiffBitsPerSample is int tiffBits)
            {
                args.Add("--conf");
                args.Add(
                    $"plugins/imageio/format/tiff/bpp={tiffBits.ToString(CultureInfo.InvariantCulture)}");
            }
            if (request.TiffWriteRgb is bool writeRgb)
            {
                args.Add("--conf");
                args.Add(
                    $"plugins/imageio/format/tiff/shortfile={(writeRgb ? "0" : "1")}");
            }
        }

        try
        {
            return await _runner.RunAsync(
                _path,
                args,
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("darktable-cli export timed out.", ex);
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static void AddCorePath(
        ICollection<string> arguments,
        string option,
        string? path,
        bool createDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var full = Path.GetFullPath(path);
        if (createDirectory)
            Directory.CreateDirectory(full);
        arguments.Add(option);
        arguments.Add(NormalizePath(full));
    }
}
