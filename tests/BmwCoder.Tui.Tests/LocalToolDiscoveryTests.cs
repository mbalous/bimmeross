using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Tui.Tests;

[TestClass]
public sealed class LocalToolDiscoveryTests
{
    [TestMethod]
    public void DetectionSkipsIncompleteLayoutsAndPrefersFirstValidCandidates()
    {
        string root = Path.Combine(Path.GetTempPath(), "bimmeross-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ista = Path.Combine(root, "ISTA");
            string data = Path.Combine(root, "psdzdata");
            string java = Path.Combine(root, "java", "bin", "java.exe");
            string esys = Path.Combine(root, "esys");
            Jar(Path.Combine(ista, "PSdZ", "binx64", "psdz", "psdz-api.jar"),
                "com/bmw/psdz/api/ServiceLocator.class", "com/bmw/psdz/api/Configuration.class", "com/bmw/psdz/api/Logic.class");
            Jar(Path.Combine(ista, "PSdZ", "data", "psdzdata", "extLibs", "codesyslib.jar"), "provider");
            Jar(Path.Combine(ista, "PSdZ", "data", "psdzdata", "extLibs", "kis.jar"), "provider");
            string prodias = Path.Combine(ista, "PSdZ", "binx64", "prodias");
            Directory.CreateDirectory(prodias);
            File.WriteAllText(Path.Combine(prodias, "native.dll"), "fixture");
            Directory.CreateDirectory(Path.Combine(ista, "PSdZ", "data", "psdzdata", "kiswb"));
            Directory.CreateDirectory(Path.Combine(ista, "PSdZ", "data", "psdzdata", "swe"));
            File.WriteAllText(Path.Combine(ista, "PSdZ", "data", "psdzdata", "kiswb", "metadata"), "fixture");
            Directory.CreateDirectory(Path.Combine(ista, "PSdZ", "data", "psdzdata", "swe", "other"));
            Directory.CreateDirectory(Path.Combine(data, "swe", "cafd"));
            File.WriteAllText(Path.Combine(data, "swe", "cafd", "cafd_00000001.caf.001_000_000"), "fixture");
            Java(java, "1.8.0_511", 0x8664);
            Directory.CreateDirectory(Path.Combine(esys, "jre", "bin"));
            File.WriteAllText(Path.Combine(esys, "jre", "bin", "java.exe"), "fixture");
            Jar(Path.Combine(esys, "lib", "PsdZ", "psdz-api.jar"), "api");
            Jar(Path.Combine(esys, "lib", "PsdZ", "psdz-core.jar"), "core");

            var found = LocalToolDiscovery.Detect(new ToolCandidates(
                new[] { Path.Combine(root, "missing"), ista }, new[] { root, data },
                new[] { Path.Combine(root, "missing.exe"), java }, new[] { esys }));
            Assert.AreEqual(ista, found.Ista);
            Assert.AreEqual(data, found.PsdzData);
            Assert.AreEqual(java, found.Java);
            Assert.AreEqual(esys, found.Esys);

            File.Delete(Path.Combine(ista, "PSdZ", "data", "psdzdata", "extLibs", "kis.jar"));
            File.Delete(Path.Combine(data, "swe", "cafd", "cafd_00000001.caf.001_000_000"));
            Java(java, "1.8.0_511", 0x014c);
            File.WriteAllText(Path.Combine(esys, "lib", "PsdZ", "psdz-core.jar"), "not a jar");
            found = LocalToolDiscovery.Detect(new ToolCandidates(new[] { ista }, new[] { data }, new[] { java }, new[] { esys }));
            Assert.AreEqual("", found.Ista);
            Assert.AreEqual("", found.PsdzData);
            Assert.AreEqual("", found.Java);
            Assert.AreEqual("", found.Esys);
            Assert.ThrowsException<FormatException>(() => LocalToolsDialog.ValidatePaths("", data, "", "", "F020"));
            Assert.ThrowsException<FormatException>(() => LocalToolsDialog.ValidatePaths("", "", "", "", "F_TEST"));
            LocalToolsDialog.ValidatePaths("", "", "", "", "F020");

            Java(java, "17.0.1", 0x8664);
            Assert.IsFalse(LocalToolDiscovery.IsJava8(java));
            Java(java, "1.8.0_511", 0x8664);
            File.WriteAllText(java, "not an executable");
            Assert.IsFalse(LocalToolDiscovery.IsJava8(java));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Jar(string path, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("fixture");
        }
    }

    private static void Java(string path, string version, ushort machine)
    {
        string bin = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(Directory.GetParent(bin)!.FullName, "release"),
            $"JAVA_VERSION=\"{version}\"\nOS_ARCH=\"amd64\"\n");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write((ushort)0x5A4D);
        stream.Position = 0x3c;
        writer.Write(64);
        writer.Write(0x00004550);
        writer.Write(machine);
    }
}
