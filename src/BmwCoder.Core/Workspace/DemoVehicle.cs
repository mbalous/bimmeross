using System.Text;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;

namespace BmwCoder.Core.Workspace;

public sealed class DemoVehicle : IReadOnlyVehicle
{
    public bool IsDemo => true;
    public bool IsConnected { get; private set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public string BackupDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BmwCoder", "backups");
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

    public Task<DiagnosticReading> ProbeAsync(byte ecu, CancellationToken cancellationToken)
    {
        RequireConnection(cancellationToken);
        if (ecu is not (0x10 or 0x63))
            throw new NotSupportedException("The synthetic demo has no ECU at this address.");
        var reading = new DiagnosticReading { Ecu = ecu, Name = "TesterPresent", RequestHex = "3E00", ResponseHex = "7E00" };
        reading.Warnings.Add("Synthetic demo data, not a vehicle response.");
        return Task.FromResult(reading);
    }

    public Task<DiagnosticReading> ReadRequestAsync(byte ecu, byte[] request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireConnection(cancellationToken);
        if (request.Length == 3 && request[0] == 0x22)
            return ReadDidAsync(ecu, (ushort)((request[1] << 8) | request[2]), cancellationToken);
        if (request.Length == 3 && request[0] == 0x19 && request[1] == 0x02)
        {
            var reading = new DiagnosticReading
            {
                Ecu = ecu, Name = "Demo fault memory", RequestHex = Convert.ToHexString(request),
                ResponseHex = "5902FF", PayloadHex = "FF"
            };
            reading.Warnings.Add("Synthetic demo: no fault records.");
            return Task.FromResult(reading);
        }
        if (request.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x00 }))
            return ProbeAsync(ecu, cancellationToken);
        throw new ArgumentException("Read-only diagnostics allow only a single DID read (22), DTC status read (19 02 <mask>), or TesterPresent (3E 00).", nameof(request));
    }

    public Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(IProgress<DiagnosticReading>? progress, CancellationToken cancellationToken)
    {
        RequireConnection(cancellationToken);
        throw new NotSupportedException("The synthetic demo does not model a DME report.");
    }

    public Task<IReadOnlyList<DiagnosticDefinition>> LoadDiagnosticDataAsync(
        string path, string series, byte ecu, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("ODX diagnostic data is not loaded in synthetic demo mode.");
    }

    public void ConfigureFaultTexts(string path) =>
        throw new NotSupportedException("ISTA fault texts are not loaded in synthetic demo mode.");

    public Task<ReadOnlyBackup> BackupSuggestedAsync(IReadOnlyList<EcuInfo> ecus, IProgress<ReadOnlyBackup>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Synthetic demo mode does not create vehicle backups.");
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
