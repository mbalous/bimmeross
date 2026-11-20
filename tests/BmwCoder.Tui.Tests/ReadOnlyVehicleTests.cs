using System.Text;
using BmwCoder.Core;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Tui.Tests;

[TestClass]
public sealed class ReadOnlyVehicleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bimmeross-readonly-{Guid.NewGuid():N}");
    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task LiveFacadeUsesExistingReadOperationsAndNeverInheritsWritePermissions()
    {
        var connection = new RecordingConnection();
        var uds = new RecordingUds();
        var session = new CodingSession(connection, uds, new BackupManager(_directory));
        using var vehicle = new ReadOnlyVehicle(session);
        await vehicle.ConnectAsync("synthetic-host", 6801, CancellationToken.None);
        Assert.AreEqual("WBA00000000000001", vehicle.Vin);
        var ecus = await vehicle.DiscoverAsync(CancellationToken.None);
        Assert.AreEqual(2, ecus.Count);
        Assert.IsTrue(ecus.All(e => !e.IsWritable));
        var read = await vehicle.ReadDidAsync(0x63, 0xF18C, CancellationToken.None);
        Assert.AreEqual("ok", read.Status);
        await vehicle.ReadFaultsAsync(0x63, CancellationToken.None);
        var probe = await vehicle.ProbeAsync(0x63, CancellationToken.None);
        Assert.AreEqual("7E00", probe.ResponseHex);
        var rawFaults = await vehicle.ReadRequestAsync(0x63, new byte[] { 0x19, 0x02, 0xFF }, CancellationToken.None);
        Assert.AreEqual("1902FF", rawFaults.RequestHex);
        int requestsBeforeWrite = uds.Requests.Count;
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            vehicle.ReadRequestAsync(0x63, new byte[] { 0x2E, 0x30, 0x00 }, CancellationToken.None));
        Assert.AreEqual(requestsBeforeWrite, uds.Requests.Count);
        int reportStart = uds.Requests.Count;
        var report = await vehicle.ReadDmeReportAsync(null, CancellationToken.None);
        Assert.AreEqual(11, report.Count);
        Assert.IsTrue(report.All(r => r.Ecu == 0x12));
        Assert.IsTrue(uds.Targets.Skip(reportStart).All(target => target == 0x12));
        Assert.IsTrue(uds.Requests.All(pdu => pdu[0] is 0x22 or 0x19 or 0x3E));
        Assert.IsTrue(uds.Probes > 0);
        vehicle.Disconnect();
        Assert.IsNull(vehicle.Vin);
        Assert.IsFalse(vehicle.IsConnected);
        Assert.ThrowsException<InvalidOperationException>(() => vehicle.ReadDidAsync(0x63, 0xF18C, CancellationToken.None));
        Assert.ThrowsException<InvalidOperationException>(() => vehicle.ProbeAsync(0x63, CancellationToken.None));
        Assert.ThrowsException<InvalidOperationException>(() => vehicle.ReadDmeReportAsync(null, CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidVehicleIdentityFailsExplicitlyAndClosesTransport()
    {
        var connection = new RecordingConnection();
        var session = new CodingSession(connection, new RecordingUds { ReturnedVin = "NOT-A-VIN" }, new BackupManager(_directory));
        using var vehicle = new ReadOnlyVehicle(session);
        await Assert.ThrowsExceptionAsync<FormatException>(() => vehicle.ConnectAsync("synthetic-host", 6801, CancellationToken.None));
        Assert.IsFalse(vehicle.IsConnected);
        Assert.IsNull(vehicle.Vin);
    }

    [TestMethod]
    public async Task SuggestedDidBackupSavesVerifiedIdentityAndReportsUnsupportedReads()
    {
        var uds = new RecordingUds();
        using var vehicle = new ReadOnlyVehicle(new CodingSession(new RecordingConnection(), uds, new BackupManager(_directory)));
        await vehicle.ConnectAsync("synthetic-host", 6801, CancellationToken.None);
        var ecus = new[]
        {
            new EcuInfo { Address = 0x63, Name = "synthetic", Responds = true, SuggestedDids = new ushort[] { 0xF18C, 0xFFFF, 0xF18C } },
            new EcuInfo { Address = 0x40, Responds = false, SuggestedDids = new ushort[] { 0x3000 } }
        };
        var backup = await vehicle.BackupSuggestedAsync(ecus, null, CancellationToken.None);
        Assert.AreEqual(1, backup.Saved);
        Assert.AreEqual(1, backup.Failures.Count);
        Assert.IsNotNull(backup.Directory);
        var inspected = BackupInspector.Inspect(backup.Directory);
        Assert.IsTrue(inspected.IsValid);
        Assert.AreEqual(vehicle.Vin, inspected.Manifest.Vin);
        Assert.AreEqual((ushort)0xF18C, inspected.Manifest.Entries.Single().Did);
        Assert.AreEqual(2, uds.Requests.Count(p => p[0] == 0x22 && !(p[1] == 0xF1 && p[2] == 0x90)));
        Assert.IsTrue(uds.Requests.All(p => p[0] == 0x22));
        var next = await vehicle.BackupSuggestedAsync(ecus, null, CancellationToken.None);
        Assert.AreNotEqual(backup.Directory, next.Directory);
    }

    [TestMethod]
    public async Task DmeReportStopsAfterTransportTimeoutInsteadOfReconnectingWithoutVinVerification()
    {
        var uds = new RecordingUds { FailDme = true };
        var connection = new RecordingConnection();
        using var vehicle = new ReadOnlyVehicle(new CodingSession(connection, uds, new BackupManager(_directory)));
        await vehicle.ConnectAsync("synthetic-host", 6801, CancellationToken.None);
        var report = await vehicle.ReadDmeReportAsync(null, CancellationToken.None);
        Assert.AreEqual(1, report.Count);
        Assert.AreEqual("timeout", report[0].Status);
        Assert.IsFalse(vehicle.IsConnected);
        Assert.AreEqual(1, uds.Targets.Count(t => t == 0x12));
    }

    [TestMethod]
    public async Task CanceledSnapshotPreservesTheCompletedManifest()
    {
        var uds = new RecordingUds();
        using var vehicle = new ReadOnlyVehicle(new CodingSession(new RecordingConnection(), uds, new BackupManager(_directory)));
        await vehicle.ConnectAsync("synthetic-host", 6801, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        ReadOnlyBackup? partial = null;
        var progress = new InlineProgress<ReadOnlyBackup>(result =>
        {
            partial = result;
            cancellation.Cancel();
        });
        var ecus = new[] { new EcuInfo { Address = 0x63, Name = "synthetic", Responds = true, SuggestedDids = new ushort[] { 0xF18C, 0xF18B } } };
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => vehicle.BackupSuggestedAsync(ecus, progress, cancellation.Token));
        Assert.IsNotNull(partial?.Directory);
        Assert.AreEqual(1, partial.Saved);
        Assert.IsTrue(BackupInspector.Inspect(partial.Directory).IsValid);
        Assert.AreEqual(1, uds.Targets.Count(t => t == 0x63));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingConnection : IHsfzConnection
    {
        public bool IsConnected { get; private set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);
        public Task ConnectAsync(string host, int port = 6801, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task<HsfzFrame> SendAsync(HsfzFrame request, CancellationToken cancellationToken = default,
            Func<HsfzFrame, bool>? acceptResponse = null) => throw new InvalidOperationException("UDS fake handles requests.");
        public void Close() => IsConnected = false;
        public void Dispose() => Close();
    }

    private sealed class RecordingUds : IUdsClient
    {
        public string ReturnedVin { get; init; } = "WBA00000000000001";
        public bool FailDme { get; init; }
        public List<byte[]> Requests { get; } = new();
        public List<byte> Targets { get; } = new();
        public int Probes { get; private set; }
        public Task<UdsResponse> SendRawAsync(byte target, byte[] pdu, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(pdu.ToArray());
            Targets.Add(target);
            if (target == 0x12 && FailDme)
                throw new TimeoutException("Synthetic timeout.");
            if (pdu.SequenceEqual(new byte[] { 0x22, 0xFF, 0xFF }))
                return Task.FromResult(UdsResponse.FromPdu(target, new byte[] { 0x7F, 0x22, 0x31 }));
            byte[] payload = pdu[0] switch
            {
                0x22 when pdu[1] == 0xF1 && pdu[2] == 0x90 => Encoding.ASCII.GetBytes(ReturnedVin),
                0x22 => Encoding.ASCII.GetBytes("SYNTHETIC"),
                0x19 => new byte[] { 0xFF },
                0x3E => Array.Empty<byte>(),
                _ => throw new InvalidOperationException("A non-read request reached the backend.")
            };
            byte[] response = pdu[0] switch
            {
                0x22 => new byte[] { 0x62, pdu[1], pdu[2] }.Concat(payload).ToArray(),
                0x19 => new byte[] { 0x59, pdu[1] }.Concat(payload).ToArray(),
                _ => new byte[] { 0x7E, pdu[1] }
            };
            return Task.FromResult(UdsResponse.FromPdu(target, response));
        }
        public Task<byte[]> ReadDataByIdentifierAsync(byte target, ushort did, CancellationToken cancellationToken = default) =>
            Task.FromResult(did == 0x3F15 ? new byte[] { 0, 2, 0x10, 0x63 } : Encoding.ASCII.GetBytes(ReturnedVin));
        public Task TesterPresentAsync(byte target, CancellationToken cancellationToken = default)
        {
            Probes++;
            return Task.CompletedTask;
        }
        public Task<UdsResponse> SendAsync(UdsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiagnosticSessionControlAsync(byte target, byte sessionType, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Session changes prohibited in this read-only test.");
        public Task WriteDataByIdentifierAsync(byte target, ushort did, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Write attempted.");
        public Task RoutineControlAsync(byte target, byte routineControlType, ushort routineId, byte[]? optionRecord = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Routine attempted.");
        public Task EcuResetAsync(byte target, byte resetType, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Reset attempted.");
        public void StartKeepAlive(byte target, TimeSpan? interval = null) => throw new NotSupportedException();
        public void StopKeepAlive() { }
        public void Dispose() { }
    }
}
