using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Uds.Tests;

public class MockHsfzConnection : IHsfzConnection
{
    private readonly Queue<HsfzFrame> _responseQueue = new();
    public List<HsfzFrame> SentFrames { get; } = new();

    public bool IsConnected { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public void EnqueueResponse(HsfzFrame response)
    {
        _responseQueue.Enqueue(response);
    }

    public Task ConnectAsync(string host, int port = 6801, CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<HsfzFrame> SendAsync(HsfzFrame request, CancellationToken cancellationToken = default,
        Func<HsfzFrame, bool>? acceptResponse = null)
    {
        SentFrames.Add(request);

        while (_responseQueue.Count > 0)
        {
            var response = _responseQueue.Dequeue();
            if (acceptResponse == null || acceptResponse(response))
                return Task.FromResult(response);
        }

        throw new TimeoutException("Mock connection has no responses enqueued.");
    }

    public void Close()
    {
        IsConnected = false;
    }

    public void Dispose()
    {
        Close();
    }
}

[TestClass]
public class UdsClientTests
{
    [TestMethod]
    public async Task ReadDataByIdentifier_PositiveResponse_ReturnsPayloadData()
    {
        // Arrange
        var mockConn = new MockHsfzConnection();
        var client = new UdsClient(mockConn);

        // VIN payload: 0x62 (RDBI positive) + 0xF1 0x90 (DID echo) + "WBA3A5C50DF123456"
        byte[] vinBytes = Encoding.ASCII.GetBytes("WBA3A5C50DF123456");
        byte[] payload = new byte[3 + vinBytes.Length];
        payload[0] = 0x62;
        payload[1] = 0xF1;
        payload[2] = 0x90;
        vinBytes.CopyTo(payload, 3);

        mockConn.EnqueueResponse(new HsfzFrame(0xF4, payload, sourceAddress: 0x10));

        // Act
        byte[] data = await client.ReadDataByIdentifierAsync(0x10, 0xF190);

        // Assert
        Assert.AreEqual("WBA3A5C50DF123456", Encoding.ASCII.GetString(data));
        Assert.AreEqual(1, mockConn.SentFrames.Count);
        CollectionAssert.AreEqual(new byte[] { 0x22, 0xF1, 0x90 }, mockConn.SentFrames[0].Payload);
    }

    [TestMethod]
    public async Task SendAsync_NegativeResponse_ThrowsUdsNegativeResponseException()
    {
        // Arrange
        var mockConn = new MockHsfzConnection();
        var client = new UdsClient(mockConn);

        // Negative response for write (0x2E) with NRC 0x33 (SecurityAccessDenied)
        byte[] pdu = new byte[] { 0x7F, 0x2E, (byte)Nrc.SecurityAccessDenied };
        mockConn.EnqueueResponse(new HsfzFrame(0xF4, pdu, sourceAddress: 0x40));

        // Act & Assert
        var ex = await Assert.ThrowsExceptionAsync<UdsNegativeResponseException>(async () =>
        {
            await client.WriteDataByIdentifierAsync(0x40, 0x3000, new byte[] { 0x01, 0x02 });
        });

        Assert.AreEqual(Nrc.SecurityAccessDenied, ex.Nrc);
        Assert.AreEqual(0x40, ex.TargetAddress);
        Assert.AreEqual(0x2E, ex.RequestedSid);
    }

    [DataTestMethod]
    [DataRow("62F18C3031")]
    [DataRow("62F1")]
    [DataRow("6EF19000")]
    public async Task ReadDataByIdentifier_RejectsMismatchedOrTruncatedResponse(string response)
    {
        using var connection = new MockHsfzConnection();
        connection.EnqueueResponse(new HsfzFrame(0xF4, Convert.FromHexString(response), 0x10));
        using var client = new UdsClient(connection);
        await Assert.ThrowsExceptionAsync<FormatException>(() => client.ReadDataByIdentifierAsync(0x10, 0xF190));
    }

    [TestMethod]
    public async Task SendAsync_ResponsePending_WaitsWithoutResending()
    {
        // Arrange
        var mockConn = new MockHsfzConnection();
        var client = new UdsClient(mockConn);

        // 1st response: 7F 22 78 (RequestCorrectlyReceivedResponsePending)
        mockConn.EnqueueResponse(new HsfzFrame(0xF4, new byte[] { 0x7F, 0x22, 0x78 }, sourceAddress: 0x63));
        // 2nd response: 62 30 00 11 22 33 (Positive RDBI)
        mockConn.EnqueueResponse(new HsfzFrame(0xF4, new byte[] { 0x62, 0x30, 0x00, 0x11, 0x22, 0x33 }, sourceAddress: 0x63));

        // Act
        byte[] data = await client.ReadDataByIdentifierAsync(0x63, 0x3000);

        // Assert
        CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x33 }, data);
        Assert.AreEqual(1, mockConn.SentFrames.Count);
    }

    [TestMethod]
    public async Task SendRawAsync_ResponsePendingThenNegativeReturnsFinalResponseWithoutResending()
    {
        var connection = new MockHsfzConnection();
        connection.EnqueueResponse(new HsfzFrame(0xF4, Convert.FromHexString("7F2278"), 0x63));
        connection.EnqueueResponse(new HsfzFrame(0xF4, Convert.FromHexString("7F2231"), 0x63));
        using var client = new UdsClient(connection);
        var response = await client.SendRawAsync(0x63, Convert.FromHexString("22F190"));
        Assert.AreEqual(Nrc.RequestOutOfRange, response.Nrc);
        Assert.AreEqual(1, connection.SentFrames.Count);
    }

    [TestMethod]
    public async Task KeepAlive_DispatchesTesterPresentPeriodically()
    {
        // Arrange
        var mockConn = new MockHsfzConnection();
        var client = new UdsClient(mockConn);

        // Enqueue several positive responses for TesterPresent (7E 00)
        for (int i = 0; i < 5; i++)
        {
            mockConn.EnqueueResponse(new HsfzFrame(0xF4, new byte[] { 0x7E, 0x00 }, sourceAddress: 0x40));
        }

        // Act
        client.StartKeepAlive(0x40, interval: TimeSpan.FromMilliseconds(50));
        await Task.Delay(180);
        client.StopKeepAlive();

        // Assert
        Assert.IsTrue(mockConn.SentFrames.Count >= 2, $"Expected at least 2 keepalive frames, got {mockConn.SentFrames.Count}");
        Assert.AreEqual(0x40, mockConn.SentFrames[0].TargetAddress);
        CollectionAssert.AreEqual(new byte[] { 0x3E, 0x00 }, mockConn.SentFrames[0].Payload);
    }
}
