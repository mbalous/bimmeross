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
        public string? Vin => null;
        public IReadOnlyList<string> Warnings => Array.Empty<string>();
        public Task ConnectAsync(string host, int port, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken token) => throw new InvalidOperationException("Must not run concurrently.");
        public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken token) => throw new NotSupportedException();
        public void Disconnect() => Disconnected = true;
        public void Dispose() => Disconnect();
    }
}
