using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BmwCoder.Transport;

public sealed record EnetInterface(string Name, IPAddress Address, IPAddress Broadcast)
{
    public override string ToString() => $"{Name}: {Address}";
}

public sealed record EnetGateway(IPAddress Address, string IdentificationHex);
public sealed record EnetDiscoveryResult(EnetGateway[] Gateways, string[] Warnings);

public sealed class EnetDiscovery
{
    private readonly int _port;
    public EnetDiscovery(int port = 6811)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    public static EnetInterface[] GetInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new EnetInterface(n.Name, a.Address, BroadcastFor(a.Address, a.IPv4Mask))))
        .OrderByDescending(n => n.Address.GetAddressBytes()[0] == 169 && n.Address.GetAddressBytes()[1] == 254)
        .ThenBy(n => n.Name, StringComparer.Ordinal).ToArray();

    public static IPAddress BroadcastFor(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("ENET discovery requires IPv4.");
        byte[] bytes = address.GetAddressBytes(), masks = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            bytes[i] |= (byte)~masks[i];
        return new IPAddress(bytes);
    }

    public async Task<EnetDiscoveryResult> DiscoverAsync(IPAddress localAddress, IPAddress destination,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || destination.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("ENET discovery requires IPv4.");
        if (localAddress.Equals(IPAddress.Any) || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Select a local interface address and a discovery duration of at most 30 seconds.");
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        using var udp = new UdpClient(new IPEndPoint(localAddress, 0)) { EnableBroadcast = true };
        var results = new Dictionary<IPAddress, EnetGateway>();
        var warnings = new List<string>();
        byte[] request = { 0, 0, 0, 0, 0, 0x11 };
        try
        {
            await udp.SendAsync(request, new IPEndPoint(destination, _port), deadline.Token).ConfigureAwait(false);
            while (true)
            {
                var reply = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (reply.RemoteEndPoint.Port != _port)
                {
                    if (warnings.Count < 20)
                        warnings.Add($"Ignored identification from unexpected port {reply.RemoteEndPoint.Port}.");
                    continue;
                }
                try
                {
                    var gateway = ParseIdentification(reply.Buffer, reply.RemoteEndPoint.Address);
                    if (!results.ContainsKey(gateway.Address) && results.Count >= 256)
                        throw new InvalidOperationException("More than 256 gateway addresses responded. Discovery aborted; verify the selected network.");
                    if (results.TryGetValue(gateway.Address, out var previous) && previous != gateway && warnings.Count < 20)
                        warnings.Add($"Different identification payloads were received from {gateway.Address}; verify VIN after connecting.");
                    results.TryAdd(gateway.Address, gateway);
                }
                catch (HsfzException ex)
                {
                    if (warnings.Count < 20)
                        warnings.Add($"Ignored identification from {reply.RemoteEndPoint.Address}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EnetDiscoveryResult(results.Values.ToArray(), warnings.ToArray());
        }
    }

    public static EnetGateway ParseIdentification(ReadOnlySpan<byte> bytes, IPAddress address)
    {
        if (bytes.Length <= 6 || bytes.Length > 4096)
            throw new HsfzException("Invalid identification frame size.");
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes) != bytes.Length - 6 ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]) != 0x11)
            throw new HsfzException("Invalid identification length or control word.");
        return new EnetGateway(address, Convert.ToHexString(bytes[6..]));
    }
}
