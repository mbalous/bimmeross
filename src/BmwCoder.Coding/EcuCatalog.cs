using System;
using System.Collections.Generic;

namespace BmwCoder.Coding;

public sealed record EcuCatalogEntry(
    byte Address,
    string Name,
    string Description,
    ushort[] SuggestedDids,
    bool IsWritable = true,
    bool RequiresRoutineControl = false,
    ushort RoutineId = 0x0200,
    bool RequiresReset = false
);

public static class EcuCatalog
{
    private static readonly List<EcuCatalogEntry> _entries = new()
    {
        new EcuCatalogEntry(0x10, "ZGW", "Central Gateway Module", new ushort[] { 0xF190 }, IsWritable: false),
        new EcuCatalogEntry(0x40, "CAS / FEM", "Car Access System / Front Electronic Module", new ushort[] { 0x3000 }, IsWritable: true, RequiresReset: true),
        new EcuCatalogEntry(0x60, "KOMBI", "Instrument Cluster", new ushort[] { 0x3000 }, IsWritable: true, RequiresReset: true),
        new EcuCatalogEntry(0x63, "HU_NBT", "Head Unit (NBT / EntryNav)", new ushort[] { 0x3000 }, IsWritable: true, RequiresReset: true),
        new EcuCatalogEntry(0x64, "HU_CIC", "Head Unit (CIC - Legacy)", new ushort[] { 0x3000 }, IsWritable: true, RequiresReset: true),
        new EcuCatalogEntry(0x12, "DME / DDE", "Engine Electronics", Array.Empty<ushort>(), IsWritable: false), // Strictly locked
        new EcuCatalogEntry(0x72, "REM", "Rear Electronic Module", Array.Empty<ushort>(), IsWritable: false),
        new EcuCatalogEntry(0x30, "EPS", "Electric Power Steering", Array.Empty<ushort>(), IsWritable: false)
    };

    public static IReadOnlyList<EcuCatalogEntry> Entries => _entries;

    public static EcuCatalogEntry? FindByAddress(byte address)
    {
        return _entries.Find(e => e.Address == address);
    }
}
