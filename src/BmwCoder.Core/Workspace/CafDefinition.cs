using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace BmwCoder.Core.Workspace;

public interface ICafReader
{
    Task<CafDefinition> ReadAsync(string path, string istaPath, string javaPath, CancellationToken cancellationToken);
}

public sealed record CafField(string Path, string Value);

public sealed class CafDefinition
{
    public string Name { get; }
    public string RuntimeVersion { get; }
    public IReadOnlyList<CafField> Fields { get; }
    public string Diagnostics { get; }

    private CafDefinition(string name, string runtimeVersion, List<CafField> fields, string diagnostics)
    {
        Name = name;
        RuntimeVersion = runtimeVersion;
        Fields = fields.AsReadOnly();
        Diagnostics = diagnostics;
    }

    public static CafDefinition Parse(string name, string runtimeVersion, string xml, string diagnostics = "")
    {
        if (string.IsNullOrWhiteSpace(runtimeVersion))
            throw new FormatException("The CAF reader did not identify its initialized runtime.");
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "caf")
            throw new FormatException("The CAF reader returned an unexpected XML root.");
        var fields = new List<CafField>();
        int nodes = 0;
        Add(document.Root, "", 1, fields, 0, ref nodes);
        return new CafDefinition(Path.GetFileName(name), runtimeVersion, fields, diagnostics);
    }

    private static void Add(XElement element, string parent, int index, List<CafField> fields, int depth, ref int nodes)
    {
        if (depth > 128 || ++nodes > 500000 || fields.Count >= 500000)
            throw new FormatException("CAF definition exceeds the supported tree depth or field count.");
        string path = $"{parent}/{element.Name}[{index}]";
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            if (fields.Count >= 500000)
                throw new FormatException("CAF definition exceeds the supported field count.");
            fields.Add(new CafField($"{path}/@{attribute.Name}", attribute.Value));
        }
        if (!element.HasElements)
        {
            if (fields.Count >= 500000)
                throw new FormatException("CAF definition exceeds the supported field count.");
            fields.Add(new CafField(path, element.Value));
        }
        var indices = new Dictionary<XName, int>();
        foreach (var child in element.Elements())
        {
            indices.TryGetValue(child.Name, out int previous);
            indices[child.Name] = previous + 1;
            Add(child, path, previous + 1, fields, depth + 1, ref nodes);
        }
    }
}
