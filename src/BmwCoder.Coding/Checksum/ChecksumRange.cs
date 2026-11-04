namespace BmwCoder.Coding.Checksum;

/// <summary>
/// Defines the data range and destination offset for a checksum in a coding block.
/// </summary>
/// <param name="DataStart">Starting offset of bytes included in checksum calculation.</param>
/// <param name="DataLength">Number of bytes included in checksum calculation.</param>
/// <param name="ChecksumOffset">Offset in the block where the checksum value is stored.</param>
/// <param name="ChecksumSize">Size in bytes of the checksum value (e.g. 1 or 2).</param>
public readonly record struct ChecksumRange(int DataStart, int DataLength, int ChecksumOffset, int ChecksumSize);
