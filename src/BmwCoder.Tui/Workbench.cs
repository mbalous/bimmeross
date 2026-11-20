using System.Text;
using System.Text.Json;
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
    private readonly List<DiagnosticReading> _diagnosticReadings = new();
    private readonly Dictionary<DiagnosticReading, (string? Host, int? Port, string? Vin)> _diagnosticEndpoints = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    private string? _connectedHost;
    private int? _connectedPort;
    public event Action? Changed;
    public AppSettings Settings { get; }
    public bool IsDemo => _vehicle.IsDemo;
    public bool IsConnected => _vehicle.IsConnected;
    public TimeSpan RequestTimeout => _vehicle.RequestTimeout;
    public string BackupDirectory => _vehicle.BackupDirectory;
    public string? Vin => _vehicle.Vin;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "Disconnected - read-only preview";
    public string Details { get; private set; } = PreviewCapabilities.CodingUnavailable;
    public IReadOnlyList<EcuInfo> Ecus { get; private set; } = Array.Empty<EcuInfo>();
    public IReadOnlyList<DiagnosticDefinition> DiagnosticDefinitions { get; private set; } = Array.Empty<DiagnosticDefinition>();
    public byte? DiagnosticDefinitionEcu { get; private set; }
    public FaXmlDocument? Fa { get; private set; }
    public CodingFile? Coding { get; private set; }
    public RawBlockDraft? RawDraft { get; private set; }
    public BackupInspection? RestoreInspection { get; private set; }
    public string? RestoreDirectory { get; private set; }
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
    public IReadOnlyList<DiagnosticReading> DiagnosticReadings
    {
        get { lock (_stateLock) return _diagnosticReadings.ToArray(); }
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
        _connectedHost = null;
        _connectedPort = null;
        string host = Settings.Host;
        int port = Settings.Port;
        await _vehicle.ConnectAsync(host, port, token).ConfigureAwait(false);
        if (!IsDemo)
        {
            _connectedHost = host;
            _connectedPort = port;
        }
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
        Record(await _vehicle.ReadDidAsync(ecu, did, token).ConfigureAwait(false)));

    public Task ReadFaultsAsync(byte ecu) => ExecuteAsync("Read faults", async token =>
        Record(await _vehicle.ReadFaultsAsync(ecu, token).ConfigureAwait(false)));

    public Task ProbeAsync(byte ecu) => ExecuteAsync("Check ECU", async token =>
        Record(await _vehicle.ProbeAsync(ecu, token).ConfigureAwait(false)));

    public Task ReadRequestAsync(byte ecu, byte[] request) => ExecuteAsync("Read-only request", async token =>
        Record(await _vehicle.ReadRequestAsync(ecu, request, token).ConfigureAwait(false)));

    public Task ReadDmeReportAsync() => ExecuteAsync("Read DME report", async token =>
    {
        var partial = new List<DiagnosticReading>();
        Details = "DME report (ECU 0x12). Reading; no faults are cleared.";
        var progress = new InlineProgress<DiagnosticReading>(reading =>
        {
            partial.Add(reading);
            Record(reading);
            Details = "DME report (ECU 0x12). Results include failures; no faults were cleared.\n\n" +
                string.Join("\n\n", partial.Select(Format));
            Changed?.Invoke();
        });
        var readings = await _vehicle.ReadDmeReportAsync(progress, token).ConfigureAwait(false);
        string completeness = readings.Count < DiagnosticCatalog.DmeReads.Count + 1 ? "PARTIAL; connection lost or report stopped. " : "";
        return $"DME report (ECU 0x12). {completeness}Results include failures; no faults were cleared.\n\n" +
            string.Join("\n\n", readings.Select(Format));
    }, preservePartialOnCancellation: true, timeout: TimeSpan.FromMilliseconds(
        Math.Max(OperationTimeout.TotalMilliseconds, RequestTimeout.TotalMilliseconds * 12)));

    public Task SetRequestTimeoutAsync(int milliseconds) => ExecuteAsync("Set request timeout", _ =>
    {
        if (milliseconds is < 100 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Request timeout must be between 100 and 30000 milliseconds.");
        _vehicle.RequestTimeout = TimeSpan.FromMilliseconds(milliseconds);
        return Task.FromResult($"Diagnostic request timeout: {milliseconds} ms (not saved).");
    });

    public Task ClearDiagnosticReadingsAsync() => ExecuteAsync("Clear diagnostic results", _ =>
    {
        lock (_stateLock)
        {
            _diagnosticReadings.Clear();
            _diagnosticEndpoints.Clear();
        }
        return Task.FromResult("Diagnostic results cleared.");
    });

    public Task ExportDiagnosticReadingsAsync(string path) => ExecuteAsync("Export diagnostic results", async token =>
    {
        var readings = DiagnosticReadings;
        if (readings.Count == 0)
            throw new InvalidOperationException("There are no diagnostic results to export.");
        (string? Host, int? Port, string? Vin)[] endpoints;
        lock (_stateLock)
            endpoints = readings.Select(r => _diagnosticEndpoints[r]).Distinct().ToArray();
        if (endpoints.Length != 1)
            throw new InvalidOperationException("Results are from different connections. Clear the results before exporting a single-session report.");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var export = new { schema = "bmwcoder/diagnostics/1", host = endpoints[0].Host, port = endpoints[0].Port, vin = endpoints[0].Vin, read_only = true, readings };
        await JsonSerializer.SerializeAsync(stream, export, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }, token).ConfigureAwait(false);
        return $"Exported {readings.Count} diagnostic results to {path}.";
    });

    public string DiagnosticCommand(DiagnosticReading reading)
    {
        (string? Host, int? Port, string? Vin) endpoint;
        lock (_stateLock)
            if (!_diagnosticEndpoints.TryGetValue(reading, out endpoint) || string.IsNullOrEmpty(reading.RequestHex))
                throw new InvalidOperationException("Select a diagnostic result with a request first.");
        if (endpoint.Host == null || endpoint.Port == null)
            throw new InvalidOperationException("Synthetic or unconnected readings have no live CLI endpoint.");
        string host = endpoint.Host.Replace("'", "''", StringComparison.Ordinal);
        return $"bmwcoder raw --host '{host}' --port {endpoint.Port} --ecu 0x{reading.Ecu:X2} --pdu {reading.RequestHex} --json";
    }

    public Task ExportDiagnosticPayloadAsync(DiagnosticReading reading, string path) => ExecuteAsync("Export diagnostic payload", async token =>
    {
        if (!DiagnosticReadings.Contains(reading) || reading.Status != "ok" || string.IsNullOrEmpty(reading.PayloadHex))
            throw new InvalidOperationException("Select a successful diagnostic result with a payload first.");
        byte[] bytes = Convert.FromHexString(reading.PayloadHex);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        return $"Exported {bytes.Length} payload bytes to {path}.";
    });

    public Task OpenRawDraftAsync(DiagnosticReading reading) => ExecuteAsync("Open raw block draft", _ =>
    {
        if (!DiagnosticReadings.Contains(reading) || reading.Status != "ok" ||
            reading.Did is not ushort did || string.IsNullOrEmpty(reading.PayloadHex))
            throw new InvalidOperationException("Select a successful DID reading with a payload first.");
        RawDraft = new RawBlockDraft(reading.Ecu, did, Convert.FromHexString(reading.PayloadHex));
        return Task.FromResult(RawDraftSummary());
    });

    public Task EditRawDraftAsync(int offset, byte value) => ExecuteAsync("Edit raw block offline", _ =>
    {
        RequireRawDraft().SetByte(offset, value);
        return Task.FromResult(RawDraftSummary());
    });

    public Task ShowRawDraftAsync() => ExecuteAsync("Show raw block", _ =>
        Task.FromResult(RawDraftSummary()));

    public Task RevertRawDraftAsync() => ExecuteAsync("Revert raw block", _ =>
    {
        RequireRawDraft().Revert();
        return Task.FromResult(RawDraftSummary());
    });

    public Task VerifyRawChecksumAsync() => ExecuteAsync("Verify legacy checksum", _ =>
    {
        bool matches = RequireRawDraft().VerifyChecksum();
        return Task.FromResult($"Legacy checksum {(matches ? "matches" : "does not match")}; this does not establish ECU compatibility or write safety.\n\n{RawDraftSummary()}");
    });

    public Task ApplyRawChecksumAsync() => ExecuteAsync("Apply legacy checksum offline", _ =>
    {
        RequireRawDraft().ApplyChecksum();
        return Task.FromResult(RawDraftSummary());
    });

    public Task ExportRawDraftAsync(string path) => ExecuteAsync("Export raw block draft", token => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        RequireRawDraft().ExportNewFile(path);
        return $"Exported offline raw block to {path}. No vehicle write was performed.";
    }, token));

    public Task RequestRawWriteAsync() => ExecuteAsync("Write raw block", _ =>
    {
        RequireRawDraft();
        throw new NotSupportedException("Live raw writes are disabled until a target-specific backend and safety qualification are available.");
    });

    public Task RequestLegacyBlockReadAsync() => ExecuteAsync("Legacy block read", _ =>
        throw new NotSupportedException("Legacy extended-session block reads are disabled. Use a default-session DID read; some blocks may be unavailable."));

    public Task LoadDiagnosticDataAsync(byte ecu) => ExecuteAsync("Load ODX diagnostics", async token =>
    {
        DiagnosticDefinitions = Array.Empty<DiagnosticDefinition>();
        DiagnosticDefinitionEcu = null;
        var definitions = await _vehicle.LoadDiagnosticDataAsync(Settings.PsdzDataPath, Settings.PsdzSeries, ecu, token).ConfigureAwait(false);
        DiagnosticDefinitions = definitions;
        DiagnosticDefinitionEcu = ecu;
        return $"Loaded {definitions.Count} candidate DIDs for ECU 0x{ecu:X2}.\n" +
            "Names and layouts are candidates, not a verified ECU variant.";
    });

    public Task ConfigureFaultTextsAsync() => ExecuteAsync("Configure ISTA fault texts", token =>
    {
        token.ThrowIfCancellationRequested();
        _vehicle.ConfigureFaultTexts(Settings.IstaPath);
        return Task.FromResult("ISTA fault texts configured for subsequent reads. Exact-code matches are not ECU-variant verified.");
    });

    public Task BackupSuggestedAsync() => ExecuteAsync("Snapshot suggested DIDs", async token =>
    {
        Details = "Reading suggested DIDs. This is not a full-vehicle or validated coding backup.";
        var progress = new InlineProgress<ReadOnlyBackup>(result =>
        {
            Details = FormatBackup(result);
            Changed?.Invoke();
        });
        var result = await _vehicle.BackupSuggestedAsync(Ecus, progress, token).ConfigureAwait(false);
        return FormatBackup(result);
    }, preservePartialOnCancellation: true, timeout: TimeSpan.FromMilliseconds(Math.Max(
        OperationTimeout.TotalMilliseconds,
        (Ecus.Sum(e => (double)e.SuggestedDids.Length) + 1) * RequestTimeout.TotalMilliseconds)));

    private static string FormatBackup(ReadOnlyBackup result) =>
        $"Suggested-DID snapshots: {result.Saved}; failures: {result.Failures.Count}\n" +
        $"Directory: {result.Directory ?? "(no snapshots saved)"}\n" +
        "Not a full-vehicle or validated coding backup. No session changes or vehicle writes were made.\n" +
        string.Join("\n", result.Failures);

    public Task DisconnectAsync() => ExecuteAsync("Disconnect", _ =>
    {
        _vehicle.Disconnect();
        _connectedHost = null;
        _connectedPort = null;
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

    public Task PrepareRestoreAsync(string path) => ExecuteAsync("Review restore", token => Task.Run(() =>
    {
        RestoreInspection = null;
        RestoreDirectory = null;
        var inspection = BackupInspector.Inspect(path, token);
        RestoreInspection = inspection;
        RestoreDirectory = Path.GetFullPath(path);
        string identity = Vin == null ? "No verified vehicle connected." :
            string.Equals(Vin, inspection.Manifest.Vin, StringComparison.OrdinalIgnoreCase)
                ? "Backup VIN matches the connected vehicle."
                : "Backup VIN does not match the connected vehicle.";
        return $"Restore review: {inspection.Manifest.Entries.Count} snapshots. {identity}\n" +
            $"Integrity: {(inspection.IsValid ? "verified (not evidence of compatibility)" : "FAILED")}\n" +
            string.Join("\n", inspection.Problems) +
            "\nLive restore is disabled until target-specific qualification.";
    }, token));

    public Task RequestRestoreAsync(BackupEntry entry) => ExecuteAsync("Restore blocked", _ =>
    {
        if (RestoreInspection == null || !RestoreInspection.Manifest.Entries.Contains(entry))
            throw new InvalidOperationException("Inspect a backup and select a snapshot first.");
        throw new NotSupportedException("Live restore is disabled until a target-specific backend and safety qualification are available.");
    });

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

    private async Task ExecuteAsync(string name, Func<CancellationToken, Task<string>> action,
        bool preservePartialOnCancellation = false, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operation.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("Another operation is running. Cancel it or wait.");
        CancellationTokenSource? cancellation = null;
        try
        {
            cancellation = new CancellationTokenSource(timeout ?? OperationTimeout);
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
                Details = preservePartialOnCancellation ? $"{Status}\n\n{Details}" : Status;
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
    private RawBlockDraft RequireRawDraft() => RawDraft ?? throw new InvalidOperationException("Open a successful DID result as a raw block draft first.");

    private string RawDraftSummary()
    {
        var draft = RequireRawDraft();
        return $"OFFLINE RAW BLOCK DRAFT - ECU 0x{draft.Ecu:X2}, DID 0x{draft.Did:X4}\n" +
            $"Bytes: {draft.Length}; changed: {draft.ChangedBytes}; SHA-256: {draft.Sha256}\n" +
            "Legacy checksum definitions are unverified. A match does not establish compatibility or write safety. Vehicle writes are unavailable.\n\n" +
            draft.Preview();
    }

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

    private string Record(DiagnosticReading reading)
    {
        lock (_stateLock)
        {
            _diagnosticReadings.Add(reading);
            _diagnosticEndpoints.Add(reading, (_connectedHost, _connectedPort, _vehicle.Vin));
        }
        return Format(reading);
    }

    public static string Format(DiagnosticReading reading) =>
        $"ECU {reading.Ecu:X2} - {reading.Name}\nStatus: {reading.Status} {reading.Error}\n" +
        $"Time (UTC): {reading.Timestamp:O}\nNRC: {reading.Nrc ?? "(none)"}\nDecoder: {reading.DecoderSource ?? "(none)"}\n" +
        $"Request: {reading.RequestHex}\nResponse: {reading.ResponseHex}\nPayload: {reading.PayloadHex}\n" +
        string.Join("\n", reading.Values.Select(v => $"{v.Key}: {v.Value}")) + "\n" +
        string.Join("\n", reading.Software.Select(s => $"{s.ProcessClass:X2}: {s.Id} {s.Version}")) + "\n" +
        string.Join("\n", reading.Faults.Select(f => $"{f.Code} / status {f.Status:X2}: {f.Description ?? f.DescriptionStatus}")) + "\n" +
        string.Join("\n", reading.Warnings);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

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
