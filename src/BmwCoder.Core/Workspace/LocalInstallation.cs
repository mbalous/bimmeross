using System.IO.Compression;

namespace BmwCoder.Core.Workspace;

public sealed record InstallationReport(string PsdzVersion, string[] Series, int CafFileCount,
    bool PsdzRuntimePresent, string[] Messages)
{
    public override string ToString() => string.Join(Environment.NewLine,
        new[] { $"PSdZData: {PsdzVersion}", $"Series: {string.Join(", ", Series)}",
            $"CAFD files: {CafFileCount}",
            $"E-Sys local runtime files: {(PsdzRuntimePresent ? "present (not execution-validated)" : "incomplete")}" }
        .Concat(Messages).Append(PreviewCapabilities.CodingUnavailable));
}

public static class LocalInstallation
{
    public static InstallationReport Inspect(string psdzPath, string esysPath, string istaPath,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        string version = "not configured";
        string[] series = Array.Empty<string>();
        int cafCount = 0;
        if (!string.IsNullOrWhiteSpace(psdzPath))
        {
            if (!Directory.Exists(psdzPath))
                throw new DirectoryNotFoundException($"PSdZData directory does not exist: {psdzPath}");
            string versionPath = Path.Combine(psdzPath, "Version.txt");
            if (File.Exists(versionPath) && new FileInfo(versionPath).Length > 64 * 1024)
                throw new FormatException("PSdZData Version.txt exceeds the 64 KiB limit.");
            version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "Version.txt missing";
            string mainSeries = Path.Combine(psdzPath, "mainseries");
            if (!Directory.Exists(mainSeries))
                messages.Add("PSdZData mainseries directory is missing.");
            else
                series = Directory.EnumerateDirectories(mainSeries).Select(Path.GetFileName)
                    .OfType<string>().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            string caf = Path.Combine(psdzPath, "swe", "cafd");
            if (!Directory.Exists(caf))
                messages.Add("PSdZData swe\\cafd directory is missing; named FDL support cannot be inferred.");
            else
                foreach (string path in Directory.EnumerateFiles(caf, "*.caf.*"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cafCount++;
                }
        }

        string[] runtimeFiles =
        {
            Path.Combine(esysPath, "jre", "bin", "java.exe"),
            Path.Combine(esysPath, "lib", "PsdZ", "psdz-api.jar"),
            Path.Combine(esysPath, "lib", "PsdZ", "psdz-core.jar")
        };
        bool runtimePresent = !string.IsNullOrWhiteSpace(esysPath) && runtimeFiles.All(File.Exists);
        if (!string.IsNullOrWhiteSpace(esysPath))
        {
            if (!Directory.Exists(esysPath))
                throw new DirectoryNotFoundException($"E-Sys directory does not exist: {esysPath}");
            foreach (string path in runtimeFiles.Where(p => !File.Exists(p)))
                messages.Add($"Missing runtime file: {path}");
            foreach (string jar in runtimeFiles.Where(p => p.EndsWith(".jar", StringComparison.Ordinal) && File.Exists(p)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var archive = ZipFile.OpenRead(jar);
                var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
                if (manifest == null)
                {
                    messages.Add($"{Path.GetFileName(jar)}: manifest missing; version unknown.");
                    continue;
                }
                if (manifest.Length > 64 * 1024)
                    throw new FormatException($"{Path.GetFileName(jar)}: manifest exceeds the 64 KiB limit.");
                using var reader = new StreamReader(manifest.Open());
                string? line;
                bool found = false;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Bundle-Version:", StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add($"{Path.GetFileName(jar)}: {line}");
                        found = true;
                    }
                }
                if (!found)
                    messages.Add($"{Path.GetFileName(jar)}: manifest has no supported version field.");
            }
        }
        if (!string.IsNullOrWhiteSpace(istaPath))
        {
            if (!Directory.Exists(istaPath))
                throw new DirectoryNotFoundException($"ISTA directory does not exist: {istaPath}");
            messages.Add("ISTA directory present; no diagnostic databases loaded automatically.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new InstallationReport(version, series, cafCount, runtimePresent, messages.ToArray());
    }
}
