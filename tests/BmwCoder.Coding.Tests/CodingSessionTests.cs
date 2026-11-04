using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BmwCoder.Coding;
using BmwCoder.Coding.Checksum;
using BmwCoder.Core;
using BmwCoder.Core.Models;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Coding.Tests;

public class FakeUdsClient : IUdsClient
{
    public byte TargetAddress { get; set; }
    public byte CurrentSession { get; set; } = 0x01; // default session
    public byte[] BlockData { get; set; } = new byte[64];
    public byte[]? TopologyData { get; set; }
    public string Vin { get; set; } = "WBA3A5C50DF999999";
    public bool RoutineControlCalled { get; set; }
    public bool ResetCalled { get; set; }
    public HashSet<ushort> NrcDids { get; } = new();
    public HashSet<ushort> TimeoutDids { get; } = new();
    public byte[]? LastRawPdu { get; private set; }
    public Func<byte, CancellationToken, Task>? ProbeHandler { get; set; }
    public Func<byte, byte[], CancellationToken, Task<UdsResponse>>? RawHandler { get; set; }

    public Task DiagnosticSessionControlAsync(byte target, byte sessionType, CancellationToken cancellationToken = default)
    {
        TargetAddress = target;
        CurrentSession = sessionType;
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadDataByIdentifierAsync(byte target, ushort did, CancellationToken cancellationToken = default)
    {
        TargetAddress = target;
        if (did == 0x3F15)
            return TopologyData != null ? Task.FromResult(TopologyData)
                : throw new UdsNegativeResponseException(target, 0x22, Nrc.RequestOutOfRange);
        if (did == 0xF190) // VIN
        {
            return Task.FromResult(Encoding.ASCII.GetBytes(Vin));
        }

        if (NrcDids.Contains(did))
        {
            throw new UdsNegativeResponseException(target, 0x22, Nrc.RequestOutOfRange);
        }

        if (TimeoutDids.Contains(did))
        {
            throw new TimeoutException($"DID 0x{did:X4} timed out");
        }

        byte[] copy = new byte[BlockData.Length];
        Array.Copy(BlockData, copy, BlockData.Length);
        return Task.FromResult(copy);
    }

    public Task WriteDataByIdentifierAsync(byte target, ushort did, byte[] data, CancellationToken cancellationToken = default)
    {
        TargetAddress = target;
        BlockData = new byte[data.Length];
        Array.Copy(data, BlockData, data.Length);
        return Task.CompletedTask;
    }

    public Task RoutineControlAsync(byte target, byte routineControlType, ushort routineId, byte[]? optionRecord = null, CancellationToken cancellationToken = default)
    {
        RoutineControlCalled = true;
        return Task.CompletedTask;
    }

    public Task EcuResetAsync(byte target, byte resetType, CancellationToken cancellationToken = default)
    {
        ResetCalled = true;
        return Task.CompletedTask;
    }

    public Task TesterPresentAsync(byte target, CancellationToken cancellationToken = default)
    {
        if (ProbeHandler != null)
        {
            return ProbeHandler(target, cancellationToken);
        }

        // 0x10 (Gateway), 0x63 (HU_NBT), 0x60 (KOMBI) respond positive. Others throw timeout
        if (target == 0x10 || target == 0x63 || target == 0x60)
        {
            return Task.CompletedTask;
        }

        throw new TimeoutException($"ECU 0x{target:X2} timed out");
    }

    public Task<UdsResponse> SendRawAsync(byte target, byte[] pdu, CancellationToken cancellationToken = default)
    {
        LastRawPdu = pdu;
        if (RawHandler != null)
            return RawHandler(target, pdu, cancellationToken);
        return Task.FromResult(UdsResponse.FromPdu(target, new byte[] { 0x62, 0xF1, 0x90, 0x41 }));
    }
    public Task<UdsResponse> SendAsync(UdsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void StartKeepAlive(byte target, TimeSpan? interval = null) { }
    public void StopKeepAlive() { }
    public void Dispose() { }
}

[TestClass]
public class CodingSessionTests : IDisposable
{
    private readonly string _tempBackupDir;

    public CodingSessionTests()
    {
        _tempBackupDir = Path.Combine(Path.GetTempPath(), "BmwCoderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempBackupDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempBackupDir))
            {
                Directory.Delete(_tempBackupDir, true);
            }
        }
        catch { }
    }

    [TestMethod]
    public async Task DiscoverEcusAsync_ProbeTimeoutDoesNotPreventLaterResponses()
    {
        var fakeUds = new FakeUdsClient
        {
            ProbeHandler = (target, token) => target == 0x72
                ? Task.Delay(Timeout.Infinite, token)
                : Task.CompletedTask
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        var ecus = await session.DiscoverEcusAsync(
            cancellationToken: deadline.Token, probeTimeout: TimeSpan.FromMilliseconds(30));

        Assert.AreEqual(EcuCatalog.Entries.Count, ecus.Count);
        Assert.IsFalse(ecus.Single(e => e.Address == 0x72).Responds);
        Assert.IsTrue(ecus.Single(e => e.Address == 0x30).Responds);
        Assert.AreEqual(fakeUds.Vin, session.CurrentVin);
    }

    [TestMethod]
    public async Task DiscoverEcusAsync_ReconnectsAfterTransportErrorAndTimeout()
    {
        var connection = new DiscoveryConnection();
        var fakeUds = new FakeUdsClient
        {
            ProbeHandler = (target, token) =>
            {
                Assert.IsTrue(connection.IsConnected);
                if (target == 0x64)
                {
                    throw new HsfzException("Gateway control 0x0043");
                }

                return target == 0x72 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
            }
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var session = new CodingSession(connection, fakeUds, new BackupManager(_tempBackupDir));
        await session.ConnectAsync("127.0.0.1", 6801, deadline.Token);

        var ecus = await session.DiscoverEcusAsync(
            cancellationToken: deadline.Token, probeTimeout: TimeSpan.FromMilliseconds(30));

        Assert.AreEqual(3, connection.ConnectCount);
        Assert.IsFalse(ecus.Single(e => e.Address == 0x64).Responds);
        Assert.IsFalse(ecus.Single(e => e.Address == 0x72).Responds);
        Assert.IsTrue(ecus.Single(e => e.Address == 0x30).Responds);
        Assert.AreEqual(fakeUds.Vin, session.CurrentVin);
    }

    private sealed class DiscoveryConnection : IHsfzConnection
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        public Task ConnectAsync(string host, int port = 6801, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("127.0.0.1", host);
            Assert.AreEqual(6801, port);
            ConnectCount++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<HsfzFrame> SendAsync(HsfzFrame request, CancellationToken cancellationToken = default,
            Func<HsfzFrame, bool>? acceptResponse = null) =>
            throw new NotSupportedException();

        public void Close() => IsConnected = false;
        public void Dispose() => Close();
    }

    [TestMethod]
    public async Task DiscoverEcusAsync_CancellationDuringLastProbePropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var fakeUds = new FakeUdsClient
        {
            ProbeHandler = (target, token) =>
            {
                if (target == EcuCatalog.Entries[^1].Address)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            }
        };
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            session.DiscoverEcusAsync(cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task DiscoverEcusAsync_IdentifiesRespondingEcusAndReadsVin()
    {
        // Arrange
        var fakeUds = new FakeUdsClient();
        var backupMgr = new BackupManager(_tempBackupDir);
        using var session = new CodingSession(uds: fakeUds, backupManager: backupMgr);

        // Act
        var ecus = await session.DiscoverEcusAsync();

        // Assert
        var zgw = ecus.Find(e => e.Address == 0x10);
        var hu = ecus.Find(e => e.Address == 0x63);
        var dme = ecus.Find(e => e.Address == 0x12);

        Assert.IsNotNull(zgw);
        Assert.IsTrue(zgw.Responds);
        Assert.IsNotNull(hu);
        Assert.IsTrue(hu.Responds);
        Assert.IsNotNull(dme);
        Assert.IsFalse(dme.Responds);

        Assert.AreEqual("WBA3A5C50DF999999", session.CurrentVin);
    }

    [TestMethod]
    public async Task ReadBlockAsync_OpensSessionReadsDataAndCreatesSnapshot()
    {
        // Arrange
        var fakeUds = new FakeUdsClient();
        for (int i = 0; i < 64; i++) fakeUds.BlockData[i] = (byte)(i + 1);

        var backupMgr = new BackupManager(_tempBackupDir);
        using var session = new CodingSession(uds: fakeUds, backupManager: backupMgr);

        // Act
        var block = await session.ReadBlockAsync(0x63, 0x3000);

        // Assert
        Assert.AreEqual(0x03, fakeUds.CurrentSession); // Extended session opened
        Assert.AreEqual(64, block.RawData.Length);
        Assert.AreEqual(1, block.RawData[0]);

        // Verify backup on disk
        string vinDir = Path.Combine(_tempBackupDir, "UNKNOWN");
        Assert.IsTrue(Directory.Exists(vinDir));
    }

    [TestMethod]
    public async Task WriteBlockAsync_SafetyLockedEcu_ThrowsException()
    {
        // Arrange
        var fakeUds = new FakeUdsClient();
        var backupMgr = new BackupManager(_tempBackupDir);
        using var session = new CodingSession(uds: fakeUds, backupManager: backupMgr);

        // Act & Assert
        // DME (0x12) is safety-locked in EcuCatalog
        var block = new CodingBlock(0x12, 0x3000, new byte[64]);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.WriteBlockAsync(block));
    }

    [TestMethod]
    public async Task WriteBlockAsync_InvalidChecksum_ThrowsException()
    {
        // Arrange
        var fakeUds = new FakeUdsClient();
        var backupMgr = new BackupManager(_tempBackupDir);
        using var session = new CodingSession(uds: fakeUds, backupManager: backupMgr);

        // HU_NBT (0x63), DID 0x3000 has registered CRC16 checksum
        var block = new CodingBlock(0x63, 0x3000, new byte[64]);
        // Do NOT apply checksum, block is invalid (CRC is 0x0000 instead of calculated)

        // Act & Assert
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.WriteBlockAsync(block));
    }

    [TestMethod]
    public async Task WriteBlockAsync_ValidChecksum_ExecutesPipelineAndVerifies()
    {
        // Arrange
        var fakeUds = new FakeUdsClient();
        var backupMgr = new BackupManager(_tempBackupDir);
        using var session = new CodingSession(uds: fakeUds, backupManager: backupMgr);

        var block = new CodingBlock(0x63, 0x3000, new byte[64]);
        block.RawData[0] = 0xAA;
        block.RawData[1] = 0xBB;

        // Apply checksum so it passes verification
        session.ChecksumRegistry.Apply(block);

        // Act
        await session.WriteBlockAsync(block);

        // Assert
        Assert.AreEqual(0x03, fakeUds.CurrentSession);
        Assert.IsTrue(fakeUds.ResetCalled); // HU_NBT requires reset
        CollectionAssert.AreEqual(block.RawData, fakeUds.BlockData);
    }

    [TestMethod]
    public async Task ScanDidsAsync_ReportsOkNrcAndTimeout()
    {
        var fakeUds = new FakeUdsClient { BlockData = new byte[] { 0x11, 0x22 } };
        fakeUds.NrcDids.Add(0x3001);
        fakeUds.TimeoutDids.Add(0x3002);
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        var hits = await session.ScanDidsAsync(0x63, 0x3000, 0x3002);

        Assert.AreEqual(3, hits.Count);
        Assert.AreEqual("ok", hits[0].Status);
        Assert.AreEqual(2, hits[0].Length);
        Assert.AreEqual("nrc", hits[1].Status);
        Assert.AreEqual((byte)Nrc.RequestOutOfRange, hits[1].Nrc);
        Assert.AreEqual("timeout", hits[2].Status);
        Assert.AreEqual(0x03, fakeUds.CurrentSession);
    }

    [TestMethod]
    public async Task ScanDidsAsync_StopsAtCap()
    {
        var fakeUds = new FakeUdsClient();
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        var hits = await session.ScanDidsAsync(0x63, 0x3000, 0x3010, maxDids: 3);

        Assert.AreEqual(3, hits.Count);
        Assert.AreEqual((ushort)0x3002, hits[2].Did);
    }

    [TestMethod]
    public async Task SendRawAsync_ReturnsResponseWithoutThrowingOnNrcShape()
    {
        var fakeUds = new FakeUdsClient();
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        var response = await session.SendRawAsync(0x63, new byte[] { 0x22, 0xF1, 0x90 });

        CollectionAssert.AreEqual(new byte[] { 0x22, 0xF1, 0x90 }, fakeUds.LastRawPdu);
        Assert.IsTrue(response.IsPositive);
        Assert.AreEqual((byte)0x62, response.ResponseSid);
    }

    [TestMethod]
    public async Task GetInfoAsync_UnknownAddressUsesCatalogFallback()
    {
        var fakeUds = new FakeUdsClient();
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        var info = await session.GetInfoAsync(0x99);

        Assert.AreEqual("Unknown(0x99)", info.Name);
        Assert.IsFalse(info.Responds);
    }

    [TestMethod]
    public async Task ReadVinAsync_SetsCurrentVin()
    {
        var fakeUds = new FakeUdsClient();
        using var session = new CodingSession(uds: fakeUds, backupManager: new BackupManager(_tempBackupDir));

        string? vin = await session.ReadVinAsync();

        Assert.AreEqual("WBA3A5C50DF999999", vin);
        Assert.AreEqual(vin, session.CurrentVin);
    }
}
