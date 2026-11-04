using System;
using System.Buffers.Binary;

namespace BmwCoder.Transport;

/// <summary>
/// Represents a framed HSFZ (High-Speed-Fahrzeug-Zugang) message over ENET (TCP 6801).
/// 
/// Framing layout (verified against Scapy BMW HSFZ module and BMW ENET captures):
/// [Bytes 0..3] Big-Endian uint32: Length = 2 + Payload.Length (encompassing SourceAddress, TargetAddress, and Payload)
/// [Bytes 4..5] Big-Endian uint16: ControlWord (0x0001 = Diagnostic Req/Res, 0x0002 = Gateway Transfer Acknowledge)
/// [Byte 6]     uint8: SourceAddress (Default 0xF4 for diagnostic tester)
/// [Byte 7]     uint8: TargetAddress (ECU diagnostic address, e.g., 0x10 Gateway, 0x40 CAS, 0x60 KOMBI, 0x63 HU_NBT)
/// [Bytes 8..N] uint8[]: Payload (UDS PDU, length = Length - 2)
/// Total wire bytes = 8 + Payload.Length.
/// </summary>
public sealed class HsfzFrame : IEquatable<HsfzFrame>
{
    public const byte DefaultTesterAddress = 0xF4;
    public const ushort ControlDiagnosticReqRes = 0x0001;
    public const ushort ControlAcknowledgeTransfer = 0x0002;
    public const ushort ControlAliveCheck = 0x0012;

    public ushort ControlWord { get; init; } = ControlDiagnosticReqRes;
    public byte SourceAddress { get; init; } = DefaultTesterAddress;
    public byte TargetAddress { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public HsfzFrame()
    {
    }

    public HsfzFrame(byte targetAddress, byte[] payload, byte sourceAddress = DefaultTesterAddress, ushort controlWord = ControlDiagnosticReqRes)
    {
        TargetAddress = targetAddress;
        Payload = payload ?? Array.Empty<byte>();
        SourceAddress = sourceAddress;
        ControlWord = controlWord;
    }

    /// <summary>
    /// Serializes this frame into wire bytes.
    /// </summary>
    public byte[] ToByteArray()
    {
        int payloadLen = Payload.Length;
        uint lengthField = (uint)(2 + payloadLen); // 1 byte src + 1 byte dst + payload

        byte[] buffer = new byte[8 + payloadLen];

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), lengthField);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), ControlWord);
        buffer[6] = SourceAddress;
        buffer[7] = TargetAddress;

        if (payloadLen > 0)
        {
            Payload.CopyTo(buffer.AsSpan(8));
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes a frame from wire bytes.
    /// </summary>
    public static HsfzFrame FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new HsfzException($"Frame data is too short: {data.Length} bytes (minimum 8 required).");
        }

        uint lengthField = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
        if (lengthField < 2)
        {
            throw new HsfzException($"Invalid HSFZ length field: {lengthField}. Must be at least 2 bytes (src + dst).");
        }

        int expectedTotalBytes = 6 + (int)lengthField;
        if (data.Length < expectedTotalBytes)
        {
            throw new HsfzException($"Incomplete frame: expected {expectedTotalBytes} bytes, got {data.Length} bytes.");
        }

        ushort control = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        byte src = data[6];
        byte dst = data[7];

        int payloadLen = (int)lengthField - 2;
        byte[] payload = payloadLen > 0 ? data.Slice(8, payloadLen).ToArray() : Array.Empty<byte>();

        return new HsfzFrame
        {
            ControlWord = control,
            SourceAddress = src,
            TargetAddress = dst,
            Payload = payload
        };
    }

    public override string ToString()
    {
        return $"HSFZ[Ctrl=0x{ControlWord:X4}, Src=0x{SourceAddress:X2}, Dst=0x{TargetAddress:X2}, PayloadLen={Payload.Length}]";
    }

    public bool Equals(HsfzFrame? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ControlWord == other.ControlWord &&
               SourceAddress == other.SourceAddress &&
               TargetAddress == other.TargetAddress &&
               Payload.AsSpan().SequenceEqual(other.Payload);
    }

    public override bool Equals(object? obj) => Equals(obj as HsfzFrame);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ControlWord);
        hash.Add(SourceAddress);
        hash.Add(TargetAddress);
        hash.Add(Payload.Length);
        return hash.ToHashCode();
    }
}
