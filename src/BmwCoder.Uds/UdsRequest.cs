using System;

namespace BmwCoder.Uds;

public sealed class UdsRequest
{
    public byte TargetAddress { get; }
    public byte Sid { get; }
    public byte[] Data { get; }

    public UdsRequest(byte targetAddress, byte sid, byte[]? data = null)
    {
        TargetAddress = targetAddress;
        Sid = sid;
        Data = data ?? Array.Empty<byte>();
    }

    public byte[] ToPdu()
    {
        byte[] pdu = new byte[1 + Data.Length];
        pdu[0] = Sid;
        if (Data.Length > 0)
        {
            Data.CopyTo(pdu.AsSpan(1));
        }
        return pdu;
    }
}
