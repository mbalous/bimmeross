using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BmwCoder.Transport;

/// <summary>
/// Manages a TCP connection to a BMW gateway over ENET (port 6801) and exchanges HSFZ frames.
/// </summary>
public sealed class HsfzConnection : IHsfzConnection
{
    private readonly ILogger<HsfzConnection> _logger;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsConnected => _tcpClient != null && _tcpClient.Connected && _stream != null;

    public HsfzConnection(ILogger<HsfzConnection>? logger = null)
    {
        _logger = logger ?? NullLogger<HsfzConnection>.Instance;
    }

    public async Task ConnectAsync(string host, int port = 6801, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Close();

        var tcpClient = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            SendTimeout = (int)Timeout.TotalMilliseconds
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(Timeout);

        try
        {
            _logger.LogDebug("Connecting TCP socket to {Host}:{Port} with timeout {Timeout}s...", host, port, Timeout.TotalSeconds);
            await tcpClient.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();
            _logger.LogDebug("TCP socket connected to {Host}:{Port}.", host, port);
        }
        catch (OperationCanceledException)
        {
            tcpClient.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Connection to {Host}:{Port} timed out.", host, port);
            throw new TimeoutException($"Connection to {host}:{port} timed out after {Timeout.TotalSeconds}s.");
        }
        catch (Exception ex)
        {
            tcpClient.Dispose();
            _logger.LogError(ex, "Failed to connect to {Host}:{Port}.", host, port);
            throw new HsfzException($"Failed to connect to {host}:{port}: {ex.Message}", ex);
        }
    }

    public async Task<HsfzFrame> SendAsync(HsfzFrame request, CancellationToken cancellationToken = default,
        Func<HsfzFrame, bool>? acceptResponse = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected || _stream == null)
        {
            throw new HsfzException("Connection is not open.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(Timeout);

        await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            byte[] wireBytes = request.ToByteArray();
            await _stream.WriteAsync(wireBytes, linkedCts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            // Read response loop: gateway may send 0x0002 (acknowledge_transfer), followed by 0x0001 (response)
            while (true)
            {
                HsfzFrame responseFrame = await ReadFrameAsync(_stream, linkedCts.Token).ConfigureAwait(false);

                // If this is an echo/ack packet (0x0002), continue reading the actual diagnostic response
                if (responseFrame.ControlWord == HsfzFrame.ControlAcknowledgeTransfer)
                {
                    continue;
                }

                if (responseFrame.ControlWord != HsfzFrame.ControlDiagnosticReqRes)
                {
                    throw new HsfzException(
                        $"Gateway returned HSFZ control 0x{responseFrame.ControlWord:X4} for ECU 0x{request.TargetAddress:X2}; expected a diagnostic response.");
                }

                if (responseFrame.SourceAddress != request.TargetAddress ||
                    responseFrame.TargetAddress != request.SourceAddress)
                {
                    _logger.LogDebug("Ignoring unrelated HSFZ response: {Frame}", responseFrame);
                    continue;
                }

                if (responseFrame.Payload.Length == 0)
                {
                    throw new HsfzException($"Empty diagnostic response from ECU 0x{responseFrame.SourceAddress:X2}.");
                }

                if (acceptResponse == null || acceptResponse(responseFrame))
                    return responseFrame;
                _logger.LogDebug("Waiting for final diagnostic response after intermediate frame: {Frame}", responseFrame);
            }
        }
        catch (OperationCanceledException)
        {
            Close();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"HSFZ request timed out after {Timeout.TotalSeconds}s.");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Close();
            throw new HsfzException($"HSFZ exchange failed: {ex.Message}", ex);
        }
        catch (HsfzException)
        {
            Close();
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<HsfzFrame> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // 1. Read 4 bytes length
        byte[] lengthHeader = new byte[4];
        await ReadExactAsync(stream, lengthHeader, cancellationToken).ConfigureAwait(false);

        uint lengthField = BinaryPrimitives.ReadUInt32BigEndian(lengthHeader);
        if (lengthField < 2)
        {
            throw new HsfzException($"Malformed HSFZ frame: length field {lengthField} is less than 2.");
        }
        if (lengthField > 0x10000) // 64 KB safety limit for MVP
        {
            throw new HsfzException($"HSFZ frame exceeds maximum safety limit: {lengthField} bytes.");
        }

        // 2. Total wire frame = 4 (length field already read) + 2 (control) + lengthField (src, dst, payload)
        int remainingBytesCount = 2 + (int)lengthField;
        byte[] fullFrame = new byte[4 + remainingBytesCount];
        lengthHeader.CopyTo(fullFrame, 0);

        await ReadExactAsync(stream, fullFrame.AsMemory(4, remainingBytesCount), cancellationToken).ConfigureAwait(false);

        return HsfzFrame.FromBytes(fullFrame);
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int bytesReadTotal = 0;
        while (bytesReadTotal < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(bytesReadTotal), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new HsfzException("Remote host closed the connection unexpectedly while reading frame.");
            }
            bytesReadTotal += read;
        }
    }

    public void Close()
    {
        if (_stream != null)
        {
            try { _stream.Dispose(); } catch { /* ignore */ }
            _stream = null;
        }

        if (_tcpClient != null)
        {
            try { _tcpClient.Dispose(); } catch { /* ignore */ }
            _tcpClient = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _sendLock.Dispose();
    }
}
