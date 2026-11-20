using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;

namespace BmwCoder.Core.Workspace;

public interface IReadOnlyVehicle : IDisposable
{
    bool IsDemo { get; }
    bool IsConnected { get; }
    TimeSpan RequestTimeout { get; set; }
    string BackupDirectory { get; }
    string? Vin { get; }
    IReadOnlyList<string> Warnings { get; }
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
    Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken cancellationToken);
    Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken cancellationToken);
    Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken cancellationToken);
    Task<DiagnosticReading> ReadRequestAsync(byte ecu, byte[] request, CancellationToken cancellationToken);
    Task<DiagnosticReading> ProbeAsync(byte ecu, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(IProgress<DiagnosticReading>? progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiagnosticDefinition>> LoadDiagnosticDataAsync(string path, string series, byte ecu, CancellationToken cancellationToken);
    void ConfigureFaultTexts(string path);
    Task<ReadOnlyBackup> BackupSuggestedAsync(IReadOnlyList<EcuInfo> ecus, IProgress<ReadOnlyBackup>? progress, CancellationToken cancellationToken);
    void Disconnect();
}
