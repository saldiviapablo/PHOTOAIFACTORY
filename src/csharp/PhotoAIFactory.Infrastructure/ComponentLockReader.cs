using System.Text.Json;

namespace PhotoAIFactory.Infrastructure;

public sealed record LocalComponent(
    string Id,
    string? Version,
    string? Source,
    string? LocalPath,
    string? Sha256,
    string? License,
    bool Installed,
    string? Status,
    string? Notes);

public sealed class ComponentLockReader
{
    public IReadOnlyDictionary<string, LocalComponent> Read(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, LocalComponent>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dict = new Dictionary<string, LocalComponent>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("components", out var components)) return dict;
        foreach (var c in components.EnumerateArray())
        {
            string? S(string name) => c.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
            bool B(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            var id = S("id");
            if (id is null) continue;
            dict[id] = new LocalComponent(id, S("version"), S("source"), S("local_path"), S("sha256"), S("license"), B("installed"), S("status"), S("notes"));
        }
        return dict;
    }
}
