using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwCoder.Cli;

public static class JsonEnvelope
{
    public const string Schema = "bmwcoder/1";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string Ok(string command, object data, IReadOnlyList<string>? warnings = null) =>
        Serialize(new Envelope(Schema, command, true, DateTime.UtcNow, data, warnings ?? Array.Empty<string>(), null));

    public static string Fail(string command, string code, string message, string? nrc = null) =>
        Serialize(new Envelope(Schema, command, false, DateTime.UtcNow, null, Array.Empty<string>(), new ErrorBody(code, message, nrc)));

    public static string Serialize(Envelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public sealed record Envelope(
        string Schema,
        string Command,
        bool Ok,
        DateTime Timestamp,
        object? Data,
        IReadOnlyList<string> Warnings,
        ErrorBody? Error);

    public sealed record ErrorBody(string Code, string Message, string? Nrc);
}
