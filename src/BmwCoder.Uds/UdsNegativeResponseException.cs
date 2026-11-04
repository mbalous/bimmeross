using System;

namespace BmwCoder.Uds;

public class UdsNegativeResponseException : Exception
{
    public byte TargetAddress { get; }
    public byte RequestedSid { get; }
    public Nrc Nrc { get; }

    public UdsNegativeResponseException(byte targetAddress, byte requestedSid, Nrc nrc)
        : base($"UDS Negative Response from 0x{targetAddress:X2} for SID 0x{requestedSid:X2}: {nrc} (0x{(byte)nrc:X2}). {NrcDescriptions.Describe(nrc)}")
    {
        TargetAddress = targetAddress;
        RequestedSid = requestedSid;
        Nrc = nrc;
    }
}
