using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using BmwCoder.Core.Workspace;
using BmwCoder.Psdz;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Psdz.Tests;

[TestClass]
public sealed class CafReaderTests
{
    [TestMethod]
    public void DefinitionPreservesUnknownNamesValuesAndDistinctSiblingPaths()
    {
        var definition = CafDefinition.Parse("synthetic.caf", "synthetic-runtime",
            "<caf xmlns:x=\"urn:test\"><unknown x:flag=\"kept\"><item>first</item><item>second</item></unknown></caf>");
        Assert.AreEqual(3, definition.Fields.Count);
        StringAssert.Contains(definition.Fields[0].Path, "@{urn:test}flag");
        Assert.AreEqual("kept", definition.Fields[0].Value);
        Assert.AreEqual("/caf[1]/unknown[1]/item[2]", definition.Fields[2].Path);
        Assert.AreEqual("second", definition.Fields[2].Value);
    }

    [TestMethod]
    public void DefinitionRejectsWrongRootExternalEntitiesAndExcessiveDepth()
    {
        Assert.ThrowsException<FormatException>(() => CafDefinition.Parse("test", "test", "<ncd/>"));
        Assert.ThrowsException<XmlException>(() => CafDefinition.Parse("test", "test",
            "<!DOCTYPE caf [<!ENTITY x SYSTEM 'file:///not-read'>]><caf>&x;</caf>"));
        string nested = "<caf>" + string.Concat(Enumerable.Repeat("<x>", 130)) +
            string.Concat(Enumerable.Repeat("</x>", 130)) + "</caf>";
        Assert.ThrowsException<FormatException>(() => CafDefinition.Parse("test", "test", nested));
    }

    [DataTestMethod]
    [DataRow("not-json", 0, "PROTOCOL")]
    [DataRow("{\"schema\":\"other\",\"ok\":true}", 0, "PROTOCOL")]
    [DataRow("{\"schema\":\"bimmeross.psdz/1\",\"ok\":false,\"error\":\"bad signature\"}", 1, "RUNTIME")]
    [DataRow("{\"schema\":\"bimmeross.psdz/1\",\"ok\":true,\"version\":\"test\",\"xml\":\"<caf/>\"}", 1, "RUNTIME")]
    [DataRow("{\"schema\":\"bimmeross.psdz/1\",\"ok\":true}", 0, "PROTOCOL")]
    [DataRow("{\"schema\":\"bimmeross.psdz/1\",\"ok\":true,\"version\":null,\"xml\":\"<caf/>\"}", 0, "PROTOCOL")]
    [DataRow("{\"schema\":\"bimmeross.psdz/1\",\"ok\":true,\"version\":\"test\",\"xml\":\"<ncd/>\"}", 0, "PROTOCOL")]
    public void ResponseCannotSilentlyAcceptProtocolOrRuntimeErrors(string response, int exitCode, string code)
    {
        var error = Assert.ThrowsException<PsdzException>(() => PsdzCafReader.ParseResponse("test", response, exitCode, ""));
        Assert.AreEqual(code, error.Code);
    }

    [TestMethod]
    public void LaunchUsesOnlySelectedIstaProvidersAndKeepsPathsAsIndividualArguments()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bimmeross-psdz-layout-" + Guid.NewGuid().ToString("N"));
        string binary = Path.Combine(directory, "PSdZ", "binx64");
        string providers = Path.Combine(directory, "PSdZ", "data", "psdzdata", "extLibs");
        try
        {
            Directory.CreateDirectory(Path.Combine(binary, "psdz"));
            Directory.CreateDirectory(Path.Combine(binary, "endorsed"));
            Directory.CreateDirectory(providers);
            string java = Path.Combine(directory, "synthetic java.exe");
            foreach (string file in new[]
            {
                java, Path.Combine(binary, "psdz", "psdz-api.jar"),
                Path.Combine(binary, "endorsed", "excluded.jar"),
                Path.Combine(providers, "codesyslib.jar"), Path.Combine(providers, "kis.jar")
            })
                File.WriteAllText(file, "Synthetic placeholder; never executed.");
            string caf = Path.Combine(directory, "a path with spaces.caf");
            var start = PsdzCafReader.CreateStartInfo(directory, java, caf);
            string classpath = start.ArgumentList[start.ArgumentList.IndexOf("-cp") + 1];
            StringAssert.Contains(classpath, providers);
            Assert.IsFalse(classpath.Contains("excluded.jar"));
            Assert.AreEqual(Path.Combine(directory, "PSdZ", "data"), start.ArgumentList[^2]);
            Assert.AreEqual(caf, start.ArgumentList[^1]);
            Assert.IsFalse(start.UseShellExecute);
            Assert.IsFalse(start.Environment.ContainsKey("JAVA_TOOL_OPTIONS"));
            Assert.IsFalse(start.Environment.ContainsKey("CLASSPATH"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task OwnProcessSeparatesVendorDiagnosticsFromUnicodeProtocolAndCleansUp()
    {
        var start = Worker("success");
        var definition = await PsdzCafReader.RunAsync("synthetic.caf", start, CancellationToken.None);
        Assert.AreEqual("value \u03b1", definition.Fields.Single().Value);
        StringAssert.Contains(definition.Diagnostics, "synthetic warning");
        Assert.IsFalse(Directory.Exists(start.WorkingDirectory));
    }

    [TestMethod]
    public async Task OwnProcessIsTerminatedAndReapedOnCancellation()
    {
        var start = Worker("hang");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await PsdzCafReader.RunAsync("synthetic.caf", start, cancel.Token);
            Assert.Fail("Cancellation must not return a definition.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(cancel.IsCancellationRequested);
        }
        Assert.IsFalse(Directory.Exists(start.WorkingDirectory));
    }

    [TestMethod]
    public async Task ExcessiveDiagnosticsKillTheOwnedProcessAndSurfaceAnError()
    {
        var start = Worker("excess-diagnostics");
        var error = await Assert.ThrowsExceptionAsync<PsdzException>(() =>
            PsdzCafReader.RunAsync("synthetic.caf", start, CancellationToken.None));
        Assert.AreEqual("LIMIT", error.Code);
        Assert.IsFalse(Directory.Exists(start.WorkingDirectory));
    }

    private static ProcessStartInfo Worker(string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "bimmeross-psdz-test-" + Guid.NewGuid().ToString("N")),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(CafReaderTests).Assembly.Location);
        start.ArgumentList.Add(mode);
        return start;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 1)
            return 2;
        if (args[0] == "success")
        {
            Console.Error.WriteLine("synthetic warning");
            Console.Write(JsonSerializer.Serialize(new
            {
                schema = "bimmeross.psdz/1", ok = true, version = "synthetic-runtime",
                xml = "<caf><field>value \u03b1</field></caf>"
            }));
            return 0;
        }
        if (args[0] == "excess-diagnostics")
            Console.Error.Write(new string('x', 1024 * 1024 + 1));
        if (args[0] is "hang" or "excess-diagnostics")
            Thread.Sleep(Timeout.Infinite);
        return 2;
    }
}
