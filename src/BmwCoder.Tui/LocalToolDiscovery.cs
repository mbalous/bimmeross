using System.IO.Compression;
using System.Text.RegularExpressions;
using BmwCoder.Core.Models;

namespace BmwCoder.Tui;

internal sealed record ToolCandidates(
    IEnumerable<string> Ista, IEnumerable<string> PsdzData,
    IEnumerable<string> Java, IEnumerable<string> Esys);

internal sealed record DetectedTools(string Ista, string PsdzData, string Java, string Esys,
    bool BridgePresent, string[] Warnings);

internal static class LocalToolDiscovery
{
    internal static DetectedTools Detect(AppSettings settings)
    {
        var warnings = new List<string>();
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not inspect a local drive: {ex.Message}");
            }
        }

        var ista = new List<string> { settings.IstaPath };
        var data = new List<string> { settings.PsdzDataPath };
        var esys = new List<string> { settings.EsysPath };
        var java = new List<string> { settings.JavaPath };
        void AddJava(string directory)
        {
            try { java.Add(Path.Combine(directory.Trim().Trim('"'), "bin", "java.exe")); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                warnings.Add($"Could not inspect a Java location: {ex.Message}");
            }
        }
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            AddJava(javaHome);
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { java.Add(Path.Combine(path.Trim().Trim('"'), "java.exe")); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                warnings.Add($"Could not inspect a PATH entry: {ex.Message}");
            }
        }

        foreach (string root in roots)
        {
            ista.AddRange(new[] { Path.Combine(root, "EC-APPS", "ISTA"), Path.Combine(root, "ISTA"),
                Path.Combine(root, "Rheingold"), Path.Combine(root, "BMW", "ISPI", "TRIC", "ISTA") });
            data.AddRange(new[] { Path.Combine(root, "Data", "psdzdata"), Path.Combine(root, "ESysData", "psdzdata"),
                Path.Combine(root, "psdzdata"), Path.Combine(root, "EC-APPS", "ISTA", "PSdZ", "data", "psdzdata") });
            esys.AddRange(new[] { Path.Combine(root, "EC-APPS", "ESG", "E-Sys"), Path.Combine(root, "EC-APPS", "E-Sys"),
                Path.Combine(root, "E-Sys"), Path.Combine(root, "Program Files", "E-Sys") });
            foreach (string parent in new[] { Path.Combine(root, "Program Files", "Java"),
                Path.Combine(root, "Program Files", "Eclipse Adoptium"), Path.Combine(root, "Program Files", "Eclipse Foundation"),
                Path.Combine(root, "Program Files", "Amazon Corretto"), Path.Combine(root, "Program Files", "Zulu") })
            {
                try
                {
                    if (Directory.Exists(parent))
                        foreach (string directory in Directory.EnumerateDirectories(parent).Take(100))
                        {
                            java.Add(Path.Combine(directory, "bin", "java.exe"));
                            java.Add(Path.Combine(directory, "jre", "bin", "java.exe"));
                        }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not inspect {parent}: {ex.Message}");
                }
            }
        }
        foreach (string path in esys.Where(p => !string.IsNullOrWhiteSpace(p)))
            java.Add(Path.Combine(path, "jre", "bin", "java.exe"));
        foreach (string path in ista.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try { data.Add(Path.Combine(path, "PSdZ", "data", "psdzdata")); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                warnings.Add($"Could not inspect an ISTA data location: {ex.Message}");
            }
        }

        bool IsLocal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                return roots.Contains(root, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                warnings.Add($"Could not inspect a configured path: {ex.Message}");
                return false;
            }
        }
        return Detect(new ToolCandidates(ista.Where(IsLocal), data.Where(IsLocal),
            java.Where(IsLocal), esys.Where(IsLocal)), warnings);
    }

    internal static DetectedTools Detect(ToolCandidates candidates, List<string>? warnings = null)
    {
        warnings ??= new List<string>();
        string Find(IEnumerable<string> paths, Func<string, bool> validate)
        {
            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (validate(path))
                        return Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    warnings.Add($"Could not inspect {path}: {ex.Message}");
                }
            }
            return "";
        }
        return new DetectedTools(Find(candidates.Ista, IsIsta), Find(candidates.PsdzData, IsPsdzData),
            Find(candidates.Java, IsJava8), Find(candidates.Esys, IsEsys),
            File.Exists(Path.Combine(AppContext.BaseDirectory, "psdz-bridge", "PsdzReadOnlyBridge.class")), warnings.ToArray());
    }

    internal static bool IsIsta(string path)
    {
        string binary = Path.Combine(path, "PSdZ", "binx64");
        string data = Path.Combine(path, "PSdZ", "data", "psdzdata");
        string providers = Path.Combine(data, "extLibs");
        string metadata = Path.Combine(data, "kiswb");
        string swe = Path.Combine(data, "swe");
        return IsJar(Path.Combine(binary, "psdz", "psdz-api.jar"),
                "com/bmw/psdz/api/ServiceLocator.class", "com/bmw/psdz/api/Configuration.class",
                "com/bmw/psdz/api/Logic.class") &&
            IsJar(Path.Combine(providers, "codesyslib.jar")) && IsJar(Path.Combine(providers, "kis.jar")) &&
            Directory.Exists(Path.Combine(binary, "prodias")) &&
            Directory.EnumerateFiles(Path.Combine(binary, "prodias"), "*.dll").Any() &&
            Directory.Exists(metadata) && Directory.EnumerateFileSystemEntries(metadata).Any() &&
            Directory.Exists(swe) && Directory.EnumerateFileSystemEntries(swe).Any();
    }

    internal static bool IsPsdzData(string path)
    {
        string caf = Path.Combine(path, "swe", "cafd");
        return Directory.Exists(caf) && Directory.EnumerateFiles(caf, "*.caf.*").Any(file =>
            Regex.IsMatch(Path.GetFileName(file), @"\Acafd_[0-9a-f]{8}\.caf\.[0-9]{3}_[0-9]{3}_[0-9]{3}\z",
                RegexOptions.IgnoreCase) && new FileInfo(file).Length > 0);
    }

    internal static bool IsEsys(string path) =>
        File.Exists(Path.Combine(path, "jre", "bin", "java.exe")) &&
        IsJar(Path.Combine(path, "lib", "PsdZ", "psdz-api.jar")) &&
        IsJar(Path.Combine(path, "lib", "PsdZ", "psdz-core.jar"));

    internal static bool IsJava8(string path)
    {
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        string? bin = Path.GetDirectoryName(Path.GetFullPath(path));
        string? home = bin == null ? null : Directory.GetParent(bin)?.FullName;
        string release = home == null ? "" : Path.Combine(home, "release");
        if (!File.Exists(release) && home != null && Path.GetFileName(home).Equals("jre", StringComparison.OrdinalIgnoreCase))
            release = Path.Combine(Directory.GetParent(home)?.FullName ?? home, "release");
        if (!File.Exists(release) || new FileInfo(release).Length > 64 * 1024)
            return false;
        string metadata = File.ReadAllText(release);
        if (!Regex.IsMatch(metadata, @"(?m)^JAVA_VERSION=""1\.8\.[^""]+""\s*$") ||
            !Regex.IsMatch(metadata, @"(?im)^OS_ARCH=""(amd64|x86_64)""\s*$"))
            return false;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            return false;
        stream.Position = 0x3c;
        int peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > stream.Length - 6)
            return false;
        stream.Position = peOffset;
        return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
    }

    private static bool IsJar(string path, params string[] requiredEntries)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return requiredEntries.Length == 0 ? archive.Entries.Count > 0 :
                requiredEntries.All(entry => archive.GetEntry(entry) != null);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
