using System;

namespace BmwCoder.Coding.Checksum;

/// <summary>
/// 8-bit additive sum (modulo 256) checksum implementation.
/// </summary>
public sealed class Sum8Checksum : IChecksum
{
    public string Name => "Sum-8";

    public byte Compute(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            sum = (byte)(sum + data[i]);
        }
        return sum;
    }

    public void Apply(Span<byte> block, ChecksumRange range)
    {
        if (range.DataStart + range.DataLength > block.Length || range.ChecksumOffset + 1 > block.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Checksum range exceeds block boundaries.");
        }

        byte computed = Compute(block.Slice(range.DataStart, range.DataLength));
        block[range.ChecksumOffset] = computed;
    }

    public bool Verify(ReadOnlySpan<byte> block, ChecksumRange range)
    {
        if (range.DataStart + range.DataLength > block.Length || range.ChecksumOffset + 1 > block.Length)
        {
            return false;
        }

        byte computed = Compute(block.Slice(range.DataStart, range.DataLength));
        return computed == block[range.ChecksumOffset];
    }
}
