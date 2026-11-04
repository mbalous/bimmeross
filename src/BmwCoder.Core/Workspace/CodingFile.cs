using System.Security.Cryptography;
using System.Text;

namespace BmwCoder.Core.Workspace;

public sealed class CodingFile
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private readonly byte[] _bytes;
    public string Name { get; }
    public int Length => _bytes.Length;
    public string Sha256 { get; }

    private CodingFile(string name, byte[] bytes)
    {
        Name = name;
        _bytes = bytes;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static CodingFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes)
            throw new FormatException("Coding file exceeds the 4 MiB preview limit.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return new CodingFile(Path.GetFileName(path), bytes);
    }

    public string HexPreview(int maximumBytes = 4096)
    {
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var text = new StringBuilder();
        int length = Math.Min(maximumBytes, _bytes.Length);
        for (int offset = 0; offset < length; offset += 16)
        {
            var line = _bytes.AsSpan(offset, Math.Min(16, length - offset));
            text.Append(offset.ToString("X8")).Append("  ")
                .AppendLine(Convert.ToHexString(line));
        }
        if (length < _bytes.Length)
            text.AppendLine($"Preview truncated at {length} bytes; original preserved.");
        return text.ToString();
    }

    public void ExportNewFile(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(_bytes);
    }
}
