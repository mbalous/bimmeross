using System;
using System.Security.Cryptography;

namespace BmwCoder.Coding;

/// <summary>
/// Represents a raw ECU coding block read via UDS ReadDataByIdentifier (0x22).
/// </summary>
public sealed class CodingBlock
{
    public byte EcuAddress { get; init; }
    public ushort Did { get; init; }
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public DateTime ReadAt { get; init; } = DateTime.UtcNow;
    public string? Vin { get; set; }

    public CodingBlock()
    {
    }

    public CodingBlock(byte ecuAddress, ushort did, byte[] rawData, string? vin = null, DateTime? readAt = null)
    {
        EcuAddress = ecuAddress;
        Did = did;
        RawData = rawData ?? Array.Empty<byte>();
        Vin = vin;
        ReadAt = readAt ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Computes SHA-256 hash of the block's raw data for verification.
    /// </summary>
    public string ComputeSha256()
    {
        byte[] hash = SHA256.HashData(RawData);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a deep copy of this coding block for safe editing.
    /// </summary>
    public CodingBlock Clone()
    {
        byte[] copiedData = new byte[RawData.Length];
        Array.Copy(RawData, copiedData, RawData.Length);

        return new CodingBlock(EcuAddress, Did, copiedData, Vin, ReadAt);
    }
}
