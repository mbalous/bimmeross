using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using BmwCoder.Psdz;
using Terminal.Gui;

namespace BmwCoder.Tui;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("BimmerOSS TUI\nUsage: BmwCoder.Tui [--demo | --live] [--settings <file>]\n" +
                "Default: offline demo (no sockets). --live enables explicit read-only connections.\n" +
                "Ctrl+Q quits. F2 connects; F3 discovers; F4 cancels. FDL writes are unavailable.");
            return 0;
        }
        bool demo = true;
        string? settingsPath = null;
        bool initialized = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--demo": demo = true; break;
                    case "--live": demo = false; break;
                    case "--settings" when i + 1 < args.Length: settingsPath = args[++i]; break;
                    default: throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help.");
                }
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("The TUI requires an interactive terminal. Use --help or the existing JSON CLI for redirected output.");
            settingsPath ??= AppSettings.GetSettingsPath();
            var settings = demo && !File.Exists(settingsPath) ? new AppSettings() : AppSettings.LoadFrom(settingsPath);
            await using var workbench = new Workbench(demo ? new DemoVehicle() : new ReadOnlyVehicle(), settings,
                new PsdzCafReader(), new PsdzCodingReader());
            try
            {
                Application.Init();
                initialized = true;
                using var window = new WorkbenchWindow(workbench, settingsPath);
                Application.Run(window);
            }
            finally
            {
                if (initialized)
                {
                    Application.Shutdown();
                    initialized = false;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (initialized)
                Application.Shutdown();
            Console.Error.WriteLine($"BimmerOSS TUI: {ex.Message}");
            return 1;
        }
    }
}
