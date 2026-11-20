using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Tui.Tests;

[TestClass]
public sealed class WorkbenchTests
{
    [TestMethod]
    public async Task CodingReadBindsVerifiedVinAndReleasesNativeSessionBeforeTheBackendRuns()
    {
        var vehicle = new SyntheticLiveVehicle();
        var reader = new SyntheticCodingReader { OnRead = () => Assert.IsFalse(vehicle.IsConnected) };
        await using var workbench = new Workbench(vehicle, new AppSettings(), codingReader: reader);
        await workbench.ConnectAsync();
        await workbench.ReadCodingAsync(0x63);
        Assert.AreEqual("WBATEST0000000001", reader.LastRequest!.ExpectedVin);
        Assert.AreEqual((byte)0x63, reader.LastRequest.Ecu);
        Assert.IsFalse(workbench.IsConnected);
        Assert.AreEqual(1, workbench.Decoded!.Parameters.Count(p => p.IsDecoded));
    }

    [TestMethod]
    public async Task OfflineDecodePreservesPreviousSnapshotOnErrorAndDemoCannotReadLiveCoding()
    {
        var reader = new SyntheticCodingReader();
        await using var workbench = new Workbench(new DemoVehicle(), new AppSettings(), codingReader: reader);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => workbench.ReadCodingAsync(0x63));
        Assert.IsNull(reader.LastRequest);
        await workbench.DecodeCodingAsync("synthetic.ncd", SyntheticCodingReader.Caf);
        var previous = workbench.Decoded;
        reader.Fail = true;
        await Assert.ThrowsExceptionAsync<FormatException>(() => workbench.DecodeCodingAsync("broken.ncd", SyntheticCodingReader.Caf));
        Assert.AreSame(previous, workbench.Decoded);
        StringAssert.Contains(workbench.Status, "failed");
        Assert.IsFalse(workbench.IsConnected);
    }

    [TestMethod]
    public async Task OfflineCafImportUsesSharedReaderWithoutConnectingAndPreservesPreviousDocumentOnFailure()
    {
        var reader = new SyntheticCafReader();
        await using var workbench = new Workbench(new DemoVehicle(), new AppSettings(), reader);
        await workbench.ImportCafAsync("synthetic.caf");
        Assert.IsFalse(workbench.IsConnected);
        Assert.AreEqual("synthetic", workbench.Caf!.Fields.Single().Value);
        StringAssert.Contains(workbench.CafSummary, "No NCD values");
        reader.Fail = true;
        await Assert.ThrowsExceptionAsync<FormatException>(() => workbench.ImportCafAsync("broken.caf"));
        Assert.AreEqual("synthetic.caf", workbench.Caf.Name);
        StringAssert.Contains(workbench.Status, "failed");
    }

    [TestMethod]
    public async Task DemoGatewayDiscoveryDoesNotChangeHostOrConnect()
    {
        var settings = new AppSettings();
        await using var workbench = new Workbench(new DemoVehicle(), settings);
        await workbench.FindGatewayAsync(new BmwCoder.Transport.EnetInterface("synthetic",
            System.Net.IPAddress.Loopback, System.Net.IPAddress.Loopback));
        Assert.AreEqual("169.254.16.17", settings.Host);
        Assert.IsFalse(workbench.IsConnected);
        StringAssert.Contains(workbench.Details, "does not send");
    }

    [TestMethod]
    public async Task DemoProbeIsSyntheticAndDmeReportIsExplicitlyUnavailable()
    {
        await using var workbench = new Workbench(new DemoVehicle(), new AppSettings());
        await workbench.ConnectAsync();
        await workbench.ProbeAsync(0x63);
        StringAssert.Contains(workbench.Details, "7E00");
        StringAssert.Contains(workbench.Details, "Synthetic demo");
        await workbench.ReadRequestAsync(0x10, new byte[] { 0x22, 0xF1, 0x90 });
        StringAssert.Contains(workbench.Details, "DEMO0000000000001");
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            workbench.ReadRequestAsync(0x10, new byte[] { 0x10, 0x03 }));
        StringAssert.Contains(workbench.Status, "failed");
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => workbench.ReadDmeReportAsync());
        StringAssert.Contains(workbench.Details, "does not model a DME report");
    }

    [TestMethod]
    public async Task DiagnosticDefinitionsUseConfiguredPathsAndDoNotSurviveFailedReload()
    {
        var vehicle = new DiagnosticVehicle();
        var settings = new AppSettings { PsdzDataPath = "synthetic-data", PsdzSeries = "Z999", IstaPath = "synthetic-ista" };
        await using var workbench = new Workbench(vehicle, settings);
        await workbench.LoadDiagnosticDataAsync(0x63);
        Assert.AreEqual(("synthetic-data", "Z999", (byte)0x63), vehicle.Loaded);
        Assert.AreEqual((byte)0x63, workbench.DiagnosticDefinitionEcu);
        Assert.AreEqual((ushort)0xF190, workbench.DiagnosticDefinitions.Single().Did);
        await workbench.ConfigureFaultTextsAsync();
        Assert.AreEqual("synthetic-ista", vehicle.FaultPath);
        vehicle.FailLoad = true;
        await Assert.ThrowsExceptionAsync<FormatException>(() => workbench.LoadDiagnosticDataAsync(0x10));
        Assert.IsNull(workbench.DiagnosticDefinitionEcu);
        Assert.AreEqual(0, workbench.DiagnosticDefinitions.Count);
        await workbench.SetRequestTimeoutAsync(2500);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2500), vehicle.RequestTimeout);
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => workbench.SetRequestTimeoutAsync(99));
        Assert.AreEqual(TimeSpan.FromMilliseconds(2500), vehicle.RequestTimeout);
    }

    [TestMethod]
    public async Task DiagnosticHistoryExportsWithoutOverwritingAndCanBeCleared()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"bimmeross-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var workbench = new Workbench(new DemoVehicle(), new AppSettings());
            await workbench.ConnectAsync();
            await workbench.ReadDidAsync(0x10, 0xF190);
            var reading = workbench.DiagnosticReadings.Single();
            await workbench.OpenRawDraftAsync(reading);
            await workbench.EditRawDraftAsync(0, 0x42);
            Assert.AreEqual(1, workbench.RawDraft!.ChangedBytes);
            StringAssert.Contains(workbench.Details, "Vehicle writes are unavailable");
            await Assert.ThrowsExceptionAsync<NotSupportedException>(() => workbench.RequestRawWriteAsync());
            StringAssert.Contains(workbench.Details, "Live raw writes are disabled");
            await workbench.RevertRawDraftAsync();
            Assert.AreEqual(0, workbench.RawDraft.ChangedBytes);
            string json = Path.Combine(directory, "readings.json");
            string payload = Path.Combine(directory, "payload.bin");
            await workbench.ExportDiagnosticReadingsAsync(json);
            await workbench.ExportDiagnosticPayloadAsync(reading, payload);
            StringAssert.Contains(File.ReadAllText(json), "F190");
            StringAssert.Contains(File.ReadAllText(json), "bmwcoder/diagnostics/1");
            Assert.ThrowsException<InvalidOperationException>(() => workbench.DiagnosticCommand(reading));
            CollectionAssert.AreEqual(Convert.FromHexString(reading.PayloadHex), File.ReadAllBytes(payload));
            await Assert.ThrowsExceptionAsync<IOException>(() => workbench.ExportDiagnosticPayloadAsync(reading, payload));
            await workbench.ClearDiagnosticReadingsAsync();
            Assert.AreEqual(0, workbench.DiagnosticReadings.Count);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => workbench.ExportDiagnosticPayloadAsync(reading, Path.Combine(directory, "other.bin")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CanceledDmeReportRetainsPartialResults()
    {
        var vehicle = new DiagnosticVehicle { BlockReport = true };
        await using var workbench = new Workbench(vehicle, new AppSettings());
        var pending = workbench.ReadDmeReportAsync();
        await vehicle.ReportStarted.Task;
        workbench.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => pending);
        Assert.AreEqual(1, workbench.DiagnosticReadings.Count);
        StringAssert.Contains(workbench.Details, "partial synthetic report");
        StringAssert.Contains(workbench.Details, "canceled");
    }

    [TestMethod]
    public async Task DiagnosticCommandsUseTheOriginalConnectionEndpoint()
    {
        var settings = new AppSettings { Host = "first-host", Port = 6801 };
        await using var workbench = new Workbench(new SyntheticLiveVehicle(), settings);
        await workbench.ConnectAsync();
        await workbench.ReadDidAsync(0x10, 0xF190);
        var reading = workbench.DiagnosticReadings.Single();
        settings.Host = "other-host";
        settings.Port = 1234;
        StringAssert.Contains(workbench.DiagnosticCommand(reading), "--host 'first-host' --port 6801");
        await workbench.ConnectAsync();
        await workbench.ReadDidAsync(0x10, 0xF190);
        string path = Path.Combine(Path.GetTempPath(), $"bimmeross-mixed-{Guid.NewGuid():N}.json");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => workbench.ExportDiagnosticReadingsAsync(path));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task RestoreReviewInspectsSnapshotsButNeverEnablesWrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"bimmeross-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] data = { 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(directory, "63_3000.bin"), data);
            var manifest = new BackupManifest
            {
                Vin = "DEMO0000000000001",
                Entries = new List<BackupEntry>
                {
                    new() { EcuAddress = 0x63, Did = 0x3000, FileName = "63_3000.bin", SizeBytes = data.Length,
                        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) }
                }
            };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
            await using var workbench = new Workbench(new DemoVehicle(), new AppSettings());
            await workbench.ConnectAsync();
            await workbench.PrepareRestoreAsync(directory);
            Assert.IsTrue(workbench.RestoreInspection!.IsValid);
            StringAssert.Contains(workbench.Details, "Live restore is disabled");
            await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
                workbench.RequestRestoreAsync(workbench.RestoreInspection.Manifest.Entries.Single()));
            CollectionAssert.AreEqual(data, File.ReadAllBytes(Path.Combine(directory, "63_3000.bin")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class DiagnosticVehicle : IReadOnlyVehicle
    {
        public (string, string, byte) Loaded { get; private set; }
        public string? FaultPath { get; private set; }
        public bool FailLoad { get; set; }
        public bool BlockReport { get; set; }
        public TaskCompletionSource ReportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDemo => false;
        public bool IsConnected => false;
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public string BackupDirectory => "synthetic-backups";
        public string? Vin => null;
        public IReadOnlyList<string> Warnings => Array.Empty<string>();
        public Task<IReadOnlyList<DiagnosticDefinition>> LoadDiagnosticDataAsync(string path, string series, byte ecu, CancellationToken token)
        {
            if (FailLoad) throw new FormatException("Invalid ODX.");
            Loaded = (path, series, ecu);
            return Task.FromResult<IReadOnlyList<DiagnosticDefinition>>(new[] { new DiagnosticDefinition(0xF190, "VIN") });
        }
        public void ConfigureFaultTexts(string path) => FaultPath = path;
        public Task<ReadOnlyBackup> BackupSuggestedAsync(IReadOnlyList<EcuInfo> ecus, IProgress<ReadOnlyBackup>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task ConnectAsync(string host, int port, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ReadRequestAsync(byte ecu, byte[] request, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ProbeAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
        public async Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(IProgress<DiagnosticReading>? progress, CancellationToken token)
        {
            if (!BlockReport) throw new NotSupportedException();
            progress?.Report(new DiagnosticReading { Ecu = 0x12, Name = "partial synthetic report" });
            ReportStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Array.Empty<DiagnosticReading>();
        }
        public void Disconnect() { }
        public void Dispose() { }
    }

    private sealed class SyntheticCafReader : ICafReader
    {
        public bool Fail { get; set; }
        public Task<CafDefinition> ReadAsync(string path, string istaPath, string javaPath, CancellationToken token) =>
            Fail ? throw new FormatException("Synthetic invalid CAF.") :
                Task.FromResult(CafDefinition.Parse(path, "synthetic-runtime", "<caf><field>synthetic</field></caf>"));
    }

    [TestMethod]
    public async Task DemoCompletesReadEditDiffAndExportWithoutLiveBackend()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bimmeross-fa-{Guid.NewGuid():N}.xml");
        try
        {
            await using var workbench = new Workbench(new DemoVehicle(), new AppSettings());
            await workbench.ConnectAsync();
            await workbench.DiscoverAsync();
            Assert.AreEqual(2, workbench.Ecus.Count);
            Assert.IsTrue(workbench.Ecus.All(e => !e.IsWritable));
            await workbench.ReadDidAsync(0x10, 0xF190);
            StringAssert.Contains(workbench.Details, "DEMO0000000000001");
            await workbench.LoadDemoFaAsync();
            var field = workbench.Fa!.Fields.Single(f => f.Value == "000");
            await workbench.EditFaAsync(field.Id, "123");
            await workbench.ShowFaAsync();
            StringAssert.Contains(workbench.Details, "000 -> 123");
            await workbench.ExportFaAsync(path);
            StringAssert.Contains(File.ReadAllText(path), "<sa>123</sa>");
            await workbench.DisconnectAsync();
            Assert.IsFalse(workbench.IsConnected);
            Assert.IsNotNull(workbench.Fa);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ConcurrentOperationsAreRejectedAndCancellationDisconnects()
    {
        var vehicle = new BlockingVehicle();
        await using var workbench = new Workbench(vehicle, new AppSettings());
        Task pending = workbench.ConnectAsync();
        Assert.IsTrue(workbench.IsBusy);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => workbench.DiscoverAsync());
        workbench.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => pending);
        Assert.IsFalse(workbench.IsBusy);
        Assert.IsTrue(vehicle.Disconnected);
        StringAssert.Contains(workbench.Details, "canceled");
    }

    [TestMethod]
    public async Task OperationErrorsRemainVisibleAndLogBufferIsBounded()
    {
        await using var workbench = new Workbench(new DemoVehicle(), new AppSettings());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => workbench.ReadDidAsync(0x10, 0xF190));
        StringAssert.Contains(workbench.Details, "Connect");
        StringAssert.Contains(workbench.Status, "failed");
        for (int i = 0; i < 110; i++)
            await workbench.DisconnectAsync();
        Assert.AreEqual(200, workbench.Logs.Count);
    }

    private sealed class BlockingVehicle : IReadOnlyVehicle
    {
        public bool Disconnected { get; private set; }
        public bool IsDemo => false;
        public bool IsConnected => false;
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public string BackupDirectory => "synthetic-backups";
        public string? Vin => null;
        public IReadOnlyList<string> Warnings => Array.Empty<string>();
        public Task ConnectAsync(string host, int port, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken token) => throw new InvalidOperationException("Must not run concurrently.");
        public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ReadRequestAsync(byte ecu, byte[] request, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ProbeAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(IProgress<DiagnosticReading>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiagnosticDefinition>> LoadDiagnosticDataAsync(string path, string series, byte ecu, CancellationToken token) => throw new NotSupportedException();
        public void ConfigureFaultTexts(string path) => throw new NotSupportedException();
        public Task<ReadOnlyBackup> BackupSuggestedAsync(IReadOnlyList<EcuInfo> ecus, IProgress<ReadOnlyBackup>? progress, CancellationToken token) => throw new NotSupportedException();
        public void Disconnect() => Disconnected = true;
        public void Dispose() => Disconnect();
    }
}
