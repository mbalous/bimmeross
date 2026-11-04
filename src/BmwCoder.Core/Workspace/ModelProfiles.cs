using System.Text.Json;

namespace BmwCoder.Core.Workspace;

public sealed record ModelProfile(string Name, string Label, IReadOnlySet<int> Criteria, int? Build, string? Note)
{
    public CriteriaContext Context => new(Criteria, Build);
}

/// <summary>Vehicle model profiles: which CAF selection criteria each model sets. Loaded from models_f8x.json.</summary>
public sealed class ModelProfiles
{
    public string Series { get; }
    public string Source { get; }
    public IReadOnlyDictionary<string, ModelProfile> Models { get; }
    public IReadOnlyDictionary<string, string> CriteriaNames { get; }

    private ModelProfiles(string series, string source, IReadOnlyDictionary<string, ModelProfile> models,
        IReadOnlyDictionary<string, string> criteriaNames)
    {
        Series = series;
        Source = source;
        Models = models;
        CriteriaNames = criteriaNames;
    }

    public static ModelProfiles? Load(string directory, string fileName = "models_f8x.json")
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var models = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in root.GetProperty("models").EnumerateObject())
        {
            var e = m.Value;
            models[m.Name] = new ModelProfile(m.Name,
                e.TryGetProperty("label", out var l) ? l.GetString() ?? m.Name : m.Name,
                e.GetProperty("criteria").EnumerateArray().Select(c => c.GetInt32()).ToHashSet(),
                e.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null,
                e.TryGetProperty("note", out var n) ? n.GetString() : null);
        }
        var names = new Dictionary<string, string>();
        if (root.TryGetProperty("criteria", out var c2))
            foreach (var c in c2.EnumerateObject())
                names[c.Name] = c.Value.GetProperty("meaning").GetString() ?? string.Empty;
        return new ModelProfiles(root.GetProperty("series").GetString() ?? "F030",
            root.TryGetProperty("source", out var s) ? s.GetString() ?? string.Empty : string.Empty, models, names);
    }
}
