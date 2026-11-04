using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using BmwCoder.Core.Workspace;

namespace BmwCoder.Psdz;

public sealed class PsdzException : Exception
{
    public string Code { get; }
    public PsdzException(string code, string message) : base(message) => Code = code;
}

public sealed class PsdzCafReader : ICafReader
{
    public async Task<CafDefinition> ReadAsync(string path, string istaPath, string javaPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(istaPath) || string.IsNullOrWhiteSpace(javaPath))
            throw new PsdzException("CONFIGURATION", "Configure the ISTA directory and a 64-bit Java 8 executable first.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new PsdzException("CONFIGURATION", "Select an existing trusted local CAF file no larger than 16 MiB.");
        var start = CreateStartInfo(istaPath, javaPath, path);
        return await RunAsync(path, start, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CafDefinition> RunAsync(string path, ProcessStartInfo start, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(start, cancellationToken).ConfigureAwait(false);
        return ParseResponse(path, result.Output, result.ExitCode, result.Diagnostics);
    }

    internal sealed record ProcessResult(string Output, string Diagnostics, int ExitCode);

    internal static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = new Process { StartInfo = start };
        bool started = false;
        Directory.CreateDirectory(start.WorkingDirectory);
        try
        {
            started = process.Start();
            if (!started)
                throw new PsdzException("RUNTIME", "Could not start the read-only PSdZ helper.");
            using var stop = deadline.Token.Register(() => KillOwnedProcess(process));
            Task<string> stdout = ReadBoundedAsync(process.StandardOutput, 32 * 1024 * 1024, process, deadline.Token);
            Task<string> stderr = ReadBoundedAsync(process.StandardError, 1024 * 1024, process, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new ProcessResult(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PsdzException("TIMEOUT", "The PSdZ reader exceeded its 90-second deadline.");
        }
        finally
        {
            if (started)
            {
                KillOwnedProcess(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            Directory.Delete(start.WorkingDirectory, recursive: true);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string istaPath, string javaPath, string cafPath)
        => CreateOperationStartInfo(istaPath, javaPath, "read-caf",
            Path.Combine(Path.GetFullPath(istaPath), "PSdZ", "data"), cafPath);

    internal static ProcessStartInfo CreateOperationStartInfo(string istaPath, string javaPath, string operation,
        string dataRoot, params string[] arguments)
    {
        string root = Path.Combine(Path.GetFullPath(istaPath), "PSdZ");
        string binary = Path.Combine(root, "binx64");
        string data = Path.Combine(root, "data");
        string providers = Path.Combine(data, "psdzdata", "extLibs");
        string helper = Path.Combine(AppContext.BaseDirectory, "psdz-bridge");
        if (!File.Exists(javaPath) || !File.Exists(Path.Combine(helper, "PsdzReadOnlyBridge.class")) ||
            !File.Exists(Path.Combine(binary, "psdz", "psdz-api.jar")) ||
            !File.Exists(Path.Combine(providers, "codesyslib.jar")) ||
            !File.Exists(Path.Combine(providers, "kis.jar")))
            throw new PsdzException("CONFIGURATION", "Missing Java executable, packaged helper, or matching ISTA PSdZ runtime/data providers.");
        var jars = Directory.EnumerateFiles(binary, "*.jar", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(binary, p).Split(Path.DirectorySeparatorChar)
                .Contains("endorsed", StringComparer.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateFiles(providers, "*.jar")).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var start = new ProcessStartInfo(Path.GetFullPath(javaPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "bimmeross-psdz-" + Guid.NewGuid().ToString("N"))
        };
        // Do not inherit Java launch overrides which could inject agents or a different classpath.
        foreach (string variable in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" })
            start.Environment.Remove(variable);
        foreach (string argument in new[]
        {
            "-Xmx512m",
            "-Dfile.encoding=UTF-8",
            "-Djava.library.path=" + Path.Combine(binary, "prodias"),
            "-cp", string.Join(Path.PathSeparator, new[] { helper }.Concat(jars)),
            "PsdzReadOnlyBridge", operation, dataRoot
        }.Concat(arguments))
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static CafDefinition ParseResponse(string path, string response, int exitCode, string diagnostics)
    {
        try
        {
            using var json = JsonDocument.Parse(response);
            var root = json.RootElement;
            if (root.GetProperty("schema").GetString() != "bimmeross.psdz/1")
                throw new PsdzException("PROTOCOL", "Unsupported PSdZ helper response schema.");
            if (!root.GetProperty("ok").GetBoolean())
                throw new PsdzException("RUNTIME", root.GetProperty("error").GetString() ?? "PSdZ failed without an error message.");
            if (exitCode != 0)
                throw new PsdzException("RUNTIME", $"PSdZ exited with code {exitCode} despite reporting success.");
            return CafDefinition.Parse(path, root.GetProperty("version").GetString() ??
                throw new FormatException("No initialized runtime version returned."),
                root.GetProperty("xml").GetString() ?? throw new FormatException("No CAF XML returned."),
                diagnostics.Length > 16000 ? diagnostics[^16000..] : diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or XmlException)
        {
            string tail = diagnostics.Length > 2000 ? diagnostics[^2000..] : diagnostics;
            throw new PsdzException("PROTOCOL", $"Invalid PSdZ helper response: {ex.Message}\n{tail}");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, Process process, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
                return text.ToString();
            if (text.Length > limit - count)
            {
                KillOwnedProcess(process);
                throw new PsdzException("LIMIT", "PSdZ helper output exceeded its supported limit.");
            }
            text.Append(buffer, 0, count);
        }
    }

    private static void KillOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The owned process may have exited between HasExited and Kill.
        }
    }
}
