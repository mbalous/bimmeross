using System.Text;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Terminal.Gui;

namespace BmwCoder.Tui.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TerminalTests
{
    [TestMethod]
    [Timeout(10000)]
    public void LocalToolsDialogUsesOrSavesOnlyAfterExplicitChoice()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bimmeross-setup-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(directory, "psdzdata");
        string caf = Path.Combine(data, "swe", "cafd");
        string settingsPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(caf);
        File.WriteAllText(Path.Combine(caf, "cafd_00000001.caf.001_000_000"), "fixture");
        var settings = new AppSettings { PsdzDataPath = "missing-saved-path", Host = "example.invalid" };
        var detected = new DetectedTools("", data, "", "", true, Array.Empty<string>());
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(80, 24);
            var workbench = new Workbench(new DemoVehicle(), settings);
            using var window = new WorkbenchWindow(workbench, settingsPath);
            using var state = Application.Begin(window);
            using (var dialog = new LocalToolsDialog(settings, settingsPath, detected))
            {
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
                {
                    Application.Refresh();
                    StringAssert.Contains(Screen(driver), "Unverified saved paths omitted");
                    StringAssert.Contains(Screen(driver), "Use");
                    StringAssert.Contains(Screen(driver), "Save");
                    StringAssert.Contains(Screen(driver), "Skip");
                    Assert.IsTrue(Views(dialog).OfType<TextField>().Any(field => field.Text.ToString() == data));
                    Views(dialog).OfType<Button>().Single(button => button.Text.ToString()!.Contains("Skip")).OnClicked();
                    return false;
                });
                Application.Run(dialog);
            }
            Assert.AreEqual("missing-saved-path", settings.PsdzDataPath);
            Assert.IsFalse(File.Exists(settingsPath));

            using (var dialog = new LocalToolsDialog(settings, settingsPath, detected))
            {
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
                {
                    Views(dialog).OfType<Button>().Single(button => button.Text.ToString()!.Contains("Use")).OnClicked();
                    return false;
                });
                Application.Run(dialog);
            }
            Assert.AreEqual(data, settings.PsdzDataPath);
            Assert.IsFalse(File.Exists(settingsPath));

            using (var dialog = new LocalToolsDialog(settings, settingsPath, detected))
            {
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
                {
                    Views(dialog).OfType<Button>().Single(button => button.Text.ToString()!.Contains("Save")).OnClicked();
                    return false;
                });
                Application.Run(dialog);
            }
            var saved = AppSettings.LoadFrom(settingsPath);
            Assert.AreEqual(data, saved.PsdzDataPath);
            Assert.AreEqual("example.invalid", saved.Host);
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Application.Shutdown();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<View> Views(View parent) =>
        parent.Subviews.SelectMany(child => new[] { child }.Concat(Views(child)));

    [TestMethod]
    [Timeout(10000)]
    public void StartupShowsOneSetupDialogBeforeReturningToWorkbench()
    {
        var settings = new AppSettings();
        var workbench = new Workbench(new DemoVehicle(), settings);
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(80, 24);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            int detections = 0;
            Program.ConfigureStartupSetup(window, settings, "unused-settings.json", () =>
            {
                detections++;
                return new DetectedTools("", "", "", "", true, Array.Empty<string>());
            });
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(50), _ =>
            {
                Application.Refresh();
                StringAssert.Contains(Screen(driver), "Local tools setup");
                var dialog = (LocalToolsDialog)Application.Current;
                Views(dialog).OfType<Button>().Single(button => button.Text.ToString()!.Contains("Use")).OnClicked();
                return false;
            });
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(200), _ =>
            {
                Assert.AreEqual(window, Application.Current);
                Application.RequestStop();
                return false;
            });
            Application.Run(window);
            Assert.AreEqual(1, detections);
            Assert.IsFalse(workbench.IsConnected);
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public void LiveModeIsDefaultAndDemoRemainsExplicit()
    {
        Assert.IsFalse(Program.ParseOptions(Array.Empty<string>()).Demo);
        Assert.IsTrue(Program.ParseOptions(new[] { "--demo" }).Demo);
        Assert.IsFalse(Program.ParseOptions(new[] { "--demo", "--live" }).Demo);
        Assert.AreEqual("settings.json", Program.ParseOptions(new[] { "--settings", "settings.json" }).SettingsPath);
        Assert.ThrowsException<ArgumentException>(() => Program.ParseOptions(new[] { "--settings" }));
    }

    [TestMethod]
    public void DiagnosticResultsShowSelectedReadingAndFilter()
    {
        var workbench = new Workbench(new DemoVehicle(), new AppSettings());
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(120, 40);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            using var state = Application.Begin(window);
            workbench.ConnectAsync().GetAwaiter().GetResult();
            workbench.ReadDidAsync(0x10, 0xF190).GetAwaiter().GetResult();
            workbench.ProbeAsync(0x63).GetAwaiter().GetResult();
            window.ShowScreen("Diagnostics");
            window.Filter("TesterPresent");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "TesterPresent");
            StringAssert.Contains(Screen(driver), "7E00");
            Assert.IsFalse(Screen(driver).Contains("DID 0xF190"));
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public void ParameterBrowserSearchesActualValuesAndShowsUnknownFieldsWithoutEditing()
    {
        var workbench = new Workbench(new DemoVehicle(), new AppSettings(), codingReader: new SyntheticCodingReader());
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(120, 40);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            using var state = Application.Begin(window);
            workbench.DecodeCodingAsync("synthetic.ncd", SyntheticCodingReader.Caf).GetAwaiter().GetResult();
            window.ShowScreen("Parameters");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "synthetic-feature");
            window.Filter("3000/1");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "(unavailable)");
            Assert.IsFalse(Screen(driver).Contains("synthetic-feature"));
            Assert.AreEqual("synthetic-original", workbench.Decoded!.OriginalNcd);
            Assert.IsFalse(workbench.IsConnected);
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public void CafBrowserRendersAndFiltersReadOnlyDefinitionFields()
    {
        var workbench = new Workbench(new DemoVehicle(), new AppSettings(), new SyntheticCafReader());
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(120, 40);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            using var state = Application.Begin(window);
            workbench.ImportCafAsync("synthetic.caf").GetAwaiter().GetResult();
            window.ShowScreen("CAF");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "synthetic-first");
            StringAssert.Contains(Screen(driver), "synthetic-second");
            window.Filter("second");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "synthetic-second");
            Assert.IsFalse(Screen(driver).Contains("synthetic-first"));
            Assert.IsFalse(workbench.IsConnected);
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class SyntheticCafReader : ICafReader
    {
        public Task<CafDefinition> ReadAsync(string path, string istaPath, string javaPath, CancellationToken token) =>
            Task.FromResult(CafDefinition.Parse(path, "synthetic-runtime",
                "<caf><first>synthetic-first</first><second>synthetic-second</second></caf>"));
    }

    [TestMethod]
    public void TerminalRendersRealViewsAndKeyboardConnectsAndDiscovers()
    {
        var workbench = new Workbench(new DemoVehicle(), new AppSettings());
        var driver = new FakeDriver();
        try
        {
            Application.Init(driver);
            driver.SetBufferSize(120, 40);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            using var state = Application.Begin(window);
            Application.Refresh();
            string initial = Screen(driver);
            StringAssert.Contains(initial, "BimmerOSS");
            StringAssert.Contains(initial, "READ-ONLY PREVIEW");
            StringAssert.Contains(initial, "F3 Discover ECUs");
            StringAssert.Contains(initial, "Synthetic demo");
            Assert.IsTrue(window.ProcessKey(new KeyEvent(Key.F2, new KeyModifiers())));
            Assert.IsTrue(workbench.IsConnected);
            Assert.IsTrue(window.ProcessKey(new KeyEvent(Key.F3, new KeyModifiers())));
            Assert.AreEqual(2, workbench.Ecus.Count);
            window.RefreshState();
            Application.Refresh();
            string connected = Screen(driver);
            StringAssert.Contains(connected, "DEMO0000000000001");
            StringAssert.Contains(connected, "DEMO Gateway");
            window.Filter("Head unit");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "DEMO Head unit");
            Assert.IsFalse(Screen(driver).Contains("DEMO Gateway"));
            window.Filter("");
            window.ShowScreen("Help");
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "Terminal.Gui");
            Assert.AreEqual("Help", window.CurrentScreen);
            window.Filter("no matches");
            Assert.IsTrue(window.ProcessKey(new KeyEvent(Key.F3, new KeyModifiers())));
            window.RefreshState();
            Application.Refresh();
            Assert.AreEqual("Vehicle", window.CurrentScreen);
            StringAssert.Contains(Screen(driver), "DEMO Gateway");
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    [Timeout(10000)]
    public void LiveWorkflowShowsSeparateDiscoveryStepsAndAnimatesBusyStatus()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vehicle = new SyntheticLiveVehicle { ConnectHook = token => release.Task.WaitAsync(token) };
        var workbench = new Workbench(vehicle, new AppSettings());
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(100, 30);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
            {
                string initial = Screen(driver);
                StringAssert.Contains(initial, "F5 Find gateway");
                StringAssert.Contains(initial, "F3 Discover ECUs");
                Assert.IsFalse(workbench.IsConnected);
                window.ProcessKey(new KeyEvent(Key.F2, new KeyModifiers()));
                return false;
            });
            string firstFrame = "";
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(70), _ =>
            {
                Application.Refresh();
                firstFrame = Screen(driver).Split('\n').Single(line => line.Contains("F4 cancels (disconnects)"));
                StringAssert.Contains(firstFrame, "Connect...");
                StringAssert.Contains(firstFrame, "F4 cancels");
                return false;
            });
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(240), _ =>
            {
                Application.Refresh();
                string nextFrame = Screen(driver).Split('\n').Single(line => line.Contains("F4 cancels (disconnects)"));
                Assert.AreNotEqual(firstFrame, nextFrame, "The activity indicator should advance.");
                release.SetResult();
                return false;
            });
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(450), _ =>
            {
                window.RefreshState();
                Application.Refresh();
                StringAssert.Contains(Screen(driver), "Connect: complete");
                Assert.IsFalse(Screen(driver).Contains("F4 cancels (disconnects)"));
                Application.RequestStop();
                return false;
            });
            Application.Run(window);
            Assert.IsTrue(workbench.IsConnected);
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public void NarrowTerminalShowsResizeGuidanceAndRecovers()
    {
        var workbench = new Workbench(new DemoVehicle(), new AppSettings());
        var driver = new FakeDriver();
        try
        {
            Application.Init(driver);
            driver.SetBufferSize(60, 20);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            using var state = Application.Begin(window);
            Application.Refresh();
            StringAssert.Contains(Screen(driver), "Minimum terminal size");
            driver.SetBufferSize(100, 30);
            Application.Refresh();
            Assert.IsFalse(Screen(driver).Contains("Minimum terminal size"));
            StringAssert.Contains(Screen(driver), "FDL writes");
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    [Timeout(10000)]
    public void ApplicationMainLoopRunsTheDemoAndExitsCleanly()
    {
        var workbench = new Workbench(new DemoVehicle(), new AppSettings());
        try
        {
            var driver = new FakeDriver();
            Application.Init(driver);
            driver.SetBufferSize(120, 40);
            using var window = new WorkbenchWindow(workbench, "unused-settings.json");
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
            {
                window.ProcessKey(new KeyEvent(Key.F2, new KeyModifiers()));
                window.ProcessKey(new KeyEvent(Key.F3, new KeyModifiers()));
                window.RefreshState();
                Application.Refresh();
                StringAssert.Contains(Screen(driver), "DEMO Gateway");
                Application.RequestStop();
                return false;
            });
            Application.Run(window);
            Assert.AreEqual(2, workbench.Ecus.Count);
        }
        finally
        {
            Application.Shutdown();
            workbench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string Screen(FakeDriver driver)
    {
        var text = new StringBuilder();
        for (int row = 0; row < driver.Rows; row++)
        {
            for (int col = 0; col < driver.Cols; col++)
                text.Append(char.ConvertFromUtf32(driver.Contents[row, col, 0]));
            text.AppendLine();
        }
        return text.ToString();
    }
}
