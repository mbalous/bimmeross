using System.Text.Json;
using BmwCoder.Coding;
using BmwCoder.Core;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using BmwCoder.Transport;
using BmwCoder.Uds;
using BmwCoder.Psdz;
using Microsoft.Extensions.Logging;

namespace BmwCoder.Cli;

public static class Program
{
    public const int MinRequestGapMs = 20;

    public static async Task<int> Main(string[] args)
    {
        CliArgs parsed;
        try
        {
            parsed = CliArgs.Parse(args);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(JsonEnvelope.Fail("usage", "USAGE", ex.Message));
            return 2;
        }

        using ILoggerFactory factory = BuildLogging(parsed.LogFile);
        var logger = factory.CreateLogger("bmwcoder");

        try
        {
            if (parsed.Command is "read-coding" or "decode")
            {
                using var timeout = new CancellationTokenSource(parsed.TimeoutMs);
                var request = new CodingReadRequest(parsed.Host, parsed.Port, parsed.Series, parsed.Ecu,
                    parsed.DataPath!, parsed.IstaPath!, parsed.JavaPath!, parsed.CafFile);
                var reader = new PsdzCodingReader();
                var coding = parsed.Command == "read-coding" ?
                    await reader.ReadAsync(request, timeout.Token).ConfigureAwait(false) :
                    await reader.DecodeAsync(parsed.FilePath!, parsed.CafFile!, request, timeout.Token).ConfigureAwait(false);
                if (parsed.OutPath != null)
                    coding.ExportOriginal(parsed.OutPath);
                var notes = CodingAnnotations.Load(
                    Environment.GetEnvironmentVariable("BIMMEROSS_ANNOTATIONS") ?? Path.Combine(AppContext.BaseDirectory, "annotations"),
                    coding.CafFile);
                WriteOut(parsed.Human, JsonEnvelope.Ok(parsed.Command, new
                {
                    ecu = coding.Ecu,
                    base_variant = coding.BaseVariant,
                    caf_file = coding.CafFile,
                    parameter_count = coding.Parameters.Count,
                    named_parameter_count = coding.Parameters.Count(p => p.Name != null),
                    decoded_parameter_count = coding.Parameters.Count(p => p.IsDecoded),
                    opaque_blocks = coding.OpaqueBlocks,
                    ncd_sha256 = coding.Sha256,
                    read_only = true,
                    annotations = notes?.Source,
                    parameters = coding.Parameters.Select(p =>
                    {
                        var note = notes?.Find(coding.CafFile, p);
                        return new
                        {
                            p.Id, p.BlockAddress, p.Name, p.Value, p.Interpretations, p.Start, p.End, p.Mask, p.IsDecoded,
                            label = note?.Label, meaning = note?.Meaning, label_note = note?.Note,
                            block_label = notes != null && notes.BlockNames.TryGetValue(p.BlockAddress, out var blockName) ? blockName : null,
                            label_verified_version = note?.VersionVerified
                        };
                    })
                }, new[] { "Read-only decoded values. Missing/trimmed names remain unknown; fields without current SDK values are unavailable. Opaque blocks are preserved; writes and full-document regeneration are not supported." }));
                return 0;
            }
            if (parsed.Command == "compare-model")
            {
                using var timeout = new CancellationTokenSource(parsed.TimeoutMs);
                string annotationDir = Environment.GetEnvironmentVariable("BIMMEROSS_ANNOTATIONS") ?? Path.Combine(AppContext.BaseDirectory, "annotations");
                var profiles = ModelProfiles.Load(annotationDir)
                    ?? throw new CliUsageException($"models_f8x.json not found in {annotationDir}.");
                if (!profiles.Models.TryGetValue(parsed.Model!, out var profile))
                    throw new CliUsageException($"Unknown model '{parsed.Model}'. Known: {string.Join(", ", profiles.Models.Keys)}.");
                var request = new CodingReadRequest(parsed.Host, parsed.Port, parsed.Series, parsed.Ecu,
                    parsed.DataPath!, parsed.IstaPath!, parsed.JavaPath!, parsed.CafFile);
                var reader = new PsdzCodingReader();
                var coding = string.IsNullOrWhiteSpace(parsed.FilePath)
                    ? await reader.ReadAsync(request, timeout.Token).ConfigureAwait(false)
                    : await reader.DecodeAsync(parsed.FilePath!, parsed.CafFile!, request, timeout.Token).ConfigureAwait(false);
                string cafPath = Path.Combine(parsed.DataPath!, "swe", "cafd", coding.CafFile);
                if (!File.Exists(cafPath))
                    throw new CliUsageException($"CAF {coding.CafFile} not found under {Path.Combine(parsed.DataPath!, "swe", "cafd")}.");
                var definition = await new PsdzCafReader().ReadAsync(cafPath, parsed.IstaPath!, parsed.JavaPath!, timeout.Token).ConfigureAwait(false);
                var notes = CodingAnnotations.Load(annotationDir, coding.CafFile);
                var actual = coding.Parameters.ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
                var rows = new List<object>();
                int mismatches = 0, unknown = 0;
                foreach (var fn in CafRules.Extract(definition))
                {
                    if (!fn.BySeries.TryGetValue(profiles.Series, out var options) || !options.Any(o => o.Expression != null))
                        continue;
                    var perModel = profiles.Models.ToDictionary(m => m.Key, m => fn.Resolve(profiles.Series, m.Value.Context)?.Value);
                    if (perModel.Values.Distinct().Count() < 2)
                        continue;
                    string? expected = perModel[profile.Name];
                    var live = actual.GetValueOrDefault(fn.Id);
                    var note = live == null ? null : notes?.Find(coding.CafFile, live);
                    var described = notes?.Describe(fn.Block, fn.Start, fn.End, fn.Mask, expected ?? string.Empty);
                    var byModelMeaning = perModel.ToDictionary(kv => kv.Key,
                        kv => kv.Value == null ? null : notes?.Describe(fn.Block, fn.Start, fn.End, fn.Mask, kv.Value)?.Meaning);
                    bool? match = expected == null || live == null ? null : string.Equals(expected, live.Value, StringComparison.OrdinalIgnoreCase);
                    if (match == false) mismatches++;
                    else if (match == null) unknown++;
                    rows.Add(new
                    {
                        id = fn.Id, label = note?.Label ?? described?.Name, mask = fn.Mask,
                        actual = live?.Value, actual_meaning = note?.Meaning,
                        expected, expected_meaning = described?.Meaning, match,
                        by_model = perModel, by_model_meaning = byModelMeaning
                    });
                }
                WriteOut(parsed.Human, JsonEnvelope.Ok(parsed.Command, new
                {
                    ecu = coding.Ecu,
                    caf_file = coding.CafFile,
                    model = profile.Name,
                    model_label = profile.Label,
                    rule_series = profiles.Series,
                    profile_source = profiles.Source,
                    model_dependent_parameters = rows.Count,
                    mismatches,
                    undetermined = unknown,
                    read_only = true,
                    parameters = rows
                }, new[] { "Read-only comparison. Expected values come from CAF selection rules and inferred criteria mappings (see models_f8x.json); mismatches are informational, not errors." }));
                return 0;
            }
            if (parsed.Command == "caf")
            {
                using var timeout = new CancellationTokenSource(parsed.TimeoutMs);
                var definition = await new PsdzCafReader().ReadAsync(parsed.FilePath!, parsed.IstaPath!,
                    parsed.JavaPath!, timeout.Token).ConfigureAwait(false);
                WriteOut(parsed.Human, JsonEnvelope.Ok(parsed.Command, new
                {
                    name = definition.Name,
                    runtime = definition.RuntimeVersion,
                    field_count = definition.Fields.Count,
                    fields = string.IsNullOrWhiteSpace(parsed.OutPath) ? null : definition.Fields,
                    read_only = true,
                    ncd_mapping_verified = false
                }, definition.Diagnostics.Length == 0 ? Array.Empty<string>() :
                    new[] { "PSdZ emitted runtime diagnostics; successful CAF import does not qualify other backend capabilities." }));
                return 0;
            }
            if (parsed.Command == "gateways")
            {
                var network = EnetDiscovery.GetInterfaces().FirstOrDefault(n => n.Address.ToString() == parsed.LocalIp)
                    ?? throw new CliUsageException("The selected --local-ip is not assigned to an active IPv4 interface.");
                var found = await new EnetDiscovery().DiscoverAsync(network.Address, network.Broadcast,
                    TimeSpan.FromMilliseconds(parsed.TimeoutMs)).ConfigureAwait(false);
                WriteOut(parsed.Human, JsonEnvelope.Ok(parsed.Command, new
                {
                    gateways = found.Gateways.Select(g => new { host = g.Address.ToString(), port = 6801 }),
                    local_ip = network.Address.ToString()
                }, found.Warnings));
                return 0;
            }
            var connection = new HsfzConnection(factory.CreateLogger<HsfzConnection>())
            {
                Timeout = TimeSpan.FromMilliseconds(parsed.TimeoutMs)
            };
            var uds = new PacedUdsClient(new UdsClient(connection, logger: factory.CreateLogger<UdsClient>()), MinRequestGapMs);
            using var session = new CodingSession(connection, uds, logger: factory.CreateLogger<CodingSession>());
            var settings = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(settings.PsdzDataPath))
                session.DiagnosticData = PsdzDiagnosticData.Load(settings.PsdzDataPath, settings.PsdzSeries);
            using var cts = new CancellationTokenSource(parsed.TimeoutMs);

            await session.ConnectAsync(parsed.Host, parsed.Port, cts.Token).ConfigureAwait(false);
            object data = await RunAsync(session, parsed, cts.Token).ConfigureAwait(false);
            WriteOut(parsed.Human, JsonEnvelope.Ok(parsed.Command, data, session.DiscoveryWarnings));
            return 0;
        }
        catch (PsdzException ex)
        {
            WriteErr(parsed, "PSDZ_" + ex.Code, ex.Message);
            return 1;
        }
        catch (CliUsageException ex)
        {
            WriteErr(parsed, "USAGE", ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            WriteErr(parsed, "TIMEOUT", $"Command exceeded {parsed.TimeoutMs} ms.");
            return 1;
        }
        catch (TimeoutException ex)
        {
            WriteErr(parsed, "TIMEOUT", ex.Message);
            return 1;
        }
        catch (UdsNegativeResponseException ex)
        {
            WriteErr(parsed, "UDS_NRC", ex.Message, $"0x{(byte)ex.Nrc:X2}");
            return 1;
        }
        catch (HsfzException ex)
        {
            WriteErr(parsed, "TRANSPORT", ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command {Command} failed.", parsed.Command);
            WriteErr(parsed, "INTERNAL", ex.Message);
            return 1;
        }
    }

    public static async Task<object> RunAsync(CodingSession session, CliArgs args, CancellationToken cancellationToken)
    {
        switch (args.Command)
        {
            case "discover":
                var ecus = await session.DiscoverEcusAsync(
                    cancellationToken: cancellationToken, probeTimeout: TimeSpan.FromMilliseconds(args.TimeoutMs),
                    discoveryBudget: TimeSpan.FromMilliseconds(args.TimeoutMs)).ConfigureAwait(false);
                return new
                {
                    ecus = ecus.Select(e => new
                    {
                        addr = $"0x{e.Address:X2}",
                        name = e.Name,
                        responds = e.Responds,
                        rtt_ms = e.RttMs,
                        source = e.DiscoverySource,
                        name_source = e.NameSource,
                        probe_error = e.ProbeError,
                        writable = e.IsWritable
                    })
                };
            case "vin":
                string? vin = await session.ReadVinAsync(cancellationToken).ConfigureAwait(false);
                return new { vin, ecu = "0x10", did = "0xF190" };
            case "read":
            case "dump":
                var block = await session.ReadBlockAsync(args.Ecu, args.Did, cancellationToken).ConfigureAwait(false);
                if (args.Command == "dump")
                {
                    string outPath = Path.GetFullPath(args.OutPath!);
                    string? dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    await File.WriteAllBytesAsync(outPath, block.RawData, cancellationToken).ConfigureAwait(false);
                    return new
                    {
                        ecu = $"0x{block.EcuAddress:X2}",
                        did = $"0x{block.Did:X4}",
                        length = block.RawData.Length,
                        sha256 = block.ComputeSha256(),
                        path = outPath
                    };
                }

                return new
                {
                    ecu = $"0x{block.EcuAddress:X2}",
                    did = $"0x{block.Did:X4}",
                    length = block.RawData.Length,
                    sha256 = block.ComputeSha256(),
                    hex = Convert.ToHexString(block.RawData)
                };
            case "info":
                var info = await session.GetInfoAsync(args.Ecu, cancellationToken).ConfigureAwait(false);
                return new
                {
                    addr = $"0x{info.Address:X2}",
                    name = info.Name,
                    description = info.Description,
                    responds = info.Responds,
                    rtt_ms = info.RttMs,
                    writable = info.IsWritable,
                    vin = info.Vin
                };
            case "raw":
                var response = await session.SendRawAsync(args.Ecu, args.Pdu, cancellationToken).ConfigureAwait(false);
                return new
                {
                    request = Convert.ToHexString(args.Pdu),
                    response = Convert.ToHexString(BuildResponsePdu(response)),
                    positive = response.IsPositive,
                    nrc = response.Nrc == null ? null : $"0x{(byte)response.Nrc:X2}",
                    nrc_message = response.Nrc is { } code ? NrcDescriptions.Describe(code) : null
                };
            case "scan":
                var hits = await session.ScanDidsAsync(args.Ecu, args.From, args.To, args.Step, args.MaxDids, cancellationToken).ConfigureAwait(false);
                return new
                {
                    ecu = $"0x{args.Ecu:X2}",
                    results = hits.Select(h => new
                    {
                        did = $"0x{h.Did:X4}",
                        status = h.Status,
                        len = h.Length,
                        nrc = h.Nrc == null ? null : $"0x{h.Nrc:X2}"
                    })
                };
            default:
                throw new CliUsageException($"Unknown command '{args.Command}'.");
        }
    }

    private static byte[] BuildResponsePdu(UdsResponse response)
    {
        if (!response.IsPositive)
        {
            return new byte[] { 0x7F, response.OriginalSid, (byte)(response.Nrc ?? Nrc.GeneralReject) };
        }

        var pdu = new byte[1 + response.Data.Length];
        pdu[0] = response.ResponseSid;
        response.Data.CopyTo(pdu, 1);
        return pdu;
    }

    private static ILoggerFactory BuildLogging(string? logFile)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new StderrLogProvider());
            if (!string.IsNullOrWhiteSpace(logFile))
            {
                builder.AddProvider(new FileLogProvider(logFile));
            }
        });
    }

    private static void WriteOut(bool human, string json)
    {
        Console.Out.WriteLine(human ? Humanize(json) : json);
    }

    private static void WriteErr(CliArgs args, string code, string message, string? nrc = null)
    {
        string json = JsonEnvelope.Fail(args.Command, code, message, nrc);
        Console.Error.WriteLine(args.Human ? Humanize(json) : json);
    }

    private static string Humanize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
}

internal sealed class PacedUdsClient : IUdsClient
{
    private readonly IUdsClient _inner;
    private readonly int _gapMs;
    private DateTime _next = DateTime.MinValue;

    public PacedUdsClient(IUdsClient inner, int gapMs)
    {
        _inner = inner;
        _gapMs = gapMs;
    }

    private async Task Pace(CancellationToken cancellationToken)
    {
        var wait = _next - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        _next = DateTime.UtcNow.AddMilliseconds(_gapMs);
    }

    public async Task<UdsResponse> SendRawAsync(byte target, byte[] pdu, CancellationToken cancellationToken = default)
    {
        await Pace(cancellationToken).ConfigureAwait(false);
        return await _inner.SendRawAsync(target, pdu, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UdsResponse> SendAsync(UdsRequest request, CancellationToken cancellationToken = default)
    {
        await Pace(cancellationToken).ConfigureAwait(false);
        return await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task DiagnosticSessionControlAsync(byte target, byte sessionType, CancellationToken cancellationToken = default) =>
        _inner.DiagnosticSessionControlAsync(target, sessionType, cancellationToken);

    public Task<byte[]> ReadDataByIdentifierAsync(byte target, ushort did, CancellationToken cancellationToken = default) =>
        _inner.ReadDataByIdentifierAsync(target, did, cancellationToken);

    public Task WriteDataByIdentifierAsync(byte target, ushort did, byte[] data, CancellationToken cancellationToken = default) =>
        _inner.WriteDataByIdentifierAsync(target, did, data, cancellationToken);

    public Task RoutineControlAsync(byte target, byte routineControlType, ushort routineId, byte[]? optionRecord = null, CancellationToken cancellationToken = default) =>
        _inner.RoutineControlAsync(target, routineControlType, routineId, optionRecord, cancellationToken);

    public Task EcuResetAsync(byte target, byte resetType, CancellationToken cancellationToken = default) =>
        _inner.EcuResetAsync(target, resetType, cancellationToken);

    public Task TesterPresentAsync(byte target, CancellationToken cancellationToken = default) =>
        _inner.TesterPresentAsync(target, cancellationToken);

    public void StartKeepAlive(byte target, TimeSpan? interval = null) => _inner.StartKeepAlive(target, interval);
    public void StopKeepAlive() => _inner.StopKeepAlive();
    public void Dispose() => _inner.Dispose();
}

internal sealed class StderrLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger();
    public void Dispose() { }

    private sealed class StderrLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Console.Error.WriteLine($"{logLevel}: {formatter(state, exception)}");
        }
    }
}

internal sealed class FileLogProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLogProvider(string path)
    {
        _path = path;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_path, _gate);
    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _path;
        private readonly object _gate;

        public FileLogger(string path, object gate)
        {
            _path = path;
            _gate = gate;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string line = $"{DateTime.UtcNow:O} {logLevel} {formatter(state, exception)}";
            if (exception != null)
            {
                line += " " + exception.Message;
            }

            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
    }
}
