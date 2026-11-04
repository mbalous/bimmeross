using System;
using System.Threading;
using System.Threading.Tasks;

namespace BmwCoder.Uds;

public interface IUdsClient : IDisposable
{
    Task<UdsResponse> SendRawAsync(byte target, byte[] pdu, CancellationToken cancellationToken = default);
    Task<UdsResponse> SendAsync(UdsRequest request, CancellationToken cancellationToken = default);

    Task DiagnosticSessionControlAsync(byte target, byte sessionType, CancellationToken cancellationToken = default);
    Task<byte[]> ReadDataByIdentifierAsync(byte target, ushort did, CancellationToken cancellationToken = default);
    Task WriteDataByIdentifierAsync(byte target, ushort did, byte[] data, CancellationToken cancellationToken = default);
    Task RoutineControlAsync(byte target, byte routineControlType, ushort routineId, byte[]? optionRecord = null, CancellationToken cancellationToken = default);
    Task EcuResetAsync(byte target, byte resetType, CancellationToken cancellationToken = default);
    Task TesterPresentAsync(byte target, CancellationToken cancellationToken = default);

    void StartKeepAlive(byte target, TimeSpan? interval = null);
    void StopKeepAlive();
}
