using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace BmwCoder.Core.Workspace;

public sealed record CodingReadRequest(string Host, int Port, string Series, byte Ecu,
    string PsdzDataPath, string IstaPath, string JavaPath, string? CafFile = null, string? ExpectedVin = null);

public interface ICodingReader
{
    Task<DecodedCoding> ReadAsync(CodingReadRequest request, CancellationToken cancellationToken);
    Task<DecodedCoding> DecodeAsync(string ncdPath, string cafFile, CodingReadRequest settings, CancellationToken cancellationToken);
}

public sealed record CodingBlock(int Address, string Hex);
public sealed record CodingParameter(string Id, int BlockAddress, string? Name, string? Value,
    IReadOnlyList<string> Interpretations, string Start, string End, string Mask)
{
    public bool IsDecoded => Value != null;
}

public sealed class DecodedCoding
{
    public string CafFile { get; }
    public string BaseVariant { get; }
    public string? Vin { get; }
    public int? Ecu { get; }
    public string RuntimeVersion { get; }
    public string OriginalNcd { get; }
    public string Sha256 { get; }
    public string Diagnostics { get; }
    public IReadOnlyList<CodingParameter> Parameters { get; }
    public IReadOnlyList<CodingBlock> Blocks { get; }
    public IReadOnlyList<int> OpaqueBlocks { get; }

    private DecodedCoding(string cafFile, string variant, string? vin, int? ecu, string runtimeVersion,
        string ncd, string diagnostics, List<CodingParameter> parameters, List<CodingBlock> blocks, List<int> opaque)
    {
        CafFile = cafFile;
        BaseVariant = variant;
        Vin = vin;
        Ecu = ecu;
        RuntimeVersion = runtimeVersion;
        OriginalNcd = ncd;
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ncd)));
        Diagnostics = diagnostics;
        Parameters = parameters.AsReadOnly();
        Blocks = blocks.AsReadOnly();
        OpaqueBlocks = opaque.AsReadOnly();
    }

    public static DecodedCoding Parse(string cafFile, string variant, string? vin, int? ecu, string runtimeVersion,
        string ncd, string xml, IEnumerable<CodingBlock> originalBlocks, IEnumerable<int> verifiedBlocks, string diagnostics = "")
    {
        if (string.IsNullOrWhiteSpace(runtimeVersion) || string.IsNullOrWhiteSpace(ncd))
            throw new FormatException("Coding response requires an initialized runtime and original NCD.");
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new FormatException("Missing decoded CAF document.");
        XNamespace ns = root.Name.Namespace;
        if (root.Name.LocalName != "caf")
            throw new FormatException("Unexpected decoded coding XML root.");
        var identity = root.Element(ns + "sweHeader")?.Element(ns + "cafIdent")
            ?? throw new FormatException("Decoded coding has no CAF identity.");
        string IdentityPart(string name) => identity.Element(ns + name)?.Value.Trim()
            ?? throw new FormatException($"CAF identity is missing {name}.");
        if (!IdentityPart("processClass").Equals("CAFD", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Decoded coding does not identify a CAFD.");
        if (!uint.TryParse(IdentityPart("id"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            throw new FormatException("Invalid CAF identifier.");
        int Version(string name) => int.TryParse(IdentityPart(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
            value is >= 0 and <= 255 ? value : throw new FormatException("Invalid CAF version.");
        string actual = $"cafd_{id:x8}.caf.{Version("mainVersion"):D3}_{Version("subVersion"):D3}_{Version("patchVersion"):D3}";
        if (!string.Equals(actual, cafFile, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Decoded CAF identity/version does not match the requested coding definition.");
        var blocks = originalBlocks.ToList();
        if (blocks.Count is < 1 or > 256 || blocks.Select(b => b.Address).Distinct().Count() != blocks.Count)
            throw new FormatException("Missing, duplicate or excessive original coding blocks.");
        foreach (var block in blocks)
            if (block.Address is < 0 or > 65535 || block.Hex.Length > 2 * 1024 * 1024 ||
                !Regex.IsMatch(block.Hex, @"\A(?:[0-9A-Fa-f]{2})*\z", RegexOptions.CultureInvariant))
                throw new FormatException("Invalid original coding block.");
        var byAddress = blocks.ToDictionary(b => b.Address);
        var verified = verifiedBlocks.ToArray();
        if (verified.Length == 0 || verified.Distinct().Count() != verified.Length || verified.Any(a => !byAddress.ContainsKey(a)))
            throw new FormatException("Missing or invalid byte-verified coding block identities.");
        var validated = verified.ToHashSet();
        var represented = new HashSet<int>();
        var parameters = new List<CodingParameter>();
        var groups = root.Element(ns + "data")?.Elements(ns + "datagroup")
            ?? throw new FormatException("Decoded coding has no data groups.");
        foreach (var group in groups)
        {
            if (!int.TryParse((string?)group.Attribute("address"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address))
                throw new FormatException("Invalid coding data-group address.");
            var functions = group.Elements(ns + "function").ToArray();
            if (functions.Length == 0 || !validated.Contains(address))
                continue;
            if (!byAddress.ContainsKey(address) || !represented.Add(address))
                throw new FormatException("Decoded parameters reference a missing or duplicate coding block.");
            int ordinal = 0;
            foreach (var function in functions)
            {
                if (parameters.Count >= 20000)
                    throw new FormatException("Decoded coding exceeds the supported parameter count.");
                var selected = function.Elements(ns + "interpretation")
                    .Where(i => i.Element(ns + "values")?.Elements(ns + "unitVal").Any() == true)
                    .ToArray();
                if (selected.Length > 1)
                    throw new FormatException("Decoded parameter has multiple current interpretations.");
                var current = selected.Length == 1 ? Units(selected[0].Element(ns + "values"), ns) : null;
                var interpretations = selected
                    .Select(i => AvailableName(i.Element(ns + "paramName")?.Value))
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToList().AsReadOnly();
                var storage = function.Element(ns + "storageInfo")
                    ?? throw new FormatException("Decoded parameter has no storage description.");
                parameters.Add(new CodingParameter($"{address:X4}/{ordinal++}", address,
                    AvailableName((string?)function.Attribute("name")),
                    current == null ? null :
                        string.Join(" ", current.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}:{v.Value}")),
                    interpretations, storage.Element(ns + "start")?.Value ?? "",
                    storage.Element(ns + "end")?.Value ?? "", storage.Element(ns + "mask")?.Value ?? ""));
            }
        }
        if (!parameters.Any(p => p.IsDecoded))
            throw new FormatException("No current coding parameters were decoded.");
        return new DecodedCoding(actual, variant, vin, ecu, runtimeVersion, ncd, diagnostics,
            parameters, blocks, blocks.Where(b => !represented.Contains(b.Address)).Select(b => b.Address).ToList());
    }

    public void ExportOriginal(string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(OriginalNcd);
        output.Write(bytes);
    }

    private static string? AvailableName(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "." ? null : value.Trim();

    private static Dictionary<string, string> Units(XElement? values, XNamespace ns)
    {
        if (values == null)
            throw new FormatException("A decoded parameter is missing its current values or interpretation.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in values.Elements(ns + "unitVal"))
        {
            string position = ((string?)unit.Attribute("position"))?.Trim() ??
                throw new FormatException("Coding unit has no position.");
            string value = unit.Value.Trim().ToUpperInvariant();
            if (position.Length == 0 || !Regex.IsMatch(value, @"\A[0-9A-F]{2}\z", RegexOptions.CultureInvariant) ||
                !result.TryAdd(position, value))
                throw new FormatException("Invalid or duplicate decoded coding unit.");
        }
        if (result.Count == 0)
            throw new FormatException("A decoded parameter contains no current coding units.");
        return result;
    }

}
