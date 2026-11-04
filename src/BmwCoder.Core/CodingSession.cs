using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BmwCoder.Coding;
using BmwCoder.Coding.Checksum;
using BmwCoder.Core.Models;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BmwCoder.Core;

public sealed partial class CodingSession : IDisposable
{
    private readonly IHsfzConnection _connection;
    private readonly IUdsClient _uds;
    private readonly BackupManager _backupManager;
    private readonly ChecksumRegistry _checksumRegistry;
    private readonly ILogger<CodingSession> _logger;
    private string? _connectedHost;
    private int _connectedPort;
    private string? _sessionBackupDir;
    private readonly HashSet<(byte Ecu, ushort Did)> _backedUpBlocks = new();
    private readonly List<string> _discoveryWarnings = new();

    public IHsfzConnection Connection => _connection;
    public IUdsClient Uds => _uds;
    public BackupManager BackupManager => _backupManager;
    public ChecksumRegistry ChecksumRegistry => _checksumRegistry;
    public string? CurrentVin { get; private set; }
    public IReadOnlyList<string> DiscoveryWarnings => _discoveryWarnings;
    public IstaFaultLookup? FaultDescriptions { get; set; }
    public PsdzDiagnosticData? DiagnosticData { get; set; }
    private bool _observedDmeSgbdVerified;

    public CodingSession(
        IHsfzConnection? connection = null,
        IUdsClient? uds = null,
        BackupManager? backupManager = null,
        ChecksumRegistry? checksumRegistry = null,
        ILogger<CodingSession>? logger = null)
    {
        _logger = logger ?? NullLogger<CodingSession>.Instance;
        _connection = connection ?? new HsfzConnection();
        _uds = uds ?? new UdsClient(_connection);
        _backupManager = backupManager ?? new BackupManager();
        _checksumRegistry = checksumRegistry ?? ChecksumRegistry.Default;
    }

    public async Task ConnectAsync(string host = "169.254.16.17", int port = 6801, CancellationToken cancellationToken = default)
    {
        _observedDmeSgbdVerified = false;
        _logger.LogInformation("Connecting to vehicle at {Host}:{Port} via HSFZ...", host, port);
        await _connection.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _connectedHost = host;
        _connectedPort = port;
        _logger.LogInformation("HSFZ transport connection established successfully.");
    }

    public async Task<List<EcuInfo>> DiscoverEcusAsync(
        IProgress<(EcuInfo Info, int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? probeTimeout = null,
        TimeSpan? discoveryBudget = null)
    {
        if (probeTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        if (discoveryBudget is { } budget && budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(discoveryBudget));
        _discoveryWarnings.Clear();
        var discoveryClock = Stopwatch.StartNew();
        _logger.LogInformation("Reading installed ECU addresses from the gateway...");
        var results = new List<EcuInfo>();
        IReadOnlyList<byte> addresses;
        string source;
        try
        {
            using var inventoryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            double inventoryTimeout = (probeTimeout ?? _connection.Timeout).TotalMilliseconds;
            if (discoveryBudget is { } inventoryBudget)
                inventoryTimeout = Math.Min(inventoryTimeout, inventoryBudget.TotalMilliseconds / 4);
            inventoryCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, inventoryTimeout)));
            await ReconnectIfNeededAsync(inventoryCts.Token).ConfigureAwait(false);
            byte[] inventory = await _uds.ReadDataByIdentifierAsync(GatewayAddress, VehicleTopology.GatewayEcuListDid, inventoryCts.Token).ConfigureAwait(false);
            addresses = VehicleTopology.DecodeEcuList(inventory);
            source = "Gateway ECU list (0x3F15)";
            _logger.LogInformation("Gateway reports {Count} installed diagnostic addresses.", addresses.Count);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is TimeoutException or OperationCanceledException or HsfzException or UdsNegativeResponseException or FormatException)
        {
            if (ex is not UdsNegativeResponseException && ex is not FormatException)
                _connection.Close();
            string warning = $"Gateway ECU inventory unavailable: {ex.Message} Falling back to catalog addresses; this is not a complete vehicle inventory.";
            _discoveryWarnings.Add(warning);
            _logger.LogWarning("{Warning}", warning);
            addresses = EcuCatalog.Entries.Select(e => e.Address).ToArray();
            source = "Catalog fallback (incomplete)";
        }
        int total = addresses.Count;
        TimeSpan effectiveProbeTimeout = probeTimeout ?? _connection.Timeout;
        if (discoveryBudget is { } commandBudget)
            effectiveProbeTimeout = TimeSpan.FromMilliseconds(Math.Max(1,
                Math.Min(effectiveProbeTimeout.TotalMilliseconds,
                    (commandBudget.TotalMilliseconds - discoveryClock.Elapsed.TotalMilliseconds - 20 * (total + 1)) / (total + 1))));
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte address = addresses[i];
            var entry = EcuCatalog.FindByAddress(address);

            var info = new EcuInfo
            {
                Address = address,
                Name = DiagnosticData?.GetEcuName(address) ?? entry?.Name ?? $"Unknown(0x{address:X2})",
                Description = entry?.Description ?? "Gateway-listed module; ODX names are candidates, not verified ECU variants",
                IsWritable = entry?.IsWritable ?? false,
                SuggestedDids = entry?.SuggestedDids ?? Array.Empty<ushort>(),
                DiscoverySource = source,
                NameSource = DiagnosticData?.GetEcuName(address) != null
                    ? DiagnosticData.SourceDirectory : entry != null ? "Built-in catalog fallback" : "Unknown",
                Responds = false
            };

            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(effectiveProbeTimeout);
                await ReconnectIfNeededAsync(probeCts.Token).ConfigureAwait(false);
                _logger.LogDebug("Probing ECU 0x{Address:X2} ({Name}) with TesterPresent...", address, info.Name);
                var probe = Stopwatch.StartNew();
                await _uds.TesterPresentAsync(address, probeCts.Token).ConfigureAwait(false);
                probe.Stop();
                info.Responds = true;
                info.RttMs = (int)probe.ElapsedMilliseconds;
                _logger.LogInformation("ECU 0x{Address:X2} ({Name}) responded: ONLINE", address, info.Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or UdsNegativeResponseException or HsfzException)
            {
                if (ex is not UdsNegativeResponseException)
                {
                    _connection.Close();
                }

                info.ProbeError = ex.Message;
                if (ex is UdsNegativeResponseException)
                    info.Responds = true;
                _logger.LogDebug("ECU 0x{Address:X2} ({Name}) probe result: {Message}", address, info.Name, ex.Message);
            }

            results.Add(info);
            progress?.Report((info, i + 1, total));
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        // Attempt to read VIN from Gateway (0x10) if online
        var gateway = results.Find(r => r.Address == 0x10 && r.Responds);
        if (gateway != null)
        {
            try
            {
                using var vinCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                vinCts.CancelAfter(effectiveProbeTimeout);
                await ReconnectIfNeededAsync(vinCts.Token).ConfigureAwait(false);
                _logger.LogInformation("Reading VIN from Central Gateway (0x10)...");
                string? vin = await ReadVinAsync(vinCts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(vin))
                {
                    _logger.LogInformation("Vehicle VIN detected: {Vin}", CurrentVin);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or UdsNegativeResponseException or HsfzException)
            {
                _logger.LogWarning("Could not read VIN from Gateway: {Message}", ex.Message);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _sessionBackupDir = _backupManager.GetSessionBackupDirectory(CurrentVin ?? "UNKNOWN", DateTime.UtcNow);
        _logger.LogInformation("Session backup directory configured at: {Dir}", _sessionBackupDir);

        return results;
    }

    public const int DefaultMaxScanDids = 64;
    public const ushort VinDid = 0xF190;
    public const byte GatewayAddress = 0x10;

    private async Task ReconnectIfNeededAsync(CancellationToken cancellationToken)
    {
        // A cancelled socket read can close the TCP connection or leave a partial frame.
        if (!_connection.IsConnected && _connectedHost != null)
        {
            await _connection.ConnectAsync(_connectedHost, _connectedPort, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string?> ReadVinAsync(CancellationToken cancellationToken = default)
    {
        byte[] vinBytes = await _uds.ReadDataByIdentifierAsync(GatewayAddress, VinDid, cancellationToken).ConfigureAwait(false);
        string vin = Encoding.ASCII.GetString(vinBytes).Trim('\0', ' ');
        if (string.IsNullOrWhiteSpace(vin))
        {
            return null;
        }

        CurrentVin = vin;
        return vin;
    }

    public Task<UdsResponse> SendRawAsync(byte ecuAddress, byte[] pdu, CancellationToken cancellationToken = default)
    {
        if (pdu == null || pdu.Length == 0)
        {
            throw new ArgumentException("PDU cannot be empty.", nameof(pdu));
        }

        return _uds.SendRawAsync(ecuAddress, pdu, cancellationToken);
    }

    public async Task<IReadOnlyList<ScanHit>> ScanDidsAsync(
        byte ecuAddress,
        ushort from,
        ushort to,
        int step = 1,
        int maxDids = DefaultMaxScanDids,
        CancellationToken cancellationToken = default)
    {
        if (step < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(step), "Step must be at least 1.");
        }

        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "Scan end must be greater than or equal to start.");
        }

        if (maxDids < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDids), "Scan cap must be at least 1.");
        }

        var hits = new List<ScanHit>();
        await _uds.DiagnosticSessionControlAsync(ecuAddress, 0x03, cancellationToken).ConfigureAwait(false);

        for (int did = from; did <= to && hits.Count < maxDids; did += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort current = (ushort)did;
            try
            {
                byte[] data = await _uds.ReadDataByIdentifierAsync(ecuAddress, current, cancellationToken).ConfigureAwait(false);
                hits.Add(new ScanHit { Did = current, Status = "ok", Length = data.Length });
            }
            catch (UdsNegativeResponseException ex)
            {
                hits.Add(new ScanHit { Did = current, Status = "nrc", Nrc = (byte)ex.Nrc });
            }
            catch (TimeoutException)
            {
                hits.Add(new ScanHit { Did = current, Status = "timeout" });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                hits.Add(new ScanHit { Did = current, Status = "timeout" });
            }
        }

        return hits;
    }

    public async Task<EcuSnapshot> GetInfoAsync(byte ecuAddress, CancellationToken cancellationToken = default)
    {
        var entry = EcuCatalog.FindByAddress(ecuAddress);
        string name = DiagnosticData?.GetEcuName(ecuAddress) ?? entry?.Name ?? $"Unknown(0x{ecuAddress:X2})";
        var snapshot = new EcuSnapshot
        {
            Address = ecuAddress,
            Name = name,
            Description = entry?.Description ?? string.Empty,
            IsWritable = entry?.IsWritable ?? false
        };

        var probe = Stopwatch.StartNew();
        try
        {
            await _uds.TesterPresentAsync(ecuAddress, cancellationToken).ConfigureAwait(false);
            probe.Stop();
            snapshot.Responds = true;
            snapshot.RttMs = (int)probe.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is TimeoutException or UdsNegativeResponseException or HsfzException or OperationCanceledException)
        {
            _logger.LogDebug("ECU 0x{Address:X2} did not respond to info probe: {Message}", ecuAddress, ex.Message);
            return snapshot;
        }

        if (ecuAddress == GatewayAddress)
        {
            try
            {
                snapshot.Vin = await ReadVinAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or UdsNegativeResponseException or HsfzException)
            {
                _logger.LogWarning("Could not read VIN from Gateway: {Message}", ex.Message);
            }
        }

        return snapshot;
    }

    public async Task<CodingBlock> ReadBlockAsync(byte ecuAddress, ushort did, CancellationToken cancellationToken = default)
    {
        var catalogEntry = EcuCatalog.FindByAddress(ecuAddress);
        string ecuName = catalogEntry?.Name ?? $"ECU_0x{ecuAddress:X2}";

        _logger.LogInformation("Reading coding block for {Ecu} (0x{EcuAddr:X2}), DID 0x{Did:X4}...", ecuName, ecuAddress, did);

        // 1. Open Extended Diagnostic Session (0x10 0x03)
        await _uds.DiagnosticSessionControlAsync(ecuAddress, 0x03, cancellationToken).ConfigureAwait(false);

        // 2. Read raw data by identifier
        byte[] rawData = await _uds.ReadDataByIdentifierAsync(ecuAddress, did, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Received {Bytes} bytes for {Ecu} DID 0x{Did:X4}.", rawData.Length, ecuName, did);

        var block = new CodingBlock(ecuAddress, did, rawData, CurrentVin);

        // 3. Persist automatic snapshot to backup directory
        if (_sessionBackupDir == null)
        {
            _sessionBackupDir = _backupManager.GetSessionBackupDirectory(CurrentVin ?? "UNKNOWN", DateTime.UtcNow);
        }

        await _backupManager.SaveBlockSnapshotAsync(_sessionBackupDir, block, ecuName, cancellationToken).ConfigureAwait(false);
        _backedUpBlocks.Add((ecuAddress, did));
        _logger.LogInformation("Saved initial block snapshot to session backup.");

        return block;
    }

    public async Task WriteBlockAsync(CodingBlock block, CancellationToken cancellationToken = default)
    {
        var catalogEntry = EcuCatalog.FindByAddress(block.EcuAddress);
        string ecuName = catalogEntry?.Name ?? $"ECU_0x{block.EcuAddress:X2}";

        // 1. Safety check: Is this ECU locked against writes?
        if (catalogEntry != null && !catalogEntry.IsWritable)
        {
            throw new InvalidOperationException($"Writing to {ecuName} (0x{block.EcuAddress:X2}) is safety-locked to prevent vehicle bricking.");
        }

        // 2. Checksum verification: Must be known and valid
        if (!_checksumRegistry.HasChecksum(block.EcuAddress, block.Did))
        {
            throw new InvalidOperationException($"Writing blocked: Checksum algorithm is unknown for {ecuName} (0x{block.EcuAddress:X2}), DID 0x{block.Did:X4}.");
        }

        if (!_checksumRegistry.Verify(block))
        {
            throw new InvalidOperationException($"Writing blocked: Checksum verification failed on modified block for {ecuName} DID 0x{block.Did:X4}. Re-apply checksum before writing.");
        }

        // 3. Mandatory Auto-Backup: If block hasn't been saved yet this session, re-read and snapshot current ECU state
        if (!_backedUpBlocks.Contains((block.EcuAddress, block.Did)))
        {
            _logger.LogInformation("Performing mandatory pre-write backup for {Ecu} DID 0x{Did:X4}...", ecuName, block.Did);
            if (_sessionBackupDir == null)
            {
                _sessionBackupDir = _backupManager.GetSessionBackupDirectory(CurrentVin ?? "UNKNOWN", DateTime.UtcNow);
            }

            byte[] currentLive = await _uds.ReadDataByIdentifierAsync(block.EcuAddress, block.Did, cancellationToken).ConfigureAwait(false);
            var preWriteSnapshot = new CodingBlock(block.EcuAddress, block.Did, currentLive, CurrentVin);
            await _backupManager.SaveBlockSnapshotAsync(_sessionBackupDir, preWriteSnapshot, ecuName, cancellationToken).ConfigureAwait(false);
            _backedUpBlocks.Add((block.EcuAddress, block.Did));
            _logger.LogInformation("Pre-write snapshot successfully saved.");
        }

        // 4. Open Extended Diagnostic Session (0x10 0x03)
        _logger.LogInformation("Switching {Ecu} to Extended Diagnostic Session (0x03)...", ecuName);
        await _uds.DiagnosticSessionControlAsync(block.EcuAddress, 0x03, cancellationToken).ConfigureAwait(false);

        // 5. Write Data via WriteDataByIdentifier (0x2E)
        _logger.LogInformation("Writing {Length} bytes to {Ecu} DID 0x{Did:X4}...", block.RawData.Length, ecuName, block.Did);
        await _uds.WriteDataByIdentifierAsync(block.EcuAddress, block.Did, block.RawData, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("WriteDataByIdentifier positive response received.");

        // 6. Routine control if required
        if (catalogEntry != null && catalogEntry.RequiresRoutineControl)
        {
            _logger.LogInformation("Executing RoutineControl (0x31 0x01 0x{Routine:X4}) on {Ecu}...", catalogEntry.RoutineId, ecuName);
            await _uds.RoutineControlAsync(block.EcuAddress, 0x01, catalogEntry.RoutineId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // 7. ECU reset if required
        if (catalogEntry != null && catalogEntry.RequiresReset)
        {
            _logger.LogInformation("Requesting ECU Hard Reset (0x11 0x01) on {Ecu}...", ecuName);
            await _uds.EcuResetAsync(block.EcuAddress, 0x01, cancellationToken).ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false); // Allow ECU to reboot
        }

        // 8. Re-read and byte-for-byte post-write verification
        _logger.LogInformation("Performing post-write byte-for-byte verification...");
        await _uds.DiagnosticSessionControlAsync(block.EcuAddress, 0x03, cancellationToken).ConfigureAwait(false);
        byte[] readBack = await _uds.ReadDataByIdentifierAsync(block.EcuAddress, block.Did, cancellationToken).ConfigureAwait(false);

        if (!readBack.AsSpan().SequenceEqual(block.RawData))
        {
            _logger.LogError("POST-WRITE VERIFICATION FAILED! Read-back data ({ReadLen} bytes) differs from written block ({WriteLen} bytes).",
                readBack.Length, block.RawData.Length);
            throw new InvalidOperationException($"Post-write verification failed! ECU data does not match the written bytes. Revert from backup immediately.");
        }

        _logger.LogInformation("Post-write verification PASSED! Write successfully committed.");
    }

    public void Close()
    {
        _connection.Close();
    }

    public void Dispose()
    {
        _uds.Dispose();
        _connection.Dispose();
    }
}
