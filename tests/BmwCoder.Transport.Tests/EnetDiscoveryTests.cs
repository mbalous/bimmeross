using System.Net;
using System.Net.Sockets;
using BmwCoder.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Transport.Tests;

[TestClass]
public sealed class EnetDiscoveryTests
{
    [TestMethod]
    public async Task DiscoverySendsOnlyIdentificationAndDeduplicatesValidatedReplies()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Respond()
        {
            var request = await server.ReceiveAsync(deadline.Token);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 0, 0x11 }, request.Buffer);
            byte[] malformed = { 0, 0, 0, 1, 0, 1, 0x42 };
            byte[] identification = { 0, 0, 0, 1, 0, 0x11, 0x42 };
            await server.SendAsync(malformed, request.RemoteEndPoint, deadline.Token);
            await server.SendAsync(identification, request.RemoteEndPoint, deadline.Token);
            await server.SendAsync(identification, request.RemoteEndPoint, deadline.Token);
        }
        Task response = Respond();
        var discovery = new EnetDiscovery(((IPEndPoint)server.Client.LocalEndPoint!).Port);
        var found = await discovery.DiscoverAsync(IPAddress.Loopback, IPAddress.Loopback,
            TimeSpan.FromMilliseconds(300), deadline.Token);
        await response;
        Assert.AreEqual(1, found.Gateways.Length);
        Assert.AreEqual("42", found.Gateways[0].IdentificationHex);
        Assert.AreEqual(1, found.Warnings.Length);
    }

    [TestMethod]
    public async Task DiscoveryHonorsCallerCancellationAndValidatesFrameLength()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var discovery = new EnetDiscovery(((IPEndPoint)server.Client.LocalEndPoint!).Port);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            discovery.DiscoverAsync(IPAddress.Loopback, IPAddress.Loopback, TimeSpan.FromSeconds(1), canceled.Token));
        Assert.ThrowsException<HsfzException>(() =>
            EnetDiscovery.ParseIdentification(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0x11, 1 }, IPAddress.Loopback));
        Assert.AreEqual(IPAddress.Parse("169.254.255.255"),
            EnetDiscovery.BroadcastFor(IPAddress.Parse("169.254.1.1"), IPAddress.Parse("255.255.0.0")));
    }
}
