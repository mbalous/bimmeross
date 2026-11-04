using System.Security.Cryptography;
using System.Text.Json;
using BmwCoder.Core.Models;

namespace BmwCoder.Core.Workspace;

public sealed record BackupInspection(BackupManifest Manifest, string[] Problems)
{
    public bool IsValid => Problems.Length == 0;
}

public static class BackupInspector
{
    public static BackupInspection Inspect(string directory, CancellationToken cancellationToken = default)
    {
        string manifestPath = Path.Combine(directory, "manifest.json");
        var fileInfo = new FileInfo(manifestPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Backup manifest was not found.", manifestPath);
        if (fileInfo.Length > 4 * 1024 * 1024)
            throw new FormatException("Backup manifest exceeds the 4 MiB limit.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new FormatException("Backup manifest is empty.");
        if (manifest.Entries == null)
            throw new FormatException("Backup entries are missing.");
        var problems = new List<string>();
        var identities = new HashSet<(byte, ushort)>();
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry == null)
            {
                problems.Add("Null backup entry.");
                continue;
            }
            if (!identities.Add((entry.EcuAddress, entry.Did)))
                problems.Add($"Duplicate snapshot: {entry.EcuAddress:X2}/{entry.Did:X4}.");
            if (string.IsNullOrWhiteSpace(entry.FileName) || entry.FileName != Path.GetFileName(entry.FileName) ||
                entry.FileName.Contains(':') || entry.FileName.Contains('\\') || entry.FileName.Contains('/'))
            {
                problems.Add("Invalid snapshot filename; must be a filename inside the backup directory.");
                continue;
            }
            string path = Path.Combine(directory, entry.FileName);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                problems.Add($"Missing snapshot: {entry.FileName}.");
                continue;
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                problems.Add($"Linked snapshot is not accepted: {entry.FileName}.");
                continue;
            }
            if (info.Length != entry.SizeBytes)
                problems.Add($"Size mismatch: {entry.FileName}.");
            using var stream = File.OpenRead(path);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.AppendData(buffer.AsSpan(0, read));
            }
            string hash = Convert.ToHexString(hasher.GetHashAndReset());
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"SHA-256 mismatch: {entry.FileName}.");
        }
        if (manifest.Entries.Count == 0)
            problems.Add("Backup contains no snapshots.");
        return new BackupInspection(manifest, problems.ToArray());
    }
}
