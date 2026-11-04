using System.Text;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using BmwCoder.Transport;

namespace BmwCoder.Tui;

public sealed class Workbench : IAsyncDisposable
{
    private readonly IReadOnlyVehicle _vehicle;
    private readonly ICafReader? _cafReader;
    private readonly ICodingReader? _codingReader;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Queue<string> _logs = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    public event Action? Changed;
    public AppSettings Settings { get; }
    public bool IsDemo => _vehicle.IsDemo;
    public bool IsConnected => _vehicle.IsConnected;
    public string? Vin => _vehicle.Vin;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "Disconnected - read-only preview";
    public string Details { get; private set; } = PreviewCapabilities.CodingUnavailable;
    public IReadOnlyList<EcuInfo> Ecus { get; private set; } = Array.Empty<EcuInfo>();
    public FaXmlDocument? Fa { get; private set; }
    public CodingFile? Coding { get; private set; }
    public CafDefinition? Caf { get; private set; }
    public DecodedCoding? Decoded { get; private set; }
    public string DecodedSummary => Decoded == null
        ? "Select a discovered ECU, then Coding > Read coding / decode. Or open a trusted NCD with its exact CAF filename."
        : $"{Decoded.BaseVariant} {Decoded.CafFile}\n" +
          $"Current values: {Decoded.Parameters.Count(p => p.IsDecoded)} / {Decoded.Parameters.Count} fields\n" +
          $"Available names: {Decoded.Parameters.Count(p => p.Name != null)}; missing/trimmed names remain unknown.\n" +
          $"Opaque blocks: {string.Join(", ", Decoded.OpaqueBlocks.Select(a => a.ToString("X4")))}\n" +
          $"Original NCD SHA-256: {Decoded.Sha256}\n\n" +
          "Read-only snapshot. Values are from byte-verified SDK-decoded blocks, not CAF defaults. " +
          "Fields without current values are explicitly unavailable. Original NCD/opaque bytes are preserved; no edits or writes.\n\n" +
          (Decoded.Diagnostics.Length == 0 ? "" : "Runtime diagnostics:\n" + Decoded.Diagnostics);
    public string CafSummary => Caf == null
        ? "Import a trusted local CAF definition using Coding > Read CAF definition. Configure ISTA and 64-bit Java 8 in Settings first."
        : $"{Caf.Name}\nReader: {Caf.RuntimeVersion}\nDefinition fields: {Caf.Fields.Count}\n\n" +
          "Read-only XML definitions. No NCD values, ECU matching or writable FDL bit mapping is established.\n\n" +
          (Caf.Diagnostics.Length == 0 ? "" : "Runtime diagnostics:\n" + Caf.Diagnostics);
    public string CodingSummary => Coding == null ? PreviewCapabilities.CodingUnavailable :
        $"{Coding.Name}\nBytes: {Coding.Length}\nSHA-256: {Coding.Sha256}\n\n" +
        PreviewCapabilities.CodingUnavailable + "\n\n" + Coding.HexPreview();
    public IReadOnlyList<string> Logs
    {
        get { lock (_stateLock) return _logs.ToArray(); }
    }
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public Workbench(IReadOnlyVehicle vehicle, AppSettings settings, ICafReader? cafReader = null, ICodingReader? codingReader = null)
    {
        _vehicle = vehicle;
        Settings = settings;
        _cafReader = cafReader;
        _codingReader = codingReader;
        Log(IsDemo ? "DEMO mode: synthetic data, no sockets." : "LIVE read-only mode: connect explicitly. No writes are exposed.");
    }

    public Task ConnectAsync() => ExecuteAsync("Connect", async token =>
    {
        Ecus = Array.Empty<EcuInfo>();
        await _vehicle.ConnectAsync(Settings.Host, Settings.Port, token).ConfigureAwait(false);
        return $"{(IsDemo ? "DEMO " : "")}VIN: {Vin}\nConnected in read-only mode. Use Discover to list ECUs.";
    });

    public Task DiscoverAsync() => ExecuteAsync("Discover", async token =>
    {
        Ecus = await _vehicle.DiscoverAsync(token).ConfigureAwait(false);
        return $"Found {Ecus.Count} ECU candidates. Catalog names are not verified variants.\n" +
            string.Join("\n", _vehicle.Warnings);
    });

    public Task FindGatewayAsync(EnetInterface network) => ExecuteAsync("Find gateway", async token =>
    {
        if (IsDemo)
            return "Demo mode does not send discovery packets. Use --live for explicit ENET discovery.";
        if (IsConnected)
            throw new InvalidOperationException("Disconnect before selecting a different vehicle gateway.");
        var result = await new EnetDiscovery().DiscoverAsync(network.Address, network.Broadcast,
            TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        string warnings = string.Join("\n", result.Warnings);
        if (result.Gateways.Length == 1)
        {
            Settings.Host = result.Gateways[0].Address.ToString();
            return $"Gateway found: {Settings.Host}. Host selected for the next explicit connection (F2).\n{warnings}";
        }
        if (result.Gateways.Length == 0)
            return $"No gateway responded on {network}. Verify cable, adapter and ignition.\n{warnings}";
        return "Multiple gateways responded; select a host explicitly in Connection settings:\n" +
            string.Join("\n", result.Gateways.Select(g => g.Address.ToString())) + "\n" + warnings;
    });

    public Task ReadDidAsync(byte ecu, ushort did) => ExecuteAsync("Read DID", async token =>
        Format(await _vehicle.ReadDidAsync(ecu, did, token).ConfigureAwait(false)));

    public Task ReadFaultsAsync(byte ecu) => ExecuteAsync("Read faults", async token =>
        Format(await _vehicle.ReadFaultsAsync(ecu, token).ConfigureAwait(false)));

    public Task DisconnectAsync() => ExecuteAsync("Disconnect", _ =>
    {
        _vehicle.Disconnect();
        Ecus = Array.Empty<EcuInfo>();
        return Task.FromResult("Disconnected. Offline documents are preserved.");
    });

    public Task InspectInstallationsAsync() => ExecuteAsync("Inspect installations", token =>
        Task.Run(() => LocalInstallation.Inspect(Settings.PsdzDataPath, Settings.EsysPath, Settings.IstaPath, token).ToString(), token));

    public Task ImportFaAsync(string path) => ExecuteAsync("Import FA", token => Task.Run(() =>
    {
        Fa = FaXmlDocument.Load(path);
        return FaSummary();
    }, token));

    public Task LoadDemoFaAsync() => ExecuteAsync("Load demo FA", _ =>
    {
        if (!IsDemo)
            throw new InvalidOperationException("Synthetic FA is available only in demo mode.");
        Fa = FaXmlDocument.Parse(Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><fa source=\"synthetic-demo\"><type key=\"DEMO\"/><options><sa>000</sa></options><unknown keep=\"yes\"/></fa>"));
        return Task.FromResult(FaSummary());
    });

    public Task EditFaAsync(int field, string value) => ExecuteAsync("Edit FA offline", _ =>
    {
        RequireFa().SetValue(field, value);
        return Task.FromResult(FaSummary());
    });

    public Task ShowFaAsync() => ExecuteAsync("FA diff", _ => Task.FromResult(FaSummary()));

    public Task ExportFaAsync(string path) => ExecuteAsync("Export FA", token => Task.Run(() =>
    {
        RequireFa().ExportNewFile(path);
        return $"Exported offline FA XML to {path}. No vehicle operation performed.";
    }, token));

    public Task ImportCodingAsync(string path) => ExecuteAsync("Inspect coding file", token => Task.Run(() =>
    {
        Coding = CodingFile.Load(path);
        return CodingSummary;
    }, token));

    public Task ImportCafAsync(string path) => ExecuteAsync("Read CAF definition", async token =>
    {
        var reader = _cafReader ?? throw new InvalidOperationException("No offline CAF reader is configured.");
        Caf = await reader.ReadAsync(path, Settings.IstaPath, Settings.JavaPath, token).ConfigureAwait(false);
        return CafSummary;
    });

    public Task ReadCodingAsync(byte ecu, string? cafFile = null) => ExecuteAsync("Read coding / decode", async token =>
    {
        if (IsDemo || !IsConnected || Vin == null)
            throw new InvalidOperationException("Connect in --live mode and verify the vehicle VIN before reading coding.");
        var reader = _codingReader ?? throw new InvalidOperationException("No coding reader is configured.");
        var request = CodingRequest(ecu, cafFile) with { ExpectedVin = Vin };
        _vehicle.Disconnect();
        Ecus = Array.Empty<EcuInfo>();
        Decoded = await reader.ReadAsync(request, token).ConfigureAwait(false);
        return DecodedSummary + "\n\nNative connection released for the PSdZ read. F2 reconnects.";
    });

    public Task DecodeCodingAsync(string path, string cafFile) => ExecuteAsync("Decode NCD offline", async token =>
    {
        var reader = _codingReader ?? throw new InvalidOperationException("No coding reader is configured.");
        Decoded = await reader.DecodeAsync(path, cafFile, CodingRequest(0, cafFile), token).ConfigureAwait(false);
        return DecodedSummary;
    });

    public Task ExportReadCodingAsync(string path) => ExecuteAsync("Export original read NCD", token => Task.Run(() =>
    {
        (Decoded ?? throw new InvalidOperationException("Read or decode coding first.")).ExportOriginal(path);
        return $"Exported original SDK-serialized NCD to {path}. No regeneration or vehicle change.";
    }, token));

    private CodingReadRequest CodingRequest(byte ecu, string? cafFile) => new(Settings.Host, Settings.Port,
        Settings.PsdzSeries, ecu, Settings.PsdzDataPath, Settings.IstaPath, Settings.JavaPath, cafFile);

    public Task ExportCodingAsync(string path) => ExecuteAsync("Export original coding file", token => Task.Run(() =>
    {
        (Coding ?? throw new InvalidOperationException("Import a coding file first.")).ExportNewFile(path);
        return $"Exported original bytes to {path}. No decoding or vehicle write performed.";
    }, token));

    public Task InspectBackupAsync(string path) => ExecuteAsync("Inspect backup", token => Task.Run(() =>
    {
        var report = BackupInspector.Inspect(path, token);
        return $"Backup VIN: {report.Manifest.Vin}\nSnapshots: {report.Manifest.Entries.Count}\n" +
            $"Integrity: {(report.IsValid ? "verified (does not imply ECU compatibility)" : "FAILED")}\n" +
            string.Join("\n", report.Problems) + "\nRestore is unavailable in this read-only preview.";
    }, token));

    public Task SaveSettingsAsync(string path) => ExecuteAsync("Save settings", token => Task.Run(() =>
    {
        Settings.SaveTo(path);
        return $"Settings saved to {path}. Legacy application settings are preserved.";
    }, token));

    public void Cancel()
    {
        lock (_stateLock)
            _cancellation?.Cancel();
    }

    private async Task ExecuteAsync(string name, Func<CancellationToken, Task<string>> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operation.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("Another operation is running. Cancel it or wait.");
        CancellationTokenSource? cancellation = null;
        try
        {
            cancellation = new CancellationTokenSource(OperationTimeout);
            lock (_stateLock)
                _cancellation = cancellation;
            IsBusy = true;
            Status = $"{name}...";
            Log(Status);
            Changed?.Invoke();
            try
            {
                Details = await action(cancellation.Token).ConfigureAwait(false);
                Status = $"{name}: complete";
                Log(Status);
            }
            catch (OperationCanceledException)
            {
                _vehicle.Disconnect();
                Ecus = Array.Empty<EcuInfo>();
                Status = $"{name}: canceled or timed out; vehicle disconnected";
                Details = Status;
                Log(Status);
                throw;
            }
            catch (Exception ex)
            {
                Status = $"{name}: failed";
                Details = $"{Status}\n{ex.Message}";
                Log($"{Status}: {ex.Message}");
                throw;
            }
        }
        finally
        {
            lock (_stateLock)
                _cancellation = null;
            cancellation?.Dispose();
            IsBusy = false;
            _operation.Release();
            Changed?.Invoke();
        }
    }

    private FaXmlDocument RequireFa() => Fa ?? throw new InvalidOperationException("Import an FA XML document first.");

    private string FaSummary()
    {
        var fa = RequireFa();
        return "FA XML - OFFLINE ONLY\nSyntax checked; vehicle semantics are not validated.\n" +
            PreviewCapabilities.FaReadUnavailable + "\n\n" +
            string.Join("\n", fa.Fields.Select(f => $"{f.Path} = {f.Value}")) + "\n\nChanges:\n" +
            (fa.Changes.Count == 0 ? "(none)" :
                string.Join("\n", fa.Changes.Select(c => $"{c.Path}: {c.Original} -> {c.Current}")));
    }

    private void Log(string message)
    {
        lock (_stateLock)
        {
            _logs.Enqueue(message);
            while (_logs.Count > 200)
                _logs.Dequeue();
        }
    }

    private static string Format(DiagnosticReading reading) =>
        $"ECU {reading.Ecu:X2} - {reading.Name}\nStatus: {reading.Status} {reading.Error}\n" +
        $"Request: {reading.RequestHex}\nResponse: {reading.ResponseHex}\nPayload: {reading.PayloadHex}\n" +
        string.Join("\n", reading.Values.Select(v => $"{v.Key}: {v.Value}")) + "\n" +
        string.Join("\n", reading.Faults.Select(f => $"{f.Code} / status {f.Status:X2}: {f.Description ?? f.DescriptionStatus}")) + "\n" +
        string.Join("\n", reading.Warnings);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            _vehicle.Dispose();
        }
        finally
        {
            _operation.Release();
        }
    }
}
