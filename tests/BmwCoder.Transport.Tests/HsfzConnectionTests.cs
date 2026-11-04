using System.Net;
using System.Net.Sockets;
using BmwCoder.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Transport.Tests;

[TestClass]
public class HsfzConnectionTests
{
    [DataTestMethod]
    [DataRow(0x0040, "HSFZ control 0x0040")]
    [DataRow(0x0001, "Empty diagnostic response")]
    public async Task SendAsync_InvalidResponseIsTransportError(int control, string expectedMessage)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task server = RespondAsync(listener, deadline.Token,
            new HsfzFrame(0xF4, Array.Empty<byte>(), 0x64, (ushort)control));
        using var connection = new HsfzConnection();
        await connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);

        var error = await Assert.ThrowsExceptionAsync<HsfzException>(() =>
            connection.SendAsync(new HsfzFrame(0x64, new byte[] { 0x3E, 0x00 }), deadline.Token));

        StringAssert.Contains(error.Message, expectedMessage);
        await server;
    }

    [TestMethod]
    public async Task SendAsync_SkipsAcknowledgementAndUnrelatedResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task server = RespondAsync(listener, deadline.Token,
            new HsfzFrame(0xF4, Array.Empty<byte>(), 0x63, HsfzFrame.ControlAcknowledgeTransfer),
            new HsfzFrame(0xF4, new byte[] { 0x7E, 0x00 }, 0x20),
            new HsfzFrame(0xF4, new byte[] { 0x7E, 0x00 }, 0x63));
        using var connection = new HsfzConnection();
        await connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);

        var response = await connection.SendAsync(
            new HsfzFrame(0x63, new byte[] { 0x3E, 0x00 }), deadline.Token);

        Assert.AreEqual((byte)0x63, response.SourceAddress);
        CollectionAssert.AreEqual(new byte[] { 0x7E, 0x00 }, response.Payload);
        await server;
    }

    private static async Task RespondAsync(TcpListener listener, CancellationToken cancellationToken,
        params HsfzFrame[] frames)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        await stream.ReadExactlyAsync(new byte[10], cancellationToken);
        foreach (var frame in frames)
        {
            await stream.WriteAsync(frame.ToByteArray(), cancellationToken);
        }
    }

    [TestMethod]
    public async Task SendAsync_FiltersIntermediateFramesWithinOneExchange()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task server = RespondAsync(listener, deadline.Token,
            new HsfzFrame(0xF4, new byte[] { 0x7F, 0x3E, 0x78 }, 0x63),
            new HsfzFrame(0xF4, new byte[] { 0x7F, 0x3E, 0x78 }, 0x63),
            new HsfzFrame(0xF4, new byte[] { 0x7E, 0x00 }, 0x63));
        using var connection = new HsfzConnection();
        await connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        var response = await connection.SendAsync(new HsfzFrame(0x63, new byte[] { 0x3E, 0x00 }),
            deadline.Token, frame => frame.Payload[0] == 0x7E);
        CollectionAssert.AreEqual(new byte[] { 0x7E, 0x00 }, response.Payload);
        await server;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SendAsync_IntermediateResponseWaitIsBoundedAndClosesTimedOutStream(bool callerCancels)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Server()
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[10], deadline.Token);
            await stream.WriteAsync(new HsfzFrame(0xF4, new byte[] { 0x7F, 0x3E, 0x78 }, 0x63).ToByteArray(), deadline.Token);
            int extraBytes = await stream.ReadAsync(new byte[1], deadline.Token);
            Assert.AreEqual(0, extraBytes, "The client must close, not resend the request.");
        }
        Task server = Server();
        using var connection = new HsfzConnection
        {
            Timeout = callerCancels ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(200)
        };
        await connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        if (callerCancels) cancellation.CancelAfter(100);
        Task<HsfzFrame> exchange = connection.SendAsync(new HsfzFrame(0x63, new byte[] { 0x3E, 0x00 }),
            cancellation.Token, _ => false);
        if (callerCancels)
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await exchange);
        else
            await Assert.ThrowsExceptionAsync<TimeoutException>(async () => await exchange);
        Assert.IsFalse(connection.IsConnected);
        await server;
    }
}
