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
                "Default: live read-only mode; no connection is made until requested. --demo uses synthetic data without sockets.\n" +
                "F5 finds a gateway; F2 connects; F3 discovers ECUs; F4 cancels; Ctrl+Q quits. FDL writes are unavailable.");
            return 0;
        }
        bool initialized = false;
        try
        {
            var (demo, settingsPath) = ParseOptions(args);
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
                ConfigureStartupSetup(window, settings, settingsPath, () => LocalToolDiscovery.Detect(settings));
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

    internal static void ConfigureStartupSetup(WorkbenchWindow window, AppSettings settings, string settingsPath,
        Func<DetectedTools> detect)
    {
        bool shown = false;
        window.Ready += () =>
        {
            if (shown) return;
            shown = true;
            using var setup = new LocalToolsDialog(settings, settingsPath, detect());
            Application.Run(setup);
            window.RefreshState();
        };
    }

    internal static (bool Demo, string? SettingsPath) ParseOptions(string[] args)
    {
        bool demo = false;
        string? settingsPath = null;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--demo": demo = true; break;
                case "--live": demo = false; break;
                case "--settings" when i + 1 < args.Length: settingsPath = args[++i]; break;
                default: throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help.");
            }
        return (demo, settingsPath);
    }
}
