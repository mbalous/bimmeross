using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BmwCoder.Coding;
using BmwCoder.Core.Models;

namespace BmwCoder.Core;

public sealed class BackupManager
{
    public string BaseDirectory { get; }

    public BackupManager(string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            BaseDirectory = Path.Combine(appData, "BmwCoder", "backups");
        }
        else
        {
            BaseDirectory = baseDirectory;
        }

        Directory.CreateDirectory(BaseDirectory);
    }

    public string GetSessionBackupDirectory(string vin, DateTime timestamp)
    {
        string safeVin = string.IsNullOrWhiteSpace(vin) ? "UNKNOWN" : vin.Trim();
        string dirName = timestamp.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine(BaseDirectory, safeVin, dirName);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<string> SaveBlockSnapshotAsync(string sessionDir, CodingBlock block, string ecuName, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(sessionDir);

        string fileName = $"{block.EcuAddress:X2}_{block.Did:X4}.bin";
        string filePath = Path.Combine(sessionDir, fileName);

        // 1. Write raw binary data
        await File.WriteAllBytesAsync(filePath, block.RawData, cancellationToken).ConfigureAwait(false);

        // 2. Read or initialize manifest
        string manifestPath = Path.Combine(sessionDir, "manifest.json");
        BackupManifest manifest;

        if (File.Exists(manifestPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize<BackupManifest>(json) ?? new BackupManifest();
            }
            catch
            {
                manifest = new BackupManifest();
            }
        }
        else
        {
            manifest = new BackupManifest
            {
                Vin = block.Vin ?? "UNKNOWN",
                Timestamp = block.ReadAt
            };
        }

        // Update or add entry
        manifest.Entries.RemoveAll(e => e.EcuAddress == block.EcuAddress && e.Did == block.Did);
        manifest.Entries.Add(new BackupEntry
        {
            EcuAddress = block.EcuAddress,
            EcuName = ecuName,
            Did = block.Did,
            FileName = fileName,
            Sha256 = block.ComputeSha256(),
            SizeBytes = block.RawData.Length
        });

        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken).ConfigureAwait(false);

        return filePath;
    }

    public BackupManifest? LoadManifest(string backupDirectory)
    {
        string manifestPath = Path.Combine(backupDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return null;

        string json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<BackupManifest>(json);
    }

    public CodingBlock? LoadBlock(string backupDirectory, byte ecuAddress, ushort did)
    {
        string fileName = $"{ecuAddress:X2}_{did:X4}.bin";
        string filePath = Path.Combine(backupDirectory, fileName);

        if (!File.Exists(filePath)) return null;

        byte[] raw = File.ReadAllBytes(filePath);
        var manifest = LoadManifest(backupDirectory);

        return new CodingBlock(ecuAddress, did, raw, manifest?.Vin);
    }
}
