using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BmwCoder.Core.Diagnostics;

public sealed class PsdzDiagnosticData
{
    private sealed record Document(string Path, XDocument Xml, Dictionary<string, XElement> Ids);
    private sealed record Layout(ushort Did, string Name, string ServiceName, Document Document, XElement Response);
    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, string[]> _variants = new();
    private readonly Dictionary<string, Dictionary<ushort, List<Layout>>> _layouts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingLayouts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public string SourceDirectory { get; }
    public string Series { get; }

    private PsdzDiagnosticData(string source, string series)
    {
        SourceDirectory = source;
        Series = series;
        _files = Directory.EnumerateFiles(Path.Combine(source, "odx-d"), "*.odx-d")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal);
        foreach (string file in _files.Values.Distinct().ToArray())
        {
            using var reader = XmlReader.Create(file, ReaderSettings());
            var parents = new Dictionary<int, string>();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "SHORT-NAME" && reader.Depth <= 4 &&
                    parents.GetValueOrDefault(reader.Depth - 1) is "DIAG-LAYER-CONTAINER" or "BASE-VARIANT" or
                        "ECU-VARIANT" or "FUNCTIONAL-GROUP" or "ECU-SHARED-DATA" or "PROTOCOL")
                {
                    string name = reader.ReadElementContentAsString();
                    if (_files.TryGetValue(name, out string? existing) && existing != file)
                        throw new FormatException($"Ambiguous ODX document/layer name {name}.");
                    _files[name] = file;
                }
                else if (reader.Depth > 4)
                    reader.Skip();
                else
                {
                    if (reader.NodeType == XmlNodeType.Element)
                        parents[reader.Depth] = reader.LocalName;
                    reader.Read();
                }
            }
        }
        var addressParameters = new HashSet<(string Document, string Id)>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "odx-c"), "*.odx-c"))
        {
            foreach (var element in ReadXml(file).Descendants().Where(e => Text(e, "SHORT-NAME") == "DIAG_ADDRESS"))
                if (element.Attribute("ID") is { } id)
                    addressParameters.Add((Path.GetFileNameWithoutExtension(file), id.Value));
        }
        var names = new Dictionary<byte, HashSet<string>>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "odx-v"), "*.odx-v"))
        {
            foreach (var link in ReadXml(file).Descendants().Where(e => e.Name.LocalName == "LOGICAL-LINK"))
            {
                string? variant = Child(link, "BASE-VARIANT-REF")?.Attribute("DOCREF")?.Value;
                if (variant == null) continue;
                foreach (var reference in link.Descendants().Where(e => e.Name.LocalName == "LINK-COMPARAM-REF"))
                {
                    if (!addressParameters.Contains((reference.Attribute("DOCREF")?.Value ?? "", reference.Attribute("ID-REF")?.Value ?? "")) ||
                        !byte.TryParse(Text(reference, "VALUE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte address))
                        continue;
                    if (!names.TryGetValue(address, out var candidates))
                        names[address] = candidates = new HashSet<string>(StringComparer.Ordinal);
                    candidates.Add(variant);
                }
            }
        }
        foreach (var pair in names)
            _variants[pair.Key] = pair.Value.Order(StringComparer.Ordinal).ToArray();
    }

    public static PsdzDiagnosticData Load(string root, string series)
    {
        if (string.IsNullOrWhiteSpace(series) || series != Path.GetFileName(series) ||
            series.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Select a main-series folder name such as F020.", nameof(series));
        string source = Path.GetFullPath(root);
        if (Directory.Exists(Path.Combine(source, "odx", "src")))
            source = Path.Combine(source, "odx", "src");
        else if (!Directory.Exists(Path.Combine(source, "odx-d")))
        {
            string seriesDirectory = Path.Combine(source, "mainseries", series);
            source = Directory.GetDirectories(seriesDirectory)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Select(p => Path.Combine(p, "odx", "src"))
                .FirstOrDefault(p => Directory.Exists(p))
                ?? throw new DirectoryNotFoundException($"No ODX source data found for {series}.");
        }
        return new PsdzDiagnosticData(source, series);
    }

    public string? GetEcuName(byte address) =>
        _variants.TryGetValue(address, out var candidates) ? string.Join(" / ", candidates) : null;

    public IReadOnlyList<DiagnosticDefinition> GetDids(byte address)
    {
        lock (_gate)
        {
            return Variants(address).SelectMany(v => Layouts(v).Values.SelectMany(l => l))
                .GroupBy(l => l.Did).OrderBy(g => g.Key)
                .Select(g => new DiagnosticDefinition(g.Key, string.Join(" / ", g.Select(l => l.Name).Distinct())))
                .ToArray();
        }
    }

    public DiagnosticReading? Decode(byte address, ushort did, byte[] payload)
    {
        lock (_gate)
        {
            var candidates = Variants(address).SelectMany(v => Layouts(v).GetValueOrDefault(did) ?? Enumerable.Empty<Layout>())
                .DistinctBy(l => (l.Document.Path, l.Response.Attribute("ID")?.Value)).ToArray();
            if (candidates.Length == 0) return null;
            byte[] pdu = new byte[payload.Length + 3];
            pdu[0] = 0x62;
            pdu[1] = (byte)(did >> 8);
            pdu[2] = (byte)did;
            payload.CopyTo(pdu, 3);
            var readings = new List<DiagnosticReading>();
            foreach (var layout in candidates)
            {
                var reading = new DiagnosticReading
                {
                    Ecu = address,
                    Did = did,
                    Name = layout.Name,
                    RequestHex = $"22{did:X4}",
                    ResponseHex = Convert.ToHexString(pdu),
                    PayloadHex = Convert.ToHexString(payload),
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
                    DecoderSource = layout.Document.Path
                };
                reading.Values["Length (bytes)"] = payload.Length.ToString(CultureInfo.InvariantCulture);
                DecodeNode(layout.Document, layout.Response, pdu, 0, "", reading, 0);
                reading.Warnings.Add("ODX address candidates are not a confirmed ECU-variant identification; raw bytes are preserved.");
                readings.Add(reading);
            }
            if (readings.Select(r => string.Join("\n", r.Values.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))).Distinct().Count() > 1)
            {
                var raw = new DiagnosticReading
                {
                    Ecu = address,
                    Did = did,
                    Name = $"DID 0x{did:X4}",
                    RequestHex = $"22{did:X4}",
                    ResponseHex = Convert.ToHexString(pdu),
                    PayloadHex = Convert.ToHexString(payload),
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
                    DecoderSource = string.Join("; ", candidates.Select(l => l.Document.Path).Distinct())
                };
                raw.Warnings.Add("Conflicting ODX layouts for the candidate ECU variants. No interpretation was selected; use the raw response.");
                return raw;
            }
            return readings[0];
        }
    }

    private IEnumerable<string> Variants(byte address) =>
        _variants.GetValueOrDefault(address) ?? Array.Empty<string>();

    private Document ReadDocument(string name)
    {
        if (!_files.TryGetValue(name, out var file))
            throw new FileNotFoundException($"Referenced ODX document {name} is missing from this dataset.");
        if (_documents.TryGetValue(file, out var document)) return document;
        var xml = ReadXml(file);
        document = new Document(file, xml, xml.Descendants().Where(e => e.Attribute("ID") != null)
            .ToDictionary(e => e.Attribute("ID")!.Value, StringComparer.Ordinal));
        _documents.Add(file, document);
        return document;
    }

    private (Document Document, XElement Element) Resolve(Document current, XElement reference)
    {
        var document = reference.Attribute("DOCREF") is { } doc ? ReadDocument(doc.Value) : current;
        string id = reference.Attribute("ID-REF")?.Value ?? throw new FormatException("ODX reference has no ID.");
        return (document, document.Ids.GetValueOrDefault(id) ?? throw new FormatException($"Unresolved ODX reference {id} in {document.Path}."));
    }

    private Dictionary<ushort, List<Layout>> Layouts(string variant, string? layerId = null)
    {
        string cacheKey = $"{variant}#{layerId}";
        if (_layouts.TryGetValue(cacheKey, out var layouts)) return layouts;
        if (!_loadingLayouts.Add(cacheKey))
            throw new FormatException($"Cyclic ODX inheritance at {variant}.");
        try
        {
            layouts = new Dictionary<ushort, List<Layout>>();
            var document = ReadDocument(variant);
            var scope = layerId != null
                ? document.Ids.GetValueOrDefault(layerId) ?? throw new FormatException($"Missing ODX parent layer {layerId}.")
                : document.Xml.Descendants().FirstOrDefault(e => Text(e, "SHORT-NAME") == variant &&
                    e.Name.LocalName is "BASE-VARIANT" or "ECU-VARIANT" or "FUNCTIONAL-GROUP" or "ECU-SHARED-DATA")
                    ?? document.Xml.Root ?? throw new FormatException("Empty ODX document.");
            foreach (var service in scope.Descendants().Where(e => e.Name.LocalName == "DIAG-SERVICE"))
            {
                if (Child(service, "REQUEST-REF") is not { } requestRef) continue;
                var request = Resolve(document, requestRef);
                var parameters = Child(request.Element, "PARAMS")?.Elements().ToArray() ?? Array.Empty<XElement>();
                if (!parameters.Any(p => Text(p, "BYTE-POSITION") == "0" && Text(p, "CODED-VALUE") == "34")) continue;
                var identifiers = new List<(ushort Did, string Name)>();
                foreach (var parameter in parameters.Where(p => Text(p, "BYTE-POSITION") == "1"))
                {
                    if (ushort.TryParse(Text(parameter, "CODED-VALUE"), out ushort fixedDid))
                        identifiers.Add((fixedDid, Text(service, "LONG-NAME") ?? Text(service, "SHORT-NAME") ?? $"DID 0x{fixedDid:X4}"));
                    else if (Child(parameter, "DOP-REF") is { } dopRef)
                    {
                        var dop = Resolve(request.Document, dopRef).Element;
                        string? physicalConstant = Text(parameter, "PHYS-CONSTANT-VALUE")
                            ?? Text(parameter, "PHYSICAL-DEFAULT-VALUE");
                        foreach (var scale in dop.Descendants().Where(e => e.Name.LocalName == "COMPU-SCALE"))
                        {
                            string? low = Text(scale, "LOWER-LIMIT");
                            string? high = Text(scale, "UPPER-LIMIT");
                            string? label = Child(scale, "COMPU-CONST")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VT")?.Value;
                            if (label != null && (physicalConstant == null || physicalConstant == label) &&
                                (high == null || low == high) && ushort.TryParse(low, out ushort did))
                                identifiers.Add((did, label));
                        }
                    }
                }
                foreach (var responseRef in service.Descendants().Where(e => e.Name.LocalName == "POS-RESPONSE-REF"))
                {
                    var response = Resolve(document, responseRef);
                    if (!(Child(response.Element, "PARAMS")?.Elements().Any(p =>
                        Text(p, "BYTE-POSITION") == "0" && Text(p, "CODED-VALUE") == "98") ?? false)) continue;
                    foreach (var identifier in identifiers)
                    {
                        if (!layouts.TryGetValue(identifier.Did, out var alternatives))
                            layouts[identifier.Did] = alternatives = new List<Layout>();
                        alternatives.Add(new Layout(identifier.Did, identifier.Name, Text(service, "SHORT-NAME") ?? "",
                            response.Document, response.Element));
                    }
                }
            }
            foreach (var parent in scope.Descendants().Where(e => e.Name.LocalName == "PARENT-REF"))
                if (parent.Attribute("DOCREF") is { } name)
                {
                    var excluded = Child(parent, "NOT-INHERITED-DIAG-COMMS")?.Descendants()
                        .Select(e => e.Attribute("SHORT-NAME")?.Value).Where(n => n != null).ToHashSet()
                        ?? new HashSet<string?>();
                    foreach (var pair in Layouts(name.Value, parent.Attribute("ID-REF")?.Value))
                    {
                        var inherited = pair.Value.Where(l => !excluded.Contains(l.ServiceName)).ToList();
                        if (inherited.Count > 0)
                            layouts.TryAdd(pair.Key, inherited);
                    }
                }
            _layouts.Add(cacheKey, layouts);
            return layouts;
        }
        finally
        {
            _loadingLayouts.Remove(cacheKey);
        }
    }

    private void DecodeNode(Document document, XElement node, byte[] pdu, int offset,
        string prefix, DiagnosticReading reading, int depth)
    {
        if (depth > 16) throw new FormatException("ODX structure nesting exceeds the decoding limit.");
        if (node.Name.LocalName is "POS-RESPONSE" or "STRUCTURE")
        {
            foreach (var parameter in Child(node, "PARAMS")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (Child(parameter, "DOP-REF") is not { } reference)
                {
                    if (Child(parameter, "DOP-SNREF") != null ||
                        parameter.Attributes().Any(a => a.Name.LocalName == "type" && a.Value == "VALUE"))
                        reading.Warnings.Add("ODX value parameter has no supported DOP reference. Raw bytes are preserved.");
                    continue;
                }
                string name = prefix + (Text(parameter, "LONG-NAME") ?? Text(parameter, "SHORT-NAME") ?? "Field");
                try
                {
                    if (!int.TryParse(Text(parameter, "BYTE-POSITION"), out int position) || position < 0 ||
                        Text(parameter, "BIT-POSITION") is not (null or "0"))
                        throw new NotSupportedException("Implicit offsets or bit-positioned fields are not yet supported.");
                    var target = Resolve(document, reference);
                    DecodeNode(target.Document, target.Element, pdu, checked(offset + position), name + " / ", reading, depth + 1);
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException or OverflowException or ArgumentOutOfRangeException)
                {
                    reading.Warnings.Add($"{name}: {ex.Message} Raw bytes are preserved.");
                }
            }
            return;
        }
        if (node.Name.LocalName == "MUX")
        {
            var key = Child(node, "SWITCH-KEY") ?? throw new FormatException("ODX multiplexer has no key.");
            if (Text(key, "BIT-POSITION") is not (null or "0"))
                throw new NotSupportedException("Bit-positioned ODX switch keys are not yet supported.");
            var reference = Child(key, "DATA-OBJECT-PROP-REF") ?? throw new FormatException("ODX multiplexer key has no type.");
            var target = Resolve(document, reference);
            var coded = Child(target.Element, "DIAG-CODED-TYPE") ?? throw new FormatException("ODX switch key has no coded type.");
            decimal value = ReadNumber(coded, pdu, checked(offset + Integer(key, "BYTE-POSITION")));
            var selected = Child(node, "CASES")?.Elements().FirstOrDefault(c => InRange(c, value))
                ?? Child(node, "DEFAULT-CASE") ?? throw new FormatException($"No ODX switch case for {value}.");
            var structure = Child(selected, "STRUCTURE-REF") ?? throw new NotSupportedException("Unsupported ODX switch-case reference.");
            var resolved = Resolve(document, structure);
            DecodeNode(resolved.Document, resolved.Element, pdu, checked(offset + Integer(node, "BYTE-POSITION")), prefix, reading, depth + 1);
            return;
        }
        if (node.Name.LocalName != "DATA-OBJECT-PROP")
            throw new NotSupportedException($"ODX {node.Name.LocalName} is not yet supported.");
        var type = Child(node, "DIAG-CODED-TYPE") ?? throw new FormatException("ODX data object has no coded type.");
        string category = Text(Child(node, "COMPU-METHOD"), "CATEGORY")
            ?? throw new FormatException("ODX data object has no computation method.");
        string baseType = type.Attribute("BASE-DATA-TYPE")?.Value ?? "";
        string valueText;
        if (baseType is "A_BYTEFIELD" or "A_ASCIISTRING" or "A_UTF8STRING" or "A_UNICODE2STRING")
        {
            if (category != "IDENTICAL")
                throw new NotSupportedException($"ODX {category} conversion for byte/string data is not supported.");
            int length;
            if (Text(type, "BIT-LENGTH") is { } bits && int.TryParse(bits, out int bitLength) && bitLength % 8 == 0)
                length = bitLength / 8;
            else if (type.Attribute("TERMINATION")?.Value == "END-OF-PDU")
                length = pdu.Length - offset;
            else
                throw new NotSupportedException("Unsupported ODX variable-length/terminated data type.");
            if (length < 0 || offset < 0 || offset > pdu.Length - length)
                throw new FormatException("ODX field extends beyond the response.");
            var bytes = pdu.AsSpan(offset, length);
            if (Text(type, "MIN-LENGTH") is { } minimum && (ulong)length < ulong.Parse(minimum, CultureInfo.InvariantCulture) ||
                Text(type, "MAX-LENGTH") is { } maximum && (ulong)length > ulong.Parse(maximum, CultureInfo.InvariantCulture))
                throw new FormatException("ODX field length is outside its declared limits.");
            if (baseType == "A_BYTEFIELD")
            {
                valueText = Convert.ToHexString(bytes);
                if (bytes.Length > 0 && bytes.ToArray().All(b => b == 0 || b is >= 32 and <= 126))
                    valueText += $" [ASCII heuristic: {Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ')}]";
            }
            else
            {
                var encoding = baseType switch
                {
                    "A_UTF8STRING" => new UTF8Encoding(false, true),
                    "A_UNICODE2STRING" => new UnicodeEncoding(type.Attribute("IS-HIGHLOW-BYTE-ORDER")?.Value != "false", false, true),
                    _ => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                };
                valueText = encoding.GetString(bytes).TrimEnd('\0');
            }
        }
        else
        {
            decimal value = ReadNumber(type, pdu, offset);
            if (Child(node, "INTERNAL-CONSTR") is { } constraint && !InRange(constraint, value))
                throw new FormatException($"Value {value} is outside the ODX internal constraints.");
            if (category == "TEXTTABLE")
            {
                var scale = Child(node, "COMPU-METHOD")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "COMPU-SCALE" && InRange(e, value));
                string? label = scale?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VT")?.Value;
                valueText = label == null ? $"{value} (unmapped ODX enum)" : $"{label} ({value})";
            }
            else if (category == "LINEAR")
            {
                var scale = Child(node, "COMPU-METHOD")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "COMPU-SCALE" && InRange(e, value));
                var coefficients = scale?.Descendants().FirstOrDefault(e => e.Name.LocalName == "COMPU-RATIONAL-COEFFS");
                var numerator = Child(coefficients, "COMPU-NUMERATOR")?.Elements().Select(e => decimal.Parse(e.Value, CultureInfo.InvariantCulture)).ToArray();
                var denominator = Child(coefficients, "COMPU-DENOMINATOR")?.Elements().Select(e => decimal.Parse(e.Value, CultureInfo.InvariantCulture)).ToArray();
                if (numerator?.Length != 2 || denominator?.Length != 1 || denominator[0] == 0)
                    throw new NotSupportedException("Unsupported ODX rational scaling.");
                valueText = ((numerator[0] + numerator[1] * value) / denominator[0]).ToString(CultureInfo.InvariantCulture);
            }
            else if (category == "IDENTICAL")
                valueText = value.ToString(CultureInfo.InvariantCulture);
            else
                throw new NotSupportedException($"ODX computation method {category} is not yet supported.");
            if (Child(node, "UNIT-REF") is { } unitRef)
            {
                var unit = Resolve(document, unitRef).Element;
                valueText += " " + (Text(unit, "DISPLAY-NAME") ?? Text(unit, "SHORT-NAME"));
            }
        }
        reading.Values[prefix.TrimEnd(' ', '/')] = valueText;
    }

    private static decimal ReadNumber(XElement type, byte[] pdu, int offset)
    {
        int bits = Integer(type, "BIT-LENGTH");
        if (bits < 8 || bits > 64 || bits % 8 != 0 || type.Attribute("BIT-MASK") != null || Child(type, "BIT-MASK") != null)
            throw new NotSupportedException("Only unmasked, byte-aligned ODX integer fields up to 64 bits are supported.");
        int length = bits / 8;
        if (offset < 0 || offset > pdu.Length - length)
            throw new FormatException("ODX number extends beyond the response.");
        bool littleEndian = type.Attribute("IS-HIGHLOW-BYTE-ORDER")?.Value == "false";
        ulong raw = 0;
        for (int i = 0; i < length; i++)
            raw = (raw << 8) | pdu[offset + (littleEndian ? length - 1 - i : i)];
        string encoding = type.Attribute("BASE-TYPE-ENCODING")?.Value ?? "NONE";
        if (encoding == "BCD-P")
        {
            decimal number = 0, factor = 1;
            for (int i = 0; i < bits / 4; i++, raw >>= 4, factor *= 10)
            {
                if ((raw & 15) > 9) throw new FormatException("Invalid packed BCD digit.");
                number += (raw & 15) * factor;
            }
            return number;
        }
        if (encoding != "NONE" && encoding != "2C")
            throw new NotSupportedException($"Unsupported ODX number encoding {encoding}.");
        return type.Attribute("BASE-DATA-TYPE")?.Value switch
        {
            "A_UINT32" => raw,
            "A_INT32" => (raw & (1UL << (bits - 1))) == 0 ? raw : (decimal)raw - (decimal)Math.Pow(2, bits),
            _ => throw new NotSupportedException("Unsupported ODX numeric base type.")
        };
    }

    private static bool InRange(XElement element, decimal value)
    {
        var lower = Child(element, "LOWER-LIMIT");
        var upper = Child(element, "UPPER-LIMIT");
        return (lower == null || value > decimal.Parse(lower.Value, CultureInfo.InvariantCulture) ||
                value == decimal.Parse(lower.Value, CultureInfo.InvariantCulture) && lower.Attribute("INTERVAL-TYPE")?.Value != "OPEN") &&
            (upper == null || value < decimal.Parse(upper.Value, CultureInfo.InvariantCulture) ||
                value == decimal.Parse(upper.Value, CultureInfo.InvariantCulture) && upper.Attribute("INTERVAL-TYPE")?.Value != "OPEN");
    }

    private static int Integer(XElement element, string name) =>
        int.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw new NotSupportedException($"ODX {name} is missing or invalid.");

    private static XElement? Child(XElement? element, string name) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    private static string? Text(XElement? element, string name) => Child(element, name)?.Value;
    private static XDocument ReadXml(string file)
    {
        using var reader = XmlReader.Create(file, ReaderSettings());
        return XDocument.Load(reader);
    }
    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 20_000_000
    };
}
