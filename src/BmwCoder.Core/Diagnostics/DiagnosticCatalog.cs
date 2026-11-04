namespace BmwCoder.Core.Diagnostics;

public static class DiagnosticCatalog
{
    public static IReadOnlyList<DiagnosticDefinition> DmeReads { get; } = Array.AsReadOnly(new[]
    {
        new DiagnosticDefinition(0xF190, "VIN"),
        new DiagnosticDefinition(0xF18C, "ECU serial number"),
        new DiagnosticDefinition(0xF18B, "Manufacturing date"),
        new DiagnosticDefinition(0x4AB4, "Operating-time counter"),
        new DiagnosticDefinition(0x2502, "Programming counter"),
        new DiagnosticDefinition(0x2503, "Programming counter maximum"),
        new DiagnosticDefinition(0x2541, "Calibration ID / CVN"),
        new DiagnosticDefinition(0xF101, "Current software inventory (SVK)"),
        new DiagnosticDefinition(0xF150, "SGBD index"),
        new DiagnosticDefinition(0xF186, "Active diagnostic session")
    });

    public static IReadOnlyList<DiagnosticDefinition> LoadPsdzDids(string root, string series = "F020", byte ecu = 0x12) =>
        PsdzDiagnosticData.Load(root, series).GetDids(ecu);
}
