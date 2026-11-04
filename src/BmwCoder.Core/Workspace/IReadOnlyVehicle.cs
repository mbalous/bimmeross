using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;

namespace BmwCoder.Core.Workspace;

public interface IReadOnlyVehicle : IDisposable
{
    bool IsDemo { get; }
    bool IsConnected { get; }
    string? Vin { get; }
    IReadOnlyList<string> Warnings { get; }
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
    Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken cancellationToken);
    Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken cancellationToken);
    Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken cancellationToken);
    void Disconnect();
}
