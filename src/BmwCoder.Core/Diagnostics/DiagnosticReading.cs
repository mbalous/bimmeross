using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BmwCoder.Core.Diagnostics;

public sealed record DiagnosticDefinition(ushort Did, string Name)
{
    public override string ToString() => $"0x{Did:X4} - {Name}";
}

public sealed record DiagnosticFault(string Code, byte Status)
{
    public const byte FailureStatusMask = 0xAF;
    public string? Description { get; init; }
    public string DescriptionStatus { get; init; } = "not_configured";
    public string? DescriptionSource { get; init; }
    public string? SourceDocumentId { get; init; }
    public bool TestFailed => (Status & 0x01) != 0;
    public bool TestFailedThisCycle => (Status & 0x02) != 0;
    public bool Pending => (Status & 0x04) != 0;
    public bool Confirmed => (Status & 0x08) != 0;
    public bool TestNotCompletedSinceClear => (Status & 0x10) != 0;
    public bool TestFailedSinceClear => (Status & 0x20) != 0;
    public bool TestNotCompletedThisCycle => (Status & 0x40) != 0;
    public bool WarningIndicatorRequested => (Status & 0x80) != 0;
    public bool HasFailureEvidence => (Status & FailureStatusMask) != 0;
}

public sealed record SoftwareComponent(byte ProcessClass, string Id, string Version);

public sealed class DiagnosticReading
{
    public byte Ecu { get; init; }
    public ushort? Did { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Status { get; init; } = "ok";
    public string RequestHex { get; init; } = string.Empty;
    public string ResponseHex { get; init; } = string.Empty;
    public string PayloadHex { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
    public string? Nrc { get; init; }
    public string? DecoderSource { get; set; }
    public Dictionary<string, string> Values { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<DiagnosticFault> Faults { get; } = new();
    public List<SoftwareComponent> Software { get; } = new();
}

public static class DiagnosticDecoder
{
    public static DiagnosticReading Raw(byte ecu, ushort did, byte[] payload)
    {
        var reading = new DiagnosticReading
        {
            Ecu = ecu,
            Did = did,
            Name = DiagnosticCatalog.DmeReads.FirstOrDefault(d => d.Did == did)?.Name ?? $"DID 0x{did:X4}",
            RequestHex = $"22{did:X4}",
            ResponseHex = $"62{did:X4}{Convert.ToHexString(payload)}",
            PayloadHex = Convert.ToHexString(payload),
            Sha256 = Convert.ToHexString(SHA256.HashData(payload))
        };
        reading.Values["Length (bytes)"] = payload.Length.ToString(CultureInfo.InvariantCulture);
        return reading;
    }

    public static DiagnosticReading Decode(byte ecu, ushort did, byte[] payload)
    {
        var reading = Raw(ecu, did, payload);
        if (did is 0xF190 or 0xF18C)
        {
            reading.Values[did == 0xF190 ? "VIN" : "Serial number"] = Encoding.ASCII.GetString(payload).Trim('\0', ' ');
        }

        if (ecu != 0x12)
        {
            return reading;
        }

        switch (did)
        {
            case 0x4AB4:
                RequireLength(payload, 4);
                uint ticks = BinaryPrimitives.ReadUInt32BigEndian(payload);
                reading.Values["Raw ticks"] = ticks.ToString(CultureInfo.InvariantCulture);
                reading.Values["Operating time (minutes, inferred)"] = (ticks / 600m).ToString("0.###", CultureInfo.InvariantCulture);
                reading.Values["Operating time (hours, inferred)"] = (ticks / 36000m).ToString("0.###", CultureInfo.InvariantCulture);
                reading.Warnings.Add("100-ms tick scale inferred from the owner's approximate ISTA observation; not independently confirmed by ODX.");
                break;
            case 0xF18B:
                RequireLength(payload, 3);
                var date = new DateTime(2000 + Bcd(payload[0]), Bcd(payload[1]), Bcd(payload[2]));
                reading.Values["Manufacturing date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case 0x2502:
                RequireLength(payload, 4);
                reading.Values["Programming counter"] = BinaryPrimitives.ReadUInt32BigEndian(payload).ToString(CultureInfo.InvariantCulture);
                break;
            case 0x2503:
                RequireLength(payload, 2);
                reading.Values["Programming counter maximum"] = BinaryPrimitives.ReadUInt16BigEndian(payload).ToString(CultureInfo.InvariantCulture);
                break;
            case 0x2541:
                RequireLength(payload, 20);
                reading.Values["Calibration ID"] = Encoding.ASCII.GetString(payload, 0, 16).TrimEnd('\0', ' ');
                reading.Values["CVN"] = Convert.ToHexString(payload.AsSpan(16, 4));
                reading.Warnings.Add("Calibration ID and CVN alone do not establish whether software is stock or modified.");
                break;
            case 0xF186:
                if (payload.Length < 1)
                    throw new FormatException("Empty session-state response.");
                reading.Values["Session"] = payload[0] switch
                {
                    1 => "Default (0x01)",
                    2 => "Programming (0x02)",
                    3 => "Extended (0x03)",
                    _ => $"0x{payload[0]:X2}"
                };
                reading.Values["Full session state (hex)"] = Convert.ToHexString(payload);
                break;
            case 0xF101:
                if (payload.Length < 17 || payload[0] != 1 || payload[1] != 1 ||
                    payload.Length != 17 + 8 * BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2)))
                    throw new FormatException("Unrecognized SVK layout; raw data is preserved.");
                for (int offset = 17; offset < payload.Length; offset += 8)
                {
                    reading.Software.Add(new SoftwareComponent(payload[offset],
                        Convert.ToHexString(payload.AsSpan(offset + 1, 4)),
                        $"{payload[offset + 5]}.{payload[offset + 6]}.{payload[offset + 7]}"));
                }
                reading.Warnings.Add("SVK layout follows the observed DME response; exact ECU variant and stock software status are not verified.");
                break;
        }

        return reading;
    }

    public static void DecodeFaults(DiagnosticReading reading, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || (data.Length - 1) % 4 != 0)
            throw new FormatException("Invalid DTC status-mask response length.");
        reading.Values["Status availability mask"] = $"0x{data[0]:X2}";
        for (int offset = 1; offset < data.Length; offset += 4)
            reading.Faults.Add(new DiagnosticFault(Convert.ToHexString(data.Slice(offset, 3)), data[offset + 3]));
        reading.Values["DTC status records returned"] = reading.Faults.Count.ToString(CultureInfo.InvariantCulture);
        reading.Values["Failure/history records"] = reading.Faults.Count(f => f.HasFailureEvidence).ToString(CultureInfo.InvariantCulture);
        reading.Values["Test-status-only records (not faults)"] = reading.Faults.Count(f => !f.HasFailureEvidence).ToString(CultureInfo.InvariantCulture);
    }

    private static void RequireLength(byte[] payload, int expected)
    {
        if (payload.Length != expected)
            throw new FormatException($"Expected {expected} data bytes, received {payload.Length}.");
    }

    private static int Bcd(byte value)
    {
        if ((value >> 4) > 9 || (value & 15) > 9)
            throw new FormatException("Invalid BCD date.");
        return (value >> 4) * 10 + (value & 15);
    }
}
