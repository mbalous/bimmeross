using System;
using System.Collections.Generic;

namespace BmwCoder.Core.Models;

public sealed class BackupEntry
{
    public byte EcuAddress { get; set; }
    public string EcuName { get; set; } = string.Empty;
    public ushort Did { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int SizeBytes { get; set; }
}

public sealed class BackupManifest
{
    public string Vin { get; set; } = "UNKNOWN";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = "1.0.0";
    public List<BackupEntry> Entries { get; set; } = new();
}
