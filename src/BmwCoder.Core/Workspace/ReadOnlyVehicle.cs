using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Transport;

namespace BmwCoder.Core.Workspace;

public sealed class ReadOnlyVehicle : IReadOnlyVehicle
{
    private readonly CodingSession _session;
    public bool IsDemo => false;
    public bool IsConnected => _session.Connection.IsConnected;
    public string? Vin { get; private set; }
    public IReadOnlyList<string> Warnings => _session.DiscoveryWarnings;

    public ReadOnlyVehicle() : this(new CodingSession()) { }

    public ReadOnlyVehicle(CodingSession session) => _session = session;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A host is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        Disconnect();
        try
        {
            await _session.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var reading = await _session.ReadDiagnosticDidAsync(CodingSession.GatewayAddress,
                CodingSession.VinDid, cancellationToken).ConfigureAwait(false);
            if (reading.Status != "ok")
                throw new HsfzException($"VIN read failed: {reading.Error}");
            string vin = System.Text.Encoding.ASCII.GetString(Convert.FromHexString(reading.PayloadHex)).Trim('\0', ' ');
            if (vin.Length != 17 || vin.Any(c => !char.IsAsciiLetterOrDigit(c) || "IOQioq".Contains(c)))
                throw new FormatException("Gateway did not return a valid 17-character VIN. Connection closed.");
            Vin = vin;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public async Task<IReadOnlyList<EcuInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        RequireConnection();
        var ecus = await _session.DiscoverEcusAsync(cancellationToken: cancellationToken,
            probeTimeout: TimeSpan.FromSeconds(1), discoveryBudget: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return ecus.Select(ecu => new EcuInfo
        {
            Address = ecu.Address, Name = ecu.Name, Description = ecu.Description,
            Responds = ecu.Responds, RttMs = ecu.RttMs, DiscoverySource = ecu.DiscoverySource,
            NameSource = ecu.NameSource, ProbeError = ecu.ProbeError,
            SuggestedDids = ecu.SuggestedDids.ToArray(), IsWritable = false
        }).ToArray();
    }

    public Task<DiagnosticReading> ReadDidAsync(byte ecu, ushort did, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ReadDiagnosticDidAsync(ecu, did, cancellationToken);
    }

    public Task<DiagnosticReading> ReadFaultsAsync(byte ecu, CancellationToken cancellationToken)
    {
        RequireConnection();
        return _session.ReadFaultMemoryAsync(ecu, cancellationToken);
    }

    public void Disconnect()
    {
        _session.Close();
        Vin = null;
    }

    public void Dispose()
    {
        Disconnect();
        _session.Dispose();
    }

    private void RequireConnection()
    {
        if (!IsConnected || Vin == null)
            throw new InvalidOperationException("Connect and verify vehicle identity first.");
    }
}
