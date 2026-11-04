using System;
using System.Threading;
using System.Threading.Tasks;

namespace BmwCoder.Transport;

public interface IHsfzConnection : IDisposable
{
    bool IsConnected { get; }
    TimeSpan Timeout { get; set; }

    Task ConnectAsync(string host, int port = 6801, CancellationToken cancellationToken = default);
    Task<HsfzFrame> SendAsync(HsfzFrame request, CancellationToken cancellationToken = default,
        Func<HsfzFrame, bool>? acceptResponse = null);
    void Close();
}
