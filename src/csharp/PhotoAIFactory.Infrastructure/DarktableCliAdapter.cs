using PhotoAIFactory.Application;

namespace PhotoAIFactory.Infrastructure;

public sealed class DarktableCliAdapter(string darktableCliPath, ProcessRunner runner) : IDarktableCli
{
    private readonly string _path = darktableCliPath;
    private readonly ProcessRunner _runner = runner;

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_path, ["--version"], TimeSpan.FromSeconds(15), cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"darktable-cli --version failed: {result.StdErr}");
        return string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr.Trim() : result.StdOut.Trim();
    }

    public Task<ProcessExecutionResult> ExportAsync(DarktableExportRequest r, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(r.InputPath)) throw new FileNotFoundException("Darktable input not found", r.InputPath);
        if (r.XmpPath is not null && !File.Exists(r.XmpPath)) throw new FileNotFoundException("XMP not found", r.XmpPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(r.OutputPath))!);
        var args = new List<string> { r.InputPath };
        if (!string.IsNullOrWhiteSpace(r.XmpPath)) args.Add(r.XmpPath!);
        args.Add(r.OutputPath);
        args.Add("--hq"); args.Add(r.HighQuality ? "true" : "false");
        if (r.MaxWidth is not null) { args.Add("--width"); args.Add(r.MaxWidth.Value.ToString()); }
        if (r.MaxHeight is not null) { args.Add("--height"); args.Add(r.MaxHeight.Value.ToString()); }
        if (!string.IsNullOrWhiteSpace(r.Style)) { args.Add("--style"); args.Add(r.Style!); }
        args.Add("--verbose");
        return _runner.RunAsync(_path, args, TimeSpan.FromMinutes(3), cancellationToken);
    }
}
