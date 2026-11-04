using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BmwCoder.Core.Workspace;

public sealed record ParameterAnnotation(string Label, string? Meaning, string? Note, bool VersionVerified);

/// <summary>
/// Optional, user-maintained names for whole-byte coding parameters. PSdZ CAFs have blanked names,
/// so these files supply labels by CAF id, block address and byte offset. Display-only; never used for writes.
/// </summary>
public sealed class CodingAnnotations
{
    private readonly Dictionary<(int Block, int Start, int End, string Mask), (string Name, Dictionary<string, string> Values, string? Note)> _entries = new();
    private readonly HashSet<string> _verified = new(StringComparer.OrdinalIgnoreCase);

    public string Source { get; private set; } = "";
    public IReadOnlyDictionary<int, string> BlockNames => _blockNames;
    private readonly Dictionary<int, string> _blockNames = new();

    public static CodingAnnotations? Load(string directory, string cafFile)
    {
        int dot = cafFile.IndexOf(".caf.", StringComparison.OrdinalIgnoreCase);
        if (dot < 0 || !Directory.Exists(directory))
            return null;
        string cafId = cafFile[..dot].ToLowerInvariant();
        string path = Path.Combine(directory, cafId + ".json");
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("caf").GetString(), cafId, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Annotation file does not match its CAF id.");
        var result = new CodingAnnotations { Source = path };
        if (root.TryGetProperty("verifiedVersions", out var versions))
            foreach (var v in versions.EnumerateArray())
                result._verified.Add(v.GetString() ?? "");
        if (root.TryGetProperty("blocks", out var blockList))
            foreach (var b in blockList.EnumerateArray())
                result._blockNames[int.Parse(b.GetProperty("block").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] =
                    b.GetProperty("name").GetString() ?? "";
        if (!root.TryGetProperty("parameters", out var parameterList))
            return result;
        foreach (var p in parameterList.EnumerateArray())
        {
            int block = int.Parse(p.GetProperty("block").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (p.TryGetProperty("values", out var map))
                foreach (var kv in map.EnumerateObject())
                    values[kv.Name] = kv.Value.GetString() ?? "";
            int start = p.GetProperty("byte").GetInt32();
            int end = p.TryGetProperty("end", out var e) ? e.GetInt32() : start;
            string mask = p.TryGetProperty("mask", out var m) ? m.GetString()! : "11111111b";
            result._entries[(block, start, end, mask.ToLowerInvariant())] =
                (p.GetProperty("name").GetString() ?? "", values, p.TryGetProperty("note", out var n) ? n.GetString() : null);
        }
        return result;
    }

    public (string Name, string? Meaning)? Describe(int block, int start, int end, string mask, string value)
    {
        if (!_entries.TryGetValue((block, start, end, mask.ToLowerInvariant()), out var entry))
            return null;
        var units = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string key = string.Join(",", units.Select(u => u[(u.IndexOf(':') + 1)..]));
        return (entry.Name, entry.Values.TryGetValue(key, out var label) ? label : null);
    }

    public ParameterAnnotation? Find(string cafFile, CodingParameter parameter)
    {
        if (!int.TryParse(parameter.Start, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
            !int.TryParse(parameter.End, NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) ||
            !_entries.TryGetValue((parameter.BlockAddress, start, end, parameter.Mask.ToLowerInvariant()), out var entry))
            return null;
        string version = cafFile[(cafFile.IndexOf(".caf.", StringComparison.OrdinalIgnoreCase) + 5)..];
        string? meaning = null;
        if (parameter.Value is { } value)
        {
            // Value looks like "0:0B 1:75"; the lookup key is the unit values joined by commas ("0B,75").
            var units = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string key = string.Join(",", units.Select(u => u[(u.IndexOf(':') + 1)..]));
            if (units.All(u => u.Contains(':')) && entry.Values.TryGetValue(key, out var label))
                meaning = label;
        }
        return new ParameterAnnotation(entry.Name, meaning, entry.Note, _verified.Contains(version));
    }
}