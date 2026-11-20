using System.Buffers.Binary;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Xml;

namespace BmwCoder.Core;

public sealed partial class CodingSession
{
    public Task<DiagnosticReading> ReadFaultMemoryAsync(byte ecu, CancellationToken cancellationToken = default) =>
        ReadOnlyRequestAsync(ecu, new byte[] { 0x19, 0x02, DiagnosticFault.FailureStatusMask }, cancellationToken);

    public Task<DiagnosticReading> ProbeDiagnosticEcuAsync(byte ecu, CancellationToken cancellationToken = default) =>
        ReadOnlyRequestAsync(ecu, new byte[] { 0x3E, 0x00 }, cancellationToken);

    public async Task<DiagnosticReading> ReadDiagnosticDidAsync(
        byte ecu, ushort did, CancellationToken cancellationToken = default)
    {
        byte[] pdu = new byte[3];
        pdu[0] = 0x22;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), did);
        return await ReadOnlyRequestAsync(ecu, pdu, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticReading> ReadOnlyRequestAsync(
        byte ecu, byte[] pdu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        bool readDid = pdu.Length == 3 && pdu[0] == 0x22;
        bool faults = pdu.Length == 3 && pdu[0] == 0x19 && pdu[1] == 0x02;
        bool present = pdu.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x00 });
        if (!readDid && !faults && !present)
            throw new ArgumentException("Read-only diagnostics allow only a single DID read (22), DTC status read (19 02 <mask>), or TesterPresent (3E 00).", nameof(pdu));
        if (ecu == 0x12 && readDid && pdu[1] == 0xF1 && pdu[2] == 0x50)
            _observedDmeSgbdVerified = false;

        await ReconnectIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var response = await _uds.SendRawAsync(ecu, pdu, cancellationToken).ConfigureAwait(false);
        ushort? did = readDid ? BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)) : null;
        string name = readDid ? DiagnosticCatalog.DmeReads.FirstOrDefault(d => d.Did == did)?.Name ?? $"DID 0x{did:X4}"
            : faults ? "Fault memory" : "TesterPresent";
        if (response.SourceAddress != ecu || (response.IsPositive
                ? response.ResponseSid != pdu[0] + 0x40 : response.OriginalSid != pdu[0]))
            throw new HsfzException("Unexpected diagnostic response address or service.");
        if (!response.IsPositive)
        {
            return new DiagnosticReading
            {
                Ecu = ecu,
                Did = did,
                Name = name,
                Status = "nrc",
                RequestHex = Convert.ToHexString(pdu),
                ResponseHex = $"7F{response.OriginalSid:X2}{(byte)(response.Nrc ?? Nrc.GeneralReject):X2}{Convert.ToHexString(response.Data)}",
                Nrc = $"0x{(byte)(response.Nrc ?? Nrc.GeneralReject):X2}",
                Error = response.Nrc is { } nrc ? NrcDescriptions.Describe(nrc) : "Negative response"
            };
        }
        if (readDid && (response.Data.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(response.Data) != did))
            throw new HsfzException("Diagnostic response did not echo the requested DID.");
        if (!readDid && (response.Data.Length < 1 || response.Data[0] != pdu[1]))
            throw new HsfzException("Diagnostic response did not echo the requested subfunction.");

        byte[] payload = readDid ? response.Data.AsSpan(2).ToArray() : response.Data.AsSpan(1).ToArray();
        if (ecu == 0x12 && did == 0xF150)
            _observedDmeSgbdVerified = payload.AsSpan().SequenceEqual(new byte[] { 0x0F, 0x1E, 0xA0 });
        var reading = new DiagnosticReading
        {
            Ecu = ecu,
            Did = did,
            Name = name,
            RequestHex = Convert.ToHexString(pdu),
            ResponseHex = $"{response.ResponseSid:X2}{Convert.ToHexString(response.Data)}",
            PayloadHex = Convert.ToHexString(payload),
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
        };
        try
        {
            if (readDid)
            {
                if (DiagnosticData == null)
                {
                    var fallback = DiagnosticDecoder.Decode(ecu, did!.Value, payload);
                    fallback.DecoderSource = "Built-in fallback; ODX not loaded, ECU variant not verified";
                    fallback.Warnings.Add("Load the selected vehicle's PSdZData for data-driven decoding. Built-in interpretations are not variant-verified.");
                    return fallback;
                }
                var decoded = DiagnosticData.Decode(ecu, did!.Value, payload) ?? DiagnosticDecoder.Raw(ecu, did.Value, payload);
                if (decoded.DecoderSource == null)
                    decoded.Warnings.Add("No matching ODX read layout for this address and DID in the selected dataset. Raw bytes are preserved.");
                if (ecu == 0x12 && _observedDmeSgbdVerified)
                {
                    try
                    {
                        var observed = DiagnosticDecoder.Decode(ecu, did.Value, payload);
                        foreach (var pair in observed.Values.Where(p => p.Key != "Length (bytes)"))
                            decoded.Values.TryAdd($"Observed DME hint / {pair.Key}", pair.Value);
                        decoded.Software.AddRange(observed.Software);
                        decoded.Warnings.AddRange(observed.Warnings);
                        if (observed.Values.Count > 1 || observed.Software.Count > 0)
                            decoded.Warnings.Add("Observed DME hints apply only to the previously observed SGBD index 0F1EA0; they are not ODX-defined units/layouts.");
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
                    {
                        decoded.Warnings.Add($"Observed DME hint unavailable: {ex.Message} ODX fields and raw bytes are preserved.");
                        _logger.LogWarning(ex, "Observed DME hint did not match the response layout");
                    }
                }
                return decoded;
            }
            if (faults)
                DiagnosticDecoder.DecodeFaults(reading, payload);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IOException or XmlException or NotSupportedException)
        {
            reading.Warnings.Add($"Decoding unavailable: {ex.Message} Raw bytes are preserved.");
            _logger.LogWarning(ex, "Could not decode diagnostic response for {Name}", name);
        }
        if (faults && reading.Faults.Count > 0)
            await EnrichFaultsAsync(reading, cancellationToken).ConfigureAwait(false);
        return reading;
    }

    private async Task EnrichFaultsAsync(DiagnosticReading reading, CancellationToken cancellationToken)
    {
        var lookup = FaultDescriptions;
        if (lookup == null && reading.Faults.Any(f => f.HasFailureEvidence))
        {
            reading.Warnings.Add("ISTA fault texts are not configured. Codes and status are preserved; no descriptions are guessed.");
            return;
        }
        for (int index = 0; index < reading.Faults.Count; index++)
        {
            var fault = reading.Faults[index];
            if (!fault.HasFailureEvidence)
            {
                reading.Faults[index] = fault with { DescriptionStatus = "test_status_only" };
                continue;
            }
            if (lookup == null) continue;
            try
            {
                var matches = await Task.Run(() => lookup.Lookup(fault.Code, cancellationToken), cancellationToken).ConfigureAwait(false);
                reading.Faults[index] = fault with
                {
                    Description = matches.Count == 1 ? matches[0].Description : null,
                    DescriptionStatus = matches.Count == 1 ? "indexed_code_match" : matches.Count == 0 ? "not_found" : "ambiguous",
                    DescriptionSource = lookup.DatabasePath,
                    SourceDocumentId = matches.Count == 1 ? matches[0].DocumentId : null
                };
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or XmlException or TimeoutException)
            {
                reading.Faults[index] = fault with { DescriptionStatus = "lookup_error" };
                reading.Warnings.Add($"ISTA lookup failed for {fault.Code}: {ex.Message} Raw fault data is preserved.");
                _logger.LogWarning(ex, "ISTA lookup failed for fault {Code}", fault.Code);
            }
        }
        reading.Warnings.Add("ISTA descriptions are exact-code document matches, not ECU-variant-verified fault definitions. Missing or ambiguous text is not guessed.");
    }

    public async Task<IReadOnlyList<DiagnosticReading>> ReadDmeReportAsync(
        IProgress<DiagnosticReading>? progress = null, CancellationToken cancellationToken = default,
        bool stopOnDisconnect = false)
    {
        var readings = new List<DiagnosticReading>();
        await ReadOneAsync("22F150", "SGBD index", 0xF150).ConfigureAwait(false);
        foreach (var definition in DiagnosticCatalog.DmeReads.Where(d => d.Did != 0xF150))
        {
            if (stopOnDisconnect && !_connection.IsConnected)
                return readings;
            await ReadOneAsync($"22{definition.Did:X4}", definition.Name, definition.Did).ConfigureAwait(false);
        }
        if (!stopOnDisconnect || _connection.IsConnected)
            await ReadOneAsync($"1902{DiagnosticFault.FailureStatusMask:X2}", "Fault memory", null).ConfigureAwait(false);
        return readings;

        async Task ReadOneAsync(string request, string name, ushort? did)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connection.Timeout);
            DiagnosticReading reading;
            try
            {
                reading = await ReadOnlyRequestAsync(0x12, Convert.FromHexString(request), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is TimeoutException or OperationCanceledException or HsfzException)
            {
                _connection.Close();
                reading = new DiagnosticReading
                {
                    Ecu = 0x12,
                    Did = did,
                    Name = name,
                    RequestHex = request,
                    Status = ex is HsfzException ? "transport_error" : "timeout",
                    Error = ex.Message
                };
                _logger.LogWarning(ex, "DME report read failed: {Name}", name);
            }
            readings.Add(reading);
            progress?.Report(reading);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }
}
