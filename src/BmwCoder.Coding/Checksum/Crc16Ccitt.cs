using System;
using System.Buffers.Binary;

namespace BmwCoder.Coding.Checksum;

/// <summary>
/// CRC-16-CCITT implementation (polynomial 0x1021) with configurable initial value.
/// </summary>
public sealed class Crc16Ccitt : IChecksum
{
    private readonly ushort _initialValue;
    private readonly ushort[] _table = new ushort[256];

    public string Name => $"CRC16-CCITT (Init 0x{_initialValue:X4})";

    public Crc16Ccitt(ushort initialValue = 0xFFFF)
    {
        _initialValue = initialValue;
        InitializeTable(0x1021);
    }

    private void InitializeTable(ushort poly)
    {
        for (int i = 0; i < 256; i++)
        {
            ushort temp = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((temp & 0x8000) != 0)
                {
                    temp = (ushort)((temp << 1) ^ poly);
                }
                else
                {
                    temp <<= 1;
                }
            }
            _table[i] = temp;
        }
    }

    public ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = _initialValue;
        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)((crc >> 8) ^ data[i]);
            crc = (ushort)((crc << 8) ^ _table[index]);
        }
        return crc;
    }

    public void Apply(Span<byte> block, ChecksumRange range)
    {
        if (range.DataStart + range.DataLength > block.Length || range.ChecksumOffset + 2 > block.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Checksum range exceeds block boundaries.");
        }

        var dataSpan = block.Slice(range.DataStart, range.DataLength);
        ushort computed = Compute(dataSpan);

        BinaryPrimitives.WriteUInt16BigEndian(block.Slice(range.ChecksumOffset, 2), computed);
    }

    public bool Verify(ReadOnlySpan<byte> block, ChecksumRange range)
    {
        if (range.DataStart + range.DataLength > block.Length || range.ChecksumOffset + 2 > block.Length)
        {
            return false;
        }

        var dataSpan = block.Slice(range.DataStart, range.DataLength);
        ushort computed = Compute(dataSpan);
        ushort current = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(range.ChecksumOffset, 2));

        return computed == current;
    }
}
