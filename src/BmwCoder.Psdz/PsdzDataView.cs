using System.Diagnostics;
using System.Text;

namespace BmwCoder.Psdz;

internal sealed class PsdzDataView : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bimmeross-psdz-data-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();

    public static async Task<PsdzDataView> CreateAsync(string istaPath, string psdzDataPath, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PsdzException("PLATFORM", "The installed PSdZ coding reader requires Windows.");
        string original = Path.Combine(Path.GetFullPath(istaPath), "PSdZ", "data", "psdzdata");
        string caf = Path.Combine(Path.GetFullPath(psdzDataPath), "swe", "cafd");
        if (string.IsNullOrWhiteSpace(psdzDataPath) || !Directory.Exists(caf) ||
            !Directory.Exists(Path.Combine(original, "kiswb")) || !Directory.Exists(Path.Combine(original, "swe")))
            throw new PsdzException("CONFIGURATION", "Configure matching ISTA metadata and a PSdZData directory containing swe\\cafd.");
        var view = new PsdzDataView();
        try
        {
            string data = Path.Combine(view.Root, "psdzdata");
            Directory.CreateDirectory(Path.Combine(data, "swe"));
            Directory.CreateDirectory(Path.Combine(data, "temp"));
            var script = new StringBuilder("$ErrorActionPreference = 'Stop'; ");
            void Link(string path, string target)
            {
                view._links.Add(path);
                static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
                script.Append($"New-Item -ItemType Junction -Path {Quote(path)} -Target {Quote(target)} -ErrorAction Stop | Out-Null; ");
            }
            foreach (string directory in Directory.EnumerateDirectories(original))
            {
                string name = Path.GetFileName(directory);
                if (!name.Equals("swe", StringComparison.OrdinalIgnoreCase) && !name.Equals("temp", StringComparison.OrdinalIgnoreCase))
                    Link(Path.Combine(data, name), directory);
            }
            foreach (string directory in Directory.EnumerateDirectories(Path.Combine(original, "swe")))
                if (!Path.GetFileName(directory).Equals("cafd", StringComparison.OrdinalIgnoreCase))
                    Link(Path.Combine(data, "swe", Path.GetFileName(directory)), directory);
            Link(Path.Combine(data, "swe", "cafd"), caf);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.Combine(Path.GetTempPath(), "bimmeross-psdz-links-" + Guid.NewGuid().ToString("N"))
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString())) })
                start.ArgumentList.Add(argument);
            var result = await PsdzCafReader.RunProcessAsync(start, token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new PsdzException("CONFIGURATION", $"Could not create the temporary dataset view: {result.Diagnostics}");
            return view;
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        // Unlink explicitly before recursively removing our own directories, never traverse OEM junction targets.
        foreach (string path in _links)
            if (new DirectoryInfo(path).LinkTarget != null)
                Directory.Delete(path);
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
