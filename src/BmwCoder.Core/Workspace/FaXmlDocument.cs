using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BmwCoder.Core.Workspace;

public sealed record XmlField(int Id, string Path, string Value);
public sealed record XmlChange(string Path, string Original, string Current);

public sealed class FaXmlDocument
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private readonly byte[] _originalBytes;
    private readonly XDocument _document;
    private readonly List<(string Path, XObject Node, string Original)> _fields = new();

    private FaXmlDocument(byte[] bytes, XDocument document)
    {
        _originalBytes = bytes;
        _document = document;
        Index(document.Root!, "");
    }

    public static FaXmlDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes)
            throw new FormatException("FA XML exceeds the 4 MiB limit.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    public static FaXmlDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes)
            throw new FormatException("FA XML exceeds the 4 MiB limit.");
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes
        });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root == null || !doc.Descendants().Any(e =>
                e.Name.LocalName.Equals("fa", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("Fahrzeugauftrag", StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("Expected an FA / Fahrzeugauftrag XML document.");
        return new FaXmlDocument(bytes.ToArray(), doc);
    }

    public IReadOnlyList<XmlField> Fields => _fields.Select((f, i) =>
        new XmlField(i, f.Path, Value(f.Node))).ToArray();

    public IReadOnlyList<XmlChange> Changes => _fields.Where(f => f.Original != Value(f.Node))
        .Select(f => new XmlChange(f.Path, f.Original, Value(f.Node))).ToArray();

    public void SetValue(int fieldId, string value)
    {
        if (fieldId < 0 || fieldId >= _fields.Count)
            throw new ArgumentOutOfRangeException(nameof(fieldId));
        XmlConvert.VerifyXmlChars(value);
        if (_fields[fieldId].Node is XAttribute attribute)
            attribute.Value = value;
        else
            ((XElement)_fields[fieldId].Node).Value = value;
    }

    public byte[] Export()
    {
        if (Changes.Count == 0)
            return _originalBytes.ToArray();
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false,
            OmitXmlDeclaration = _document.Declaration == null
        }))
            _document.Save(writer);
        return stream.ToArray();
    }

    public void ExportNewFile(string path)
    {
        byte[] bytes = Export();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    private void Index(XElement element, string parent)
    {
        string path = $"{parent}/{element.Name}[{element.ElementsBeforeSelf(element.Name).Count() + 1}]";
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            _fields.Add(($"{path}/@{attribute.Name}", attribute, attribute.Value));
        if (!element.HasElements && !element.Nodes().Any(n => n is XComment or XProcessingInstruction))
            _fields.Add((path, element, element.Value));
        foreach (var child in element.Elements())
            Index(child, path);
    }

    private static string Value(XObject node) => node is XAttribute a ? a.Value : ((XElement)node).Value;
}
