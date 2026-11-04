using System.Collections.Concurrent;

namespace BmwCoder.Coding.Checksum;

public sealed class ChecksumDefinition
{
    public IChecksum Checksum { get; }
    public ChecksumRange Range { get; }

    public ChecksumDefinition(IChecksum checksum, ChecksumRange range)
    {
        Checksum = checksum;
        Range = range;
    }
}

/// <summary>
/// Registry mapping ECU address and DID to verified checksum algorithms and ranges.
/// If an ECU/DID has no entry in the registry, writes are blocked by design.
/// </summary>
public sealed class ChecksumRegistry
{
    private static readonly Lazy<ChecksumRegistry> _instance = new(() => new ChecksumRegistry());
    public static ChecksumRegistry Default => _instance.Value;

    private readonly ConcurrentDictionary<(byte Ecu, ushort Did), ChecksumDefinition> _definitions = new();

    public ChecksumRegistry()
    {
        RegisterDefaults();
    }

    private void RegisterDefaults()
    {
        // HU_NBT (0x63), DID 0x3000: Standard 16-bit CRC over header and data
        // Example standard range: Data start 0, length 62 bytes, CRC at offset 62 (2 bytes)
        Register(0x63, 0x3000, new Crc16Ccitt(0xFFFF), new ChecksumRange(0, 62, 62, 2));

        // KOMBI (0x60), DID 0x3000: Standard CRC-16
        Register(0x60, 0x3000, new Crc16Ccitt(0x0000), new ChecksumRange(0, 62, 62, 2));

        // CAS / FEM (0x40), DID 0x3000: XOR checksum over 31 bytes, stored at byte 31
        Register(0x40, 0x3000, new XorChecksum(), new ChecksumRange(0, 31, 31, 1));
    }

    public void Register(byte ecu, ushort did, IChecksum checksum, ChecksumRange range)
    {
        _definitions[(ecu, did)] = new ChecksumDefinition(checksum, range);
    }

    public ChecksumDefinition? GetDefinition(byte ecu, ushort did)
    {
        return _definitions.TryGetValue((ecu, did), out var def) ? def : null;
    }

    public bool HasChecksum(byte ecu, ushort did)
    {
        return _definitions.ContainsKey((ecu, did));
    }

    public bool Verify(CodingBlock block)
    {
        var def = GetDefinition(block.EcuAddress, block.Did);
        if (def == null) return false;

        return def.Checksum.Verify(block.RawData, def.Range);
    }

    public void Apply(CodingBlock block)
    {
        var def = GetDefinition(block.EcuAddress, block.Did);
        if (def == null)
        {
            throw new System.InvalidOperationException($"No checksum algorithm registered for ECU 0x{block.EcuAddress:X2}, DID 0x{block.Did:X4}.");
        }

        def.Checksum.Apply(block.RawData, def.Range);
    }
}
