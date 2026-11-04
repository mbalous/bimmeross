using System;

namespace BmwCoder.Coding.Checksum;

public interface IChecksum
{
    string Name { get; }

    /// <summary>
    /// Computes the checksum over the configured data range and writes it into the block at ChecksumOffset.
    /// </summary>
    void Apply(Span<byte> block, ChecksumRange range);

    /// <summary>
    /// Verifies that the checksum at ChecksumOffset matches the computed checksum over the data range.
    /// </summary>
    bool Verify(ReadOnlySpan<byte> block, ChecksumRange range);
}
