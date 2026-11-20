using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Transport;
using System.Security.Cryptography;
using System.Text.Json;

namespace BmwCoder.Core.Workspace;

public sealed class ReadOnlyVehicle : IReadOnlyVehicle
{
    private readonly CodingSession _session;
    public bool IsDemo => false;
    public bool IsConnected => _session.Connection.IsConnected;
    public string BackupDirectory => _session.BackupManager.BaseDirectory;
    public TimeSpan RequestTimeout
    {
        get => _session.Connection.Timeout;
        set
        {
            if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromSeconds(30))
                throw new ArgumentOutOfRangeException(nameof(value), "Request timeout must be between 100 and 30000 milliseconds.");
            _session.Connection.Timeout = value;
        }
    }
    public string? Vin { get; private set; }
    public IReadOnlyList<string> Warnings => _session.DiscoveryWarnings;

    public ReadOnlyVehicle() : this(new CodingSession()) { }

    public ReadOnlyVehicle(CodingSession session) => _session = session;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A host is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        Disconnect();
        try
        {
            await _session.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var reading = await _session.ReadDiagnosticDidAsync(CodingSession.GatewayAddress,
                CodingSession.VinDid, cancellationToken).ConfigureAwait(false);
            if (reading.Status != "ok")
                throw new HsfzException($"VIN read failed: {reading.Error}");
            string vin = System.Text.Encoding.ASCII.GetString(Convert.FromHexString(reading.PayloadHex)).Trim('\0', ' ');
            if (vin.Length != 17 || vin.Any(c => !char.IsAsciiLetterOrDigit(c) || "IOQioq".Contains(c)))
                throw new FormatException("Gateway did not return a valid 17-character VIN. Connection closed.");
            Vin = vin;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public async Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        RequireConnection();
        var ecus = await _session.DiscoverEcusAsync(cancellationToken: cancellationToken,
            probeTimeout: TimeSpan.FromSeconds(1), discoveryBudget: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return ecus.Select(ecu => new EcuInfo
        {
            Address = ecu.Address, Name = ecu.Name, Description = ecu.Description,
            Responds = ecu.Responds, RttMs = ecu.RttMs, DiscoverySource = ecu.DiscoverySource,
            NameSource = ecu.NameSource, ProbeError = ecu.ProbeError,
            SuggestedDids = ecu.SuggestedDids.ToArray(), IsWritable = false
        }).ToArray();
    }

    public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ReadDiagnosticDidAsync(ecu, did, cancellationToken);
    }

    public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ReadFaultMemoryAsync(ecu, cancellationToken);
    }

    public Task<DiagnosticReading> ProbeAsync(byte ecu, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ProbeDiagnosticEcuAsync(ecu, cancellationToken);
    }

    public Task<DiagnosticReading> ReadRequestAsync(byte ecu, byte[] request, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ReadOnlyRequestAsync(ecu, request, cancellationToken);
    }

    public Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(IProgress<DiagnosticReading>? progress, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ReadDmeReportAsync(progress, cancellationToken, stopOnDisconnect: true);
    }

    public async Task<IReadOnlyList<DiagnosticDefinition>> LoadDiagnosticDataAsync(
        string path, string series, byte ecu, CancellationToken cancellationToken)
    {
        var result = await Task.Run(() =>
        {
            var data = PsdzDiagnosticData.Load(path, series);
            return (Data: data, Definitions: data.GetDids(ecu));
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _session.DiagnosticData = result.Data;
        return result.Definitions;
    }

    public void ConfigureFaultTexts(string path) => _session.FaultDescriptions = new IstaFaultLookup(path);

    public async Task<ReadOnlyBackup> BackupSuggestedAsync(IReadOnlyList<EcuInfo> ecus, IProgress<ReadOnlyBackup>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ecus);
        RequireConnection();
        var targets = ecus.Where(e => e.Responds)
            .SelectMany(e => e.SuggestedDids.Select(did => (Ecu: e, Did: did)))
            .DistinctBy(t => (t.Ecu.Address, t.Did)).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("No responding ECUs with suggested DIDs are available. Discover ECUs first.");

        string vin = Vin!;
        string? directory = null;
        var manifest = new BackupManifest { Vin = vin, Timestamp = DateTime.UtcNow };
        var failures = new List<string>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireConnection();
            DiagnosticReading reading;
            try
            {
                reading = await _session.ReadDiagnosticDidAsync(target.Ecu.Address, target.Did, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or HsfzException)
            {
                failures.Add($"ECU 0x{target.Ecu.Address:X2} DID 0x{target.Did:X4}: {ex.Message} Backup stopped.");
                progress?.Report(new ReadOnlyBackup(directory, manifest.Entries.Count, failures.ToArray()));
                break;
            }
            if (reading.Status != "ok")
            {
                failures.Add($"ECU 0x{target.Ecu.Address:X2} DID 0x{target.Did:X4}: {reading.Error ?? reading.Status}.");
                progress?.Report(new ReadOnlyBackup(directory, manifest.Entries.Count, failures.ToArray()));
                continue;
            }

            byte[] bytes = Convert.FromHexString(reading.PayloadHex);
            directory ??= CreateBackupDirectory(vin);
            string filename = $"{target.Ecu.Address:X2}_{target.Did:X4}.bin";
            await using (var stream = new FileStream(Path.Combine(directory, filename), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            manifest.Entries.Add(new BackupEntry
            {
                EcuAddress = target.Ecu.Address,
                EcuName = target.Ecu.Name,
                Did = target.Did,
                FileName = filename,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                SizeBytes = bytes.Length
            });
            string temporary = Path.Combine(directory, $"manifest.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, Path.Combine(directory, "manifest.json"), overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            progress?.Report(new ReadOnlyBackup(directory, manifest.Entries.Count, failures.ToArray()));
        }
        return new ReadOnlyBackup(directory, manifest.Entries.Count, failures);
    }

    private string CreateBackupDirectory(string vin)
    {
        string directory = Path.Combine(_session.BackupManager.BaseDirectory, vin, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        if (Directory.Exists(directory))
            throw new IOException("The new backup directory already exists.");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Disconnect()
    {
        _session.Close();
        Vin = null;
    }

    public void Dispose()
    {
        Disconnect();
        _session.Dispose();
    }

    private void RequireConnection()
    {
        if (!IsConnected || Vin == null)
            throw new InvalidOperationException("Connect and verify vehicle identity first.");
    }
}
