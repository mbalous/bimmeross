using System.Security.Cryptography;
using System.Text;
using BmwCoder.Coding.Checksum;
using LegacyCodingBlock = BmwCoder.Coding.CodingBlock;

namespace BmwCoder.Core.Workspace;

public sealed class RawBlockDraft
{
    private readonly byte[] _original;
    private byte[] _current;
    public byte Ecu { get; }
    public ushort Did { get; }
    public int Length => _current.Length;
    public int ChangedBytes => _original.Zip(_current).Count(pair => pair.First != pair.Second);
    public string Sha256 => Convert.ToHexString(SHA256.HashData(_current));

    public RawBlockDraft(byte ecu, ushort did, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > CodingFile.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "A raw block must contain between 1 byte and 4 MiB.");
        Ecu = ecu;
        Did = did;
        _original = bytes.ToArray();
        _current = bytes.ToArray();
    }

    public void SetByte(int offset, byte value)
    {
        if ((uint)offset >= (uint)_current.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _current[offset] = value;
    }

    public void Revert() => _current = _original.ToArray();

    public bool VerifyChecksum()
    {
        if (!ChecksumRegistry.Default.HasChecksum(Ecu, Did))
            throw new InvalidOperationException("No legacy checksum definition exists for this ECU and DID.");
        return ChecksumRegistry.Default.Verify(new LegacyCodingBlock(Ecu, Did, _current));
    }

    public void ApplyChecksum()
    {
        var copy = new LegacyCodingBlock(Ecu, Did, _current.ToArray());
        ChecksumRegistry.Default.Apply(copy);
        _current = copy.RawData;
    }

    public string Preview(int maximumBytes = 4096)
    {
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var text = new StringBuilder();
        int length = Math.Min(maximumBytes, _current.Length);
        for (int offset = 0; offset < length; offset += 16)
        {
            int count = Math.Min(16, length - offset);
            text.Append(offset.ToString("X8")).Append("  ")
                .Append(Convert.ToHexString(_current.AsSpan(offset, count)));
            if (!_current.AsSpan(offset, count).SequenceEqual(_original.AsSpan(offset, count)))
                text.Append("  *");
            text.AppendLine();
        }
        if (length < _current.Length)
            text.AppendLine($"Preview truncated at {length} bytes.");
        return text.ToString();
    }

    public void ExportNewFile(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(_current);
    }
}
