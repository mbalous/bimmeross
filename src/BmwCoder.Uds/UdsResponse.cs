using System;

namespace BmwCoder.Uds;

public sealed class UdsResponse
{
    public byte SourceAddress { get; }
    public bool IsPositive { get; }
    public byte ResponseSid { get; }
    public byte OriginalSid { get; }
    public Nrc? Nrc { get; }
    public byte[] Data { get; }

    public UdsResponse(byte sourceAddress, bool isPositive, byte responseSid, byte originalSid, Nrc? nrc, byte[] data)
    {
        SourceAddress = sourceAddress;
        IsPositive = isPositive;
        ResponseSid = responseSid;
        OriginalSid = originalSid;
        Nrc = nrc;
        Data = data ?? Array.Empty<byte>();
    }

    public static UdsResponse FromPdu(byte sourceAddress, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0)
        {
            throw new ArgumentException("UDS PDU cannot be empty.", nameof(pdu));
        }

        byte firstByte = pdu[0];

        // Negative response: 0x7F <SID> <NRC>
        if (firstByte == 0x7F)
        {
            if (pdu.Length < 3)
            {
                throw new ArgumentException("Invalid negative response PDU: expected at least 3 bytes.", nameof(pdu));
            }

            byte failedSid = pdu[1];
            Nrc nrc = (Nrc)pdu[2];

            return new UdsResponse(
                sourceAddress: sourceAddress,
                isPositive: false,
                responseSid: 0x7F,
                originalSid: failedSid,
                nrc: nrc,
                data: pdu.Length > 3 ? pdu.Slice(3).ToArray() : Array.Empty<byte>()
            );
        }

        // Positive response: firstByte = requestedSid + 0x40
        byte originalSid = (byte)(firstByte >= 0x40 ? firstByte - 0x40 : firstByte);
        byte[] data = pdu.Length > 1 ? pdu.Slice(1).ToArray() : Array.Empty<byte>();

        return new UdsResponse(
            sourceAddress: sourceAddress,
            isPositive: true,
            responseSid: firstByte,
            originalSid: originalSid,
            nrc: null,
            data: data
        );
    }
}
