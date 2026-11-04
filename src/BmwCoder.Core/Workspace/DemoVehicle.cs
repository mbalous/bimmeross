using System.Text;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;

namespace BmwCoder.Core.Workspace;

public sealed class DemoVehicle : IReadOnlyVehicle
{
    public bool IsDemo => true;
    public bool IsConnected { get; private set; }
    public string? Vin => IsConnected ? "DEMO0000000000001" : null;
    public IReadOnlyList<string> Warnings { get; } = new[] { "Synthetic demo data only; no network connection is opened." };

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        RequireConnection(cancellationToken);
        IReadOnlyList<EcuInfo> ecus = new[]
        {
            new EcuInfo { Address = 0x10, Name = "DEMO Gateway", Responds = true, DiscoverySource = "Synthetic demo", NameSource = "Synthetic demo", SuggestedDids = new ushort[] { 0xF190 } },
            new EcuInfo { Address = 0x63, Name = "DEMO Head unit", Responds = true, DiscoverySource = "Synthetic demo", NameSource = "Synthetic demo", SuggestedDids = new ushort[] { 0xF18C } }
        };
        return Task.FromResult(ecus);
    }

    public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken cancellationToken)
    {
        RequireConnection(cancellationToken);
        byte[] payload = did switch
        {
            0xF190 when ecu == 0x10 => Encoding.ASCII.GetBytes(Vin!),
            0xF18C when ecu == 0x63 => Encoding.ASCII.GetBytes("DEMO-SERIAL"),
            _ => throw new NotSupportedException("Demo supports gateway VIN F190 and head-unit serial F18C only.")
        };
        var reading = DiagnosticDecoder.Decode(ecu, did, payload);
        reading.Warnings.Add("Synthetic demo data, not a vehicle response.");
        return Task.FromResult(reading);
    }

    public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken cancellationToken)
    {
        RequireConnection(cancellationToken);
        var reading = new DiagnosticReading { Ecu = ecu, Name = "Demo fault memory", RequestHex = "1902AF", ResponseHex = "5902FF", PayloadHex = "FF" };
        reading.Warnings.Add("Synthetic demo: no fault records.");
        return Task.FromResult(reading);
    }

    public void Disconnect() => IsConnected = false;
    public void Dispose() => Disconnect();

    private void RequireConnection(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
            throw new InvalidOperationException("Connect the demo first.");
    }
}
