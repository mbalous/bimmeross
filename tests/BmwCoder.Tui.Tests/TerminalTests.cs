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
