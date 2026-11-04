using System.Text;
using System.Text.Json;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Core.Workspace;
using BmwCoder.Psdz;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Psdz.Tests;

[TestClass]
public sealed class CodingReaderTests
{
    private const string Caf = "cafd_00000001.caf.001_002_003";
    private const string Vin = "WBATEST0000000001";
    private const string Xml = """
        <caf xmlns="urn:synthetic">
          <sweHeader><cafIdent><processClass>CAFD</processClass><id>00000001</id><mainVersion>001</mainVersion><subVersion>002</subVersion><patchVersion>003</patchVersion></cafIdent></sweHeader>
          <data>
            <datagroup address="3000">
              <function name="Feature"><storageInfo><start>0</start><end>0</end><mask>00000001b</mask></storageInfo>
                <interpretation><paramName>enabled</paramName><values><unitVal position="0">01</unitVal></values></interpretation>
                <interpretation><paramName>unavailable enum</paramName></interpretation>
                <defVal><unitVal position="0">00</unitVal></defVal>
              </function>
              <function name="."><storageInfo><start>0</start><end>0</end><mask>00000010b</mask></storageInfo><interpretation><paramName>.</paramName><values><unitVal position="0">00</unitVal></values></interpretation><defVal><unitVal position="0">01</unitVal></defVal></function>
              <function name="inactive"><storageInfo><start>0</start><end>0</end><mask>00000100b</mask></storageInfo><defVal><unitVal position="0">01</unitVal></defVal></function>
            </datagroup>
            <datagroup address="3001"><function name="not byte-verified"><storageInfo/><defVal><unitVal position="0">FF</unitVal></defVal></function></datagroup>
          </data>
        </caf>
        """;
    private static readonly CodingBlock[] Blocks = { new(0x3000, "01"), new(0x3001, "AABBCC") };

    private static DecodedCoding Parse(string xml = Xml) =>
        DecodedCoding.Parse(Caf, "synthetic", null, null, "synthetic-runtime", "synthetic-original-ncd",
            xml, Blocks, new[] { 0x3000 });

    [TestMethod]
    public void CurrentValuesEnumsTrimmedNamesAndUnavailableFieldsStayDistinct()
    {
        var coding = Parse();
        Assert.AreEqual(3, coding.Parameters.Count);
        Assert.AreEqual("0:01", coding.Parameters[0].Value);
        CollectionAssert.AreEqual(new[] { "enabled" }, coding.Parameters[0].Interpretations.ToArray());
        Assert.IsNull(coding.Parameters[1].Name);
        Assert.AreEqual("3000/1", coding.Parameters[1].Id);
        Assert.IsFalse(coding.Parameters[2].IsDecoded);
        Assert.IsNull(coding.Parameters[2].Value);
        CollectionAssert.AreEqual(new[] { 0x3001 }, coding.OpaqueBlocks.ToArray());
        Assert.AreEqual("AABBCC", coding.Blocks[1].Hex);
    }

    [TestMethod]
    public void DecoderRefusesMismatchedCafVersionAndDuplicateCurrentUnits()
    {
        Assert.ThrowsException<FormatException>(() => Parse(Xml.Replace("<patchVersion>003", "<patchVersion>004")));
        Assert.ThrowsException<FormatException>(() => Parse(Xml.Replace(
            "<unitVal position=\"0\">01</unitVal>", "<unitVal position=\"0\">01</unitVal><unitVal position=\"0\">02</unitVal>")));
        Assert.ThrowsException<FormatException>(() => DecodedCoding.Parse(Caf, "", null, null, "test", "test",
            Xml, Blocks, new[] { 0x3999 }));
        Assert.ThrowsException<FormatException>(() => Parse(Xml.Replace(
            "<interpretation><paramName>unavailable enum</paramName></interpretation>",
            "<interpretation><paramName>another value</paramName><values><unitVal position=\"0\">02</unitVal></values></interpretation>")));
    }

    [TestMethod]
    public void OriginalExportPreservesTheReadSnapshotAndCannotOverwriteIt()
    {
        string path = Path.Combine(Path.GetTempPath(), "bimmeross-original-" + Guid.NewGuid().ToString("N") + ".ncd");
        try
        {
            var coding = Parse();
            coding.ExportOriginal(path);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(coding.OriginalNcd), File.ReadAllBytes(path));
            Assert.ThrowsException<IOException>(() => coding.ExportOriginal(path));
            Assert.AreEqual("AABBCC", coding.Blocks.Single(b => b.Address == 0x3001).Hex);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [DataTestMethod]
    [DataRow(0x40, Vin, Caf)]
    [DataRow(0x63, "WBATEST0000000002", Caf)]
    [DataRow(0x63, Vin, "cafd_00000001.caf.001_002_004")]
    public void LiveResponsesAreBoundToTheRequestedVinEcuAndCaf(int ecu, string vin, string caf)
    {
        var request = new CodingReadRequest("127.0.0.1", 6801, "F020", 0x63, "data", "ista", "java", Caf, Vin);
        var response = JsonSerializer.Serialize(new
        {
            schema = "bimmeross.psdz/1",
            ok = true,
            kind = "coding",
            ecu,
            vin,
            caf_file = caf
        });
        var error = Assert.ThrowsException<PsdzException>(() =>
            PsdzCodingReader.ParseResponse(new(response, "", 0), request, true));
        Assert.AreEqual("IDENTITY", error.Code);
    }

    [TestMethod]
    public void OfflineResponseDecodesWithoutClaimingAVehicleIdentity()
    {
        var request = new CodingReadRequest("", 0, "F020", 0, "data", "ista", "java", Caf);
        var response = JsonSerializer.Serialize(new
        {
            schema = "bimmeross.psdz/1",
            ok = true,
            kind = "coding",
            ecu = (int?)null,
            vin = (string?)null,
            caf_file = Caf,
            base_variant = "",
            version = "synthetic-runtime",
            ncd = "synthetic-original-ncd",
            xml = Xml,
            blocks = Blocks.Select(b => new { address = b.Address, hex = b.Hex }),
            verified_blocks = new[] { 0x3000 }
        });
        var coding = PsdzCodingReader.ParseResponse(new(response, "", 0), request, false);
        Assert.IsNull(coding.Vin);
        Assert.IsNull(coding.Ecu);
        Assert.AreEqual(2, coding.Parameters.Count(p => p.IsDecoded));
    }

    [TestMethod]
    public void NativeVerificationRejectsSdkDefaultsAndFailedReads()
    {
        var block = new CodingBlock(0x3000, "00000000");
        var mismatch = Assert.ThrowsException<PsdzException>(() =>
            PsdzCodingReader.VerifyNativeBlock(block, new DiagnosticReading { PayloadHex = "01020304" }));
        Assert.AreEqual("DATA_MISMATCH", mismatch.Code);
        var failed = Assert.ThrowsException<PsdzException>(() =>
            PsdzCodingReader.VerifyNativeBlock(block,
                new DiagnosticReading { Status = "error", Error = "Read rejected", PayloadHex = block.Hex }));
        Assert.AreEqual("VERIFICATION", failed.Code);
        PsdzCodingReader.VerifyNativeBlock(new CodingBlock(0x3000, "AABB"),
            new DiagnosticReading { PayloadHex = "aabb" });
    }

    [TestMethod]
    public async Task TemporaryDataViewUnlinksWithoutDeletingOrChangingSourceFiles()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows junction support is required.");
        string directory = Path.Combine(Path.GetTempPath(), "bimmeross-view's-" + Guid.NewGuid().ToString("N"));
        string ista = Path.Combine(directory, "ista");
        string source = Path.Combine(ista, "PSdZ", "data", "psdzdata");
        string external = Path.Combine(directory, "external");
        Directory.CreateDirectory(Path.Combine(source, "kiswb"));
        Directory.CreateDirectory(Path.Combine(source, "swe", "fafp"));
        Directory.CreateDirectory(Path.Combine(external, "swe", "cafd"));
        string providerFile = Path.Combine(source, "kiswb", "synthetic.txt");
        string cafFile = Path.Combine(external, "swe", "cafd", Caf);
        File.WriteAllText(providerFile, "synthetic ISTA metadata");
        File.WriteAllText(cafFile, "synthetic CAF placeholder");
        try
        {
            string viewPath;
            using (var view = await PsdzDataView.CreateAsync(ista, external, CancellationToken.None))
            {
                viewPath = view.Root;
                Assert.AreEqual("synthetic ISTA metadata", File.ReadAllText(Path.Combine(view.Root, "psdzdata", "kiswb", "synthetic.txt")));
                Assert.AreEqual("synthetic CAF placeholder", File.ReadAllText(Path.Combine(view.Root, "psdzdata", "swe", "cafd", Caf)));
            }
            Assert.IsFalse(Directory.Exists(viewPath));
            Assert.AreEqual("synthetic ISTA metadata", File.ReadAllText(providerFile));
            Assert.AreEqual("synthetic CAF placeholder", File.ReadAllText(cafFile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
