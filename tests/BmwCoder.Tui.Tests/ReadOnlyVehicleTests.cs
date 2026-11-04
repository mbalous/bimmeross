using System.Text;
using BmwCoder.Core;
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
        Assert.IsTrue(uds.Requests.All(pdu => pdu[0] is 0x22 or 0x19));
        Assert.IsTrue(uds.Probes > 0);
        vehicle.Disconnect();
        Assert.IsNull(vehicle.Vin);
        Assert.IsFalse(vehicle.IsConnected);
        Assert.ThrowsException<InvalidOperationException>(() => vehicle.ReadDidAsync(0x63, 0xF18C, CancellationToken.None));
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
        public List<byte[]> Requests { get; } = new();
        public int Probes { get; private set; }
        public Task<UdsResponse> SendRawAsync(byte target, byte[] pdu, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(pdu.ToArray());
            byte[] payload = pdu[0] switch
            {
                0x22 when pdu[1] == 0xF1 && pdu[2] == 0x90 => Encoding.ASCII.GetBytes(ReturnedVin),
                0x22 => Encoding.ASCII.GetBytes("SYNTHETIC"),
                0x19 => new byte[] { 0xFF },
                _ => throw new InvalidOperationException("A non-read request reached the backend.")
            };
            byte[] response = pdu[0] == 0x22
                ? new byte[] { 0x62, pdu[1], pdu[2] }.Concat(payload).ToArray()
                : new byte[] { 0x59, pdu[1] }.Concat(payload).ToArray();
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
