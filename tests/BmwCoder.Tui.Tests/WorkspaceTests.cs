using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using BmwCoder.Core.Models;
using BmwCoder.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Tui.Tests;

[TestClass]
public sealed class WorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bimmeross-workspace-{Guid.NewGuid():N}");

    public WorkspaceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void FaUnchangedExportPreservesExactBytesAndUnknownFields()
    {
        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<fa xmlns=\"urn:test\"><unknown keep=\"true\"/>  <option>123</option></fa>")).ToArray();
        var fa = FaXmlDocument.Parse(bytes);
        CollectionAssert.AreEqual(bytes, fa.Export());
        fa.SetValue(fa.Fields.Single(f => f.Value == "123").Id, "456");
        var edited = FaXmlDocument.Parse(fa.Export());
        Assert.AreEqual("true", edited.Fields.Single(f => f.Path.EndsWith("/@keep")).Value);
        Assert.AreEqual("456", edited.Fields.Single(f => f.Value == "456").Value);
        Assert.AreEqual(1, fa.Changes.Count);
        fa.SetValue(fa.Fields.Single(f => f.Value == "456").Id, "123");
        CollectionAssert.AreEqual(bytes, fa.Export());
    }

    [TestMethod]
    public void FaExportsNeverOverwriteTheSource()
    {
        string path = Path.Combine(_directory, "fa.xml");
        var fa = FaXmlDocument.Parse(Encoding.UTF8.GetBytes("<fa><option>123</option></fa>"));
        fa.ExportNewFile(path);
        fa.SetValue(0, "456");
        Assert.ThrowsException<IOException>(() => fa.ExportNewFile(path));
        StringAssert.Contains(File.ReadAllText(path), "123");
        Assert.ThrowsException<XmlException>(() => fa.SetValue(0, "\u0001"));
    }

    [TestMethod]
    public void FaRejectsExternalEntitiesNonFaAndOversizedInput()
    {
        Assert.ThrowsException<XmlException>(() => FaXmlDocument.Parse(Encoding.UTF8.GetBytes(
            "<!DOCTYPE fa [<!ENTITY external SYSTEM 'file:///not-accessed'>]><fa>&external;</fa>")));
        Assert.ThrowsException<FormatException>(() => FaXmlDocument.Parse(Encoding.UTF8.GetBytes("<configuration/>")));
        Assert.ThrowsException<FormatException>(() => FaXmlDocument.Parse(new byte[FaXmlDocument.MaximumBytes + 1]));
    }

    [TestMethod]
    public void CodingPreviewPreservesBytesWithoutPretendingToDecodeThem()
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        string source = Path.Combine(_directory, "original.ncd");
        File.WriteAllBytes(source, bytes);
        var coding = CodingFile.Load(source);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), coding.Sha256);
        StringAssert.Contains(coding.HexPreview(16), "truncated");
        string export = Path.Combine(_directory, "copy.ncd");
        coding.ExportNewFile(export);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(export));
        Assert.ThrowsException<IOException>(() => coding.ExportNewFile(source));
    }

    [TestMethod]
    public void RawBlockDraftEditsRevertsAndExportsWithoutWritingToVehicle()
    {
        byte[] source = new byte[32];
        var draft = new RawBlockDraft(0x40, 0x3000, source);
        draft.SetByte(0, 0x12);
        Assert.AreEqual(0, source[0]);
        Assert.AreEqual(1, draft.ChangedBytes);
        Assert.IsFalse(draft.VerifyChecksum());
        draft.ApplyChecksum();
        Assert.IsTrue(draft.VerifyChecksum());
        Assert.AreEqual(2, draft.ChangedBytes);
        string path = Path.Combine(_directory, "draft.bin");
        draft.ExportNewFile(path);
        Assert.AreEqual(0x12, File.ReadAllBytes(path)[31]);
        Assert.ThrowsException<IOException>(() => draft.ExportNewFile(path));
        draft.Revert();
        Assert.AreEqual(0, draft.ChangedBytes);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => draft.SetByte(32, 0));
        Assert.ThrowsException<InvalidOperationException>(() => new RawBlockDraft(0x10, 0xF190, source).ApplyChecksum());
    }

    [TestMethod]
    public void BackupInspectorChecksHashSizeAndFilenameWithoutRepairingEvidence()
    {
        byte[] bytes = { 1, 2, 3 };
        string manifestPath = Path.Combine(_directory, "manifest.json");
        var manifest = new BackupManifest
        {
            Entries = new List<BackupEntry> { new()
            {
                FileName = "63_3000.bin", SizeBytes = 3,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            } }
        };
        File.WriteAllBytes(Path.Combine(_directory, "63_3000.bin"), bytes);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Assert.IsTrue(BackupInspector.Inspect(_directory).IsValid);
        File.WriteAllBytes(Path.Combine(_directory, "63_3000.bin"), new byte[] { 4 });
        var report = BackupInspector.Inspect(_directory);
        Assert.AreEqual(2, report.Problems.Length);
        manifest.Entries[0].FileName = "..\\outside.bin";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        report = BackupInspector.Inspect(_directory);
        StringAssert.Contains(report.Problems.Single(), "Invalid snapshot filename");
        File.WriteAllText(manifestPath, "{corrupt");
        Assert.ThrowsException<JsonException>(() => BackupInspector.Inspect(_directory));
        Assert.AreEqual("{corrupt", File.ReadAllText(manifestPath));
    }

    [TestMethod]
    public void InstallationInventoryUsesOnlySelectedDataAndSurfacesMissingPaths()
    {
        File.WriteAllText(Path.Combine(_directory, "Version.txt"), "Synthetic version");
        Directory.CreateDirectory(Path.Combine(_directory, "mainseries", "FTEST"));
        string caf = Path.Combine(_directory, "swe", "cafd");
        Directory.CreateDirectory(caf);
        File.WriteAllText(Path.Combine(caf, "synthetic.caf.001"), "");
        var report = LocalInstallation.Inspect(_directory, "", "");
        Assert.AreEqual(1, report.CafFileCount);
        CollectionAssert.AreEqual(new[] { "FTEST" }, report.Series);
        Assert.IsFalse(report.PsdzRuntimePresent);
        StringAssert.Contains(report.ToString(), "unavailable");
        Assert.ThrowsException<DirectoryNotFoundException>(() =>
            LocalInstallation.Inspect(Path.Combine(_directory, "missing"), "", ""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => LocalInstallation.Inspect(_directory, "", "", cts.Token));
    }

    [TestMethod]
    public void InstallationInspectionReadsVersionMetadataWithoutExecutingTheRuntime()
    {
        string runtime = Path.Combine(_directory, "jre", "bin");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "java.exe"), "Not executable: the inspector must never launch this.");
        string jars = Path.Combine(_directory, "lib", "PsdZ");
        Directory.CreateDirectory(jars);
        foreach (string name in new[] { "psdz-api.jar", "psdz-core.jar" })
        {
            using var zip = ZipFile.Open(Path.Combine(jars, name), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open());
            writer.Write("Manifest-Version: 1.0\nImplementation-Version: synthetic-1.2\n");
        }
        var report = LocalInstallation.Inspect("", _directory, "");
        Assert.IsTrue(report.PsdzRuntimePresent);
        Assert.AreEqual(2, report.Messages.Count(m => m.Contains("synthetic-1.2")));
        StringAssert.Contains(report.ToString(), "not execution-validated");
        File.WriteAllText(Path.Combine(jars, "psdz-api.jar"), "not a zip file");
        Assert.ThrowsException<InvalidDataException>(() => LocalInstallation.Inspect("", _directory, ""));
    }

    [TestMethod]
    public void StrictSettingsSurfaceMalformedFilesAndRoundTripAddedPath()
    {
        string path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{broken");
        Assert.ThrowsException<JsonException>(() => AppSettings.LoadFrom(path));
        File.WriteAllText(path, "{\"Port\":0}");
        Assert.ThrowsException<FormatException>(() => AppSettings.LoadFrom(path));
        File.WriteAllText(path, "{\"future_setting\":{\"preserve\":true}}");
        var compatible = AppSettings.LoadFrom(path);
        compatible.SaveTo(path);
        StringAssert.Contains(File.ReadAllText(path), "future_setting");
        var settings = new AppSettings { Host = "test-host", EsysPath = "test-esys", PsdzSeries = "FTEST", JavaPath = "test-java" };
        settings.SaveTo(path);
        var loaded = AppSettings.LoadFrom(path);
        Assert.AreEqual("test-host", loaded.Host);
        Assert.AreEqual("test-esys", loaded.EsysPath);
        Assert.AreEqual("FTEST", loaded.PsdzSeries);
        Assert.AreEqual("test-java", loaded.JavaPath);
    }
}
