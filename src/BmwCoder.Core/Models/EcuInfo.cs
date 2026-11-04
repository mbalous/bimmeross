using System;

namespace BmwCoder.Core.Models;

public sealed class EcuInfo
{
    public byte Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Responds { get; set; }
    public int? RttMs { get; set; }
    public string DiscoverySource { get; init; } = string.Empty;
    public string NameSource { get; init; } = string.Empty;
    public string? ProbeError { get; set; }
    public bool IsWritable { get; init; }
    public ushort[] SuggestedDids { get; init; } = Array.Empty<ushort>();

    public override string ToString() => $"[0x{Address:X2}] {Name} - {(Responds ? "Online" : "Offline")}";
}
