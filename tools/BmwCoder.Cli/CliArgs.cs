namespace BmwCoder.Cli;

public sealed class CliArgs
{
    public string Command { get; init; } = string.Empty;
    public string Host { get; init; } = "169.254.16.17";
    public int Port { get; init; } = 6801;
    public int TimeoutMs { get; init; } = 5000;
    public bool Human { get; init; }
    public string? LogFile { get; init; }
    public byte Ecu { get; init; }
    public bool HasEcu { get; init; }
    public ushort Did { get; init; }
    public bool HasDid { get; init; }
    public ushort From { get; init; }
    public bool HasFrom { get; init; }
    public ushort To { get; init; }
    public bool HasTo { get; init; }
    public int Step { get; init; } = 1;
    public int MaxDids { get; init; } = 64;
    public byte[] Pdu { get; init; } = Array.Empty<byte>();
    public string? OutPath { get; init; }
    public string? LocalIp { get; init; }
    public string? FilePath { get; init; }
    public string? IstaPath { get; init; }
    public string? JavaPath { get; init; }
    public string? DataPath { get; init; }
    public string Series { get; init; } = "F020";
    public string? CafFile { get; init; }
    public string? Model { get; init; }
    public bool HasHost { get; init; }

    public static CliArgs Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            throw new CliUsageException("Missing command. Expected read-coding, decode, caf, gateways, discover, vin, read, dump, info, raw, or scan.");
        }

        string command = args[0].ToLowerInvariant();
        if (command is not ("read-coding" or "decode" or "caf" or "compare-model" or "gateways" or "discover" or "vin" or "read" or "dump" or "info" or "raw" or "scan"))
        {
            throw new CliUsageException($"Unknown command '{args[0]}'.");
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (token is "--human" or "--json")
            {
                map[token] = "true";
                continue;
            }

            if (!token.StartsWith("--") || i + 1 >= args.Length)
            {
                throw new CliUsageException($"Unexpected argument '{token}'.");
            }

            map[token] = args[++i];
        }

        var parsed = new CliArgs
        {
            Command = command,
            Host = Value(map, "--host") ?? "169.254.16.17",
            Port = ParseInt(Value(map, "--port"), "--port", 6801),
            TimeoutMs = ParseInt(Value(map, "--timeout"), "--timeout", command is "compare-model" ? 180000 : command is "read-coding" or "decode" ? 90000 : 5000),
            Human = map.ContainsKey("--human"),
            LogFile = Value(map, "--log"),
            HasEcu = map.ContainsKey("--ecu"),
            Ecu = map.ContainsKey("--ecu") ? ParseByte(map["--ecu"], "--ecu") : (byte)0,
            HasDid = map.ContainsKey("--did"),
            Did = map.ContainsKey("--did") ? ParseUShort(map["--did"], "--did") : (ushort)0,
            HasFrom = map.ContainsKey("--from"),
            From = map.ContainsKey("--from") ? ParseUShort(map["--from"], "--from") : (ushort)0,
            HasTo = map.ContainsKey("--to"),
            To = map.ContainsKey("--to") ? ParseUShort(map["--to"], "--to") : (ushort)0,
            Step = ParseInt(Value(map, "--step"), "--step", 1),
            MaxDids = ParseInt(Value(map, "--max-dids"), "--max-dids", 64),
            Pdu = map.ContainsKey("--pdu") ? ParseHex(map["--pdu"]) : Array.Empty<byte>(),
            OutPath = Value(map, "--out"),
            LocalIp = Value(map, "--local-ip"),
            FilePath = Value(map, "--file"),
            IstaPath = Value(map, "--ista"),
            JavaPath = Value(map, "--java"),
            DataPath = Value(map, "--data"),
            Series = Value(map, "--series") ?? "F020",
            CafFile = Value(map, "--caf"),
            Model = Value(map, "--model"),
            HasHost = map.ContainsKey("--host")
        };

        Validate(parsed);
        return parsed;
    }

    private static void Validate(CliArgs args)
    {
        if (args.Command is "read-coding" or "decode")
        {
            if (string.IsNullOrWhiteSpace(args.IstaPath) || string.IsNullOrWhiteSpace(args.JavaPath) ||
                string.IsNullOrWhiteSpace(args.DataPath))
                throw new CliUsageException("Coding reads/decoding require --ista <root> --java <Java 8 executable> --data <PSdZData directory>.");
            if (args.Command == "read-coding" && !args.HasHost)
                throw new CliUsageException("read-coding requires an explicitly selected --host <gateway IPv4>.");
            if (args.Command == "decode" && (string.IsNullOrWhiteSpace(args.FilePath) || string.IsNullOrWhiteSpace(args.CafFile)))
                throw new CliUsageException("decode requires --file <NCD> --caf <exact CAF filename>.");
        }
        if (args.Command == "compare-model")
        {
            if (string.IsNullOrWhiteSpace(args.IstaPath) || string.IsNullOrWhiteSpace(args.JavaPath) ||
                string.IsNullOrWhiteSpace(args.DataPath))
                throw new CliUsageException("compare-model requires --ista <root> --java <Java 8 executable> --data <PSdZData directory>.");
            if (string.IsNullOrWhiteSpace(args.Model))
                throw new CliUsageException("compare-model requires --model <name from models_f8x.json, e.g. F82GTS>.");
            bool offline = !string.IsNullOrWhiteSpace(args.FilePath);
            if (offline && string.IsNullOrWhiteSpace(args.CafFile))
                throw new CliUsageException("Offline compare-model requires --file <NCD> --caf <exact CAF filename>.");
            if (!offline && (!args.HasHost || !args.HasEcu))
                throw new CliUsageException("Live compare-model requires an explicit --host and --ecu (or use --file <NCD> --caf <CAF>).");
        }
        if (args.Command == "caf" && (string.IsNullOrWhiteSpace(args.FilePath) ||
            string.IsNullOrWhiteSpace(args.IstaPath) || string.IsNullOrWhiteSpace(args.JavaPath)))
            throw new CliUsageException("caf requires --file <local CAF> --ista <ISTA directory> --java <64-bit Java 8 java.exe>.");
        if (args.Command == "gateways" &&
            (!System.Net.IPAddress.TryParse(args.LocalIp, out var local) ||
             local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
             local.Equals(System.Net.IPAddress.Any) || args.TimeoutMs > 30000))
            throw new CliUsageException("gateways requires --local-ip <interface IPv4> and --timeout at most 30000 ms.");
        if (args.TimeoutMs < 1)
        {
            throw new CliUsageException("--timeout must be at least 1.");
        }

        if (args.Step < 1)
        {
            throw new CliUsageException("--step must be at least 1.");
        }

        if (args.Command is "read-coding" or "read" or "dump" or "info" or "raw" or "scan" && !args.HasEcu)
        {
            throw new CliUsageException($"--ecu is required for {args.Command}.");
        }

        if (args.Command is "read" or "dump" && !args.HasDid)
        {
            throw new CliUsageException($"--did is required for {args.Command}.");
        }

        if (args.Command == "dump" && string.IsNullOrWhiteSpace(args.OutPath))
        {
            throw new CliUsageException("--out is required for dump.");
        }

        if (args.Command == "raw" && args.Pdu.Length == 0)
        {
            throw new CliUsageException("--pdu is required for raw.");
        }

        if (args.Command == "scan" && (!args.HasFrom || !args.HasTo))
        {
            throw new CliUsageException("--from and --to are required for scan.");
        }
    }

    private static string? Value(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out string? value) ? value : null;

    private static int ParseInt(string? text, string name, int fallback)
    {
        if (text == null)
        {
            return fallback;
        }

        if (!int.TryParse(text, out int value))
        {
            throw new CliUsageException($"{name} must be an integer.");
        }

        return value;
    }

    public static byte ParseByte(string text, string name)
    {
        text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out byte value))
        {
            throw new CliUsageException($"{name} must be a hex byte, for example 0x63.");
        }

        return value;
    }

    public static ushort ParseUShort(string text, string name)
    {
        text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ushort value))
        {
            throw new CliUsageException($"{name} must be a hex word, for example 0x3000.");
        }

        return value;
    }

    public static byte[] ParseHex(string text)
    {
        string compact = text.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (compact.Length == 0 || compact.Length % 2 != 0)
        {
            throw new CliUsageException("--pdu must be an even number of hex digits.");
        }

        var bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(compact.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                throw new CliUsageException("--pdu contains a non-hex digit.");
            }
        }

        return bytes;
    }
}

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}
