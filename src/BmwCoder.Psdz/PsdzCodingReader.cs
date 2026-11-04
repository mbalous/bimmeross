using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Workspace;

namespace BmwCoder.Psdz;

public sealed class PsdzCodingReader : ICodingReader
{
    public async Task<DecodedCoding> ReadAsync(CodingReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        if (!IPAddress.TryParse(request.Host, out var host) || host.AddressFamily != AddressFamily.InterNetwork ||
            host.Equals(IPAddress.Any) || request.Port is < 1 or > 65535)
            throw new PsdzException("CONFIGURATION", "Coding reads require an explicit gateway IPv4 address and valid TCP port.");
        using var view = await PsdzDataView.CreateAsync(request.IstaPath, request.PsdzDataPath, cancellationToken).ConfigureAwait(false);
        var start = PsdzCafReader.CreateOperationStartInfo(request.IstaPath, request.JavaPath, "read-coding", view.Root,
            host.ToString(), request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Series, request.Ecu.ToString("X2"), request.CafFile ?? "auto", request.ExpectedVin ?? "");
        using var vehicle = new ReadOnlyVehicle();
        await vehicle.ConnectAsync(host.ToString(), request.Port, cancellationToken).ConfigureAwait(false);
        string vin = vehicle.Vin ?? throw new PsdzException("IDENTITY", "The gateway did not provide a verified VIN.");
        if (request.ExpectedVin != null && request.ExpectedVin != vin)
            throw new PsdzException("IDENTITY", "The connected vehicle no longer matches the selected VIN.");
        vehicle.Disconnect();
        request = request with { ExpectedVin = vin };
        start.ArgumentList[^1] = vin;
        var coding = ParseResponse(await PsdzCafReader.RunProcessAsync(start, cancellationToken).ConfigureAwait(false), request, true);
        await vehicle.ConnectAsync(host.ToString(), request.Port, cancellationToken).ConfigureAwait(false);
        if (vehicle.Vin != vin)
            throw new PsdzException("IDENTITY", "The vehicle changed during the coding read; the result was rejected.");
        foreach (var block in coding.Blocks)
        {
            var reading = await vehicle.ReadDidAsync(request.Ecu, checked((ushort)block.Address),
                cancellationToken).ConfigureAwait(false);
            VerifyNativeBlock(block, reading);
        }
        return coding;
    }

    internal static void VerifyNativeBlock(CodingBlock block, DiagnosticReading reading)
    {
        if (reading.Status != "ok")
            throw new PsdzException("VERIFICATION", $"Could not independently read coding block {block.Address:X4}: {reading.Error}");
        if (!block.Hex.Equals(reading.PayloadHex, StringComparison.OrdinalIgnoreCase))
            throw new PsdzException("DATA_MISMATCH",
                $"PSdZ coding block {block.Address:X4} does not match the direct ECU read. Decoded results were rejected.");
    }

    public async Task<DecodedCoding> DecodeAsync(string ncdPath, string cafFile, CodingReadRequest settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(cafFile))
            throw new PsdzException("CONFIGURATION", "Offline decoding requires an exact CAF filename.");
        var request = settings with { CafFile = cafFile };
        Validate(request);
        ncdPath = Path.GetFullPath(ncdPath);
        if (!File.Exists(ncdPath) || new FileInfo(ncdPath).Length > 16 * 1024 * 1024)
            throw new PsdzException("CONFIGURATION", "Select an existing trusted NCD file no larger than 16 MiB.");
        using var view = await PsdzDataView.CreateAsync(request.IstaPath, request.PsdzDataPath, cancellationToken).ConfigureAwait(false);
        var start = PsdzCafReader.CreateOperationStartInfo(request.IstaPath, request.JavaPath, "decode-ncd", view.Root, ncdPath, cafFile);
        return ParseResponse(await PsdzCafReader.RunProcessAsync(start, cancellationToken).ConfigureAwait(false), request, false);
    }

    private static void Validate(CodingReadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IstaPath) || string.IsNullOrWhiteSpace(request.JavaPath) ||
            string.IsNullOrWhiteSpace(request.PsdzDataPath) || !Regex.IsMatch(request.Series, @"\A[A-Z][0-9A-Z]{3}\z") ||
            request.CafFile != null && !Regex.IsMatch(request.CafFile, @"\Acafd_[0-9a-f]{8}\.caf\.[0-9]{3}_[0-9]{3}_[0-9]{3}\z", RegexOptions.IgnoreCase) ||
            request.ExpectedVin != null && !Regex.IsMatch(request.ExpectedVin, @"\A[A-HJ-NPR-Z0-9]{17}\z"))
            throw new PsdzException("CONFIGURATION", "Configure ISTA, Java 8, PSdZData, series and an optional exact CAF filename/verified VIN.");
    }

    internal static DecodedCoding ParseResponse(PsdzCafReader.ProcessResult response, CodingReadRequest request, bool live)
    {
        try
        {
            using var json = JsonDocument.Parse(response.Output);
            var root = json.RootElement;
            if (root.GetProperty("schema").GetString() != "bimmeross.psdz/1")
                throw new PsdzException("PROTOCOL", "Unsupported PSdZ response schema.");
            if (!root.GetProperty("ok").GetBoolean())
                throw new PsdzException("RUNTIME", root.GetProperty("error").GetString() ?? "PSdZ coding read failed.");
            if (response.ExitCode != 0 || root.GetProperty("kind").GetString() != "coding")
                throw new PsdzException("PROTOCOL", "PSdZ did not complete a coding-data read.");
            int? ecu = root.GetProperty("ecu").ValueKind == JsonValueKind.Null ? null : root.GetProperty("ecu").GetInt32();
            string? vin = root.GetProperty("vin").GetString();
            string caf = root.GetProperty("caf_file").GetString() ?? throw new FormatException("No CAF identity returned.");
            if (live && (ecu != request.Ecu || vin == null || !Regex.IsMatch(vin, @"\A[A-HJ-NPR-Z0-9]{17}\z") ||
                request.ExpectedVin != null && request.ExpectedVin != vin) ||
                !live && (ecu != null || vin != null) ||
                request.CafFile != null && !request.CafFile.Equals(caf, StringComparison.OrdinalIgnoreCase))
                throw new PsdzException("IDENTITY", "Coding response does not match the requested vehicle, ECU or CAF.");
            string variant = root.GetProperty("base_variant").GetString() ?? "";
            if (live && string.IsNullOrWhiteSpace(variant))
                throw new PsdzException("IDENTITY", "Coding response has no observed ECU base variant.");
            var blocks = root.GetProperty("blocks").EnumerateArray().Select(b => new CodingBlock(
                b.GetProperty("address").GetInt32(), b.GetProperty("hex").GetString() ?? throw new FormatException("Missing coding bytes.")));
            return DecodedCoding.Parse(caf, variant, vin, ecu,
                root.GetProperty("version").GetString() ?? throw new FormatException("No initialized runtime version."),
                root.GetProperty("ncd").GetString() ?? throw new FormatException("No original NCD."),
                root.GetProperty("xml").GetString() ?? throw new FormatException("No decoded FDL."),
                blocks, root.GetProperty("verified_blocks").EnumerateArray().Select(b => b.GetInt32()),
                response.Diagnostics.Length > 16000 ? response.Diagnostics[^16000..] : response.Diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or XmlException)
        {
            throw new PsdzException("PROTOCOL", $"Invalid coding-read response: {ex.Message}");
        }
    }
}
