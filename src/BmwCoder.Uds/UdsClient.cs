using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using BmwCoder.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BmwCoder.Uds;

public sealed class UdsClient : IUdsClient
{
    private readonly IHsfzConnection _connection;
    private readonly byte _testerAddress;
    private readonly ILogger<UdsClient> _logger;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    private bool _disposed;

    public UdsClient(IHsfzConnection connection, byte testerAddress = HsfzFrame.DefaultTesterAddress, ILogger<UdsClient>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _testerAddress = testerAddress;
        _logger = logger ?? NullLogger<UdsClient>.Instance;
    }

    public async Task<UdsResponse> SendRawAsync(byte target, byte[] pdu, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pdu);
        if (pdu.Length == 0)
            throw new ArgumentException("UDS request PDU cannot be empty.", nameof(pdu));

        var requestFrame = new HsfzFrame(target, pdu, _testerAddress);
        var responseFrame = await _connection.SendAsync(requestFrame, cancellationToken,
            frame => !(frame.Payload.Length == 3 && frame.Payload[0] == 0x7F &&
                frame.Payload[1] == pdu[0] && frame.Payload[2] == (byte)Nrc.RequestCorrectlyReceivedResponsePending)).ConfigureAwait(false);

        return UdsResponse.FromPdu(responseFrame.SourceAddress, responseFrame.Payload);
    }

    public async Task<UdsResponse> SendAsync(UdsRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] pdu = request.ToPdu();

        var response = await SendRawAsync(request.TargetAddress, pdu, cancellationToken).ConfigureAwait(false);
        if (!response.IsPositive)
            throw new UdsNegativeResponseException(request.TargetAddress, request.Sid, response.Nrc ?? Nrc.GeneralReject);
        return response;
    }

    public async Task DiagnosticSessionControlAsync(byte target, byte sessionType, CancellationToken cancellationToken = default)
    {
        var request = new UdsRequest(target, 0x10, new[] { sessionType });
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadDataByIdentifierAsync(byte target, ushort did, CancellationToken cancellationToken = default)
    {
        byte[] didBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(didBytes, did);

        var request = new UdsRequest(target, 0x22, didBytes);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.ResponseSid != 0x62 || response.Data.Length < 2 ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Data.AsSpan(0, 2)) != did)
            throw new FormatException($"Invalid ReadDataByIdentifier response from ECU 0x{target:X2}: expected service 0x62 and DID 0x{did:X4}.");

        return response.Data.AsSpan(2).ToArray();
    }

    public async Task WriteDataByIdentifierAsync(byte target, ushort did, byte[] data, CancellationToken cancellationToken = default)
    {
        byte[] payload = new byte[2 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), did);
        data.CopyTo(payload.AsSpan(2));

        var request = new UdsRequest(target, 0x2E, payload);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task RoutineControlAsync(byte target, byte routineControlType, ushort routineId, byte[]? optionRecord = null, CancellationToken cancellationToken = default)
    {
        int recordLen = optionRecord?.Length ?? 0;
        byte[] payload = new byte[3 + recordLen];
        payload[0] = routineControlType;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1, 2), routineId);
        if (recordLen > 0 && optionRecord != null)
        {
            optionRecord.CopyTo(payload.AsSpan(3));
        }

        var request = new UdsRequest(target, 0x31, payload);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task EcuResetAsync(byte target, byte resetType, CancellationToken cancellationToken = default)
    {
        var request = new UdsRequest(target, 0x11, new[] { resetType });
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task TesterPresentAsync(byte target, CancellationToken cancellationToken = default)
    {
        var request = new UdsRequest(target, 0x3E, new byte[] { 0x00 });
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void StartKeepAlive(byte target, TimeSpan? interval = null)
    {
        StopKeepAlive();

        var timeInterval = interval ?? TimeSpan.FromSeconds(2);
        _keepAliveCts = new CancellationTokenSource();
        var token = _keepAliveCts.Token;

        _keepAliveTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(timeInterval);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
                    await TesterPresentAsync(target, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Suppress transient keep-alive send errors to prevent unhandled background task crashes
                }
            }
        }, token);
    }

    public void StopKeepAlive()
    {
        if (_keepAliveCts != null)
        {
            _keepAliveCts.Cancel();
            _keepAliveCts.Dispose();
            _keepAliveCts = null;
        }
        _keepAliveTask = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopKeepAlive();
    }
}
