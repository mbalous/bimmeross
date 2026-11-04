namespace BmwCoder.Core.Models;

public sealed class ScanHit
{
    public ushort Did { get; init; }
    public string Status { get; init; } = "timeout";
    public int? Length { get; init; }
    public byte? Nrc { get; init; }
}

public sealed class EcuSnapshot
{
    public byte Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Responds { get; set; }
    public int? RttMs { get; set; }
    public bool IsWritable { get; init; }
    public string? Vin { get; set; }
}
