using BmwCoder.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Cli.Tests;

[TestClass]
public class CodingAnnotationsTests
{
    private static string AnnotationDir()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "data", "annotations")))
            dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "data", "annotations");
    }

    [TestMethod]
    public void Load_ReadsBlockNamesWithoutParameterEntries()
    {
        var notes = CodingAnnotations.Load(AnnotationDir(), "cafd_00001a33.caf.005_000_000");
        Assert.IsNotNull(notes);
        Assert.AreEqual("DSC_CODIERDATEN_ALLGEMEIN", notes.BlockNames[0x3000]);
        Assert.AreEqual("SIGNATURE", notes.BlockNames[0x300F]);
    }

    [TestMethod]
    public void Find_MatchesBitMasksAndMultiByteFieldsOfDscCaf()
    {
        const string caf = "cafd_00001a33.caf.005_000_000";
        var notes = CodingAnnotations.Load(AnnotationDir(), caf)!;
        var axle = new CodingParameter("3000/1", 0x3000, null, "0:0B 1:75", Array.Empty<string>(), "6", "7", "11111111b");
        Assert.AreEqual("C_Cp_Vorderachse", notes.Find(caf, axle)?.Label);
        Assert.AreEqual("GG", notes.Find(caf, axle)?.Meaning);
        var bit = new CodingParameter("3000/10", 0x3000, null, "0:00", Array.Empty<string>(), "12", "12", "00100000b");
        Assert.AreEqual("C_Bremsenkuehlluftschacht_verbaut_I", notes.Find(caf, bit)?.Label);
        Assert.IsNull(notes.Find(caf, bit with { Mask = "01000000b", Start = "13", End = "13" }));
    }

    [TestMethod]
    public void Find_LabelsWholeByteParametersOfAsdCaf()
    {
        const string caf = "cafd_00000f9b.caf.001_046_001";
        var notes = CodingAnnotations.Load(AnnotationDir(), caf);
        Assert.IsNotNull(notes);
        var model = new CodingParameter("3000/0", 0x3000, null, "0:0D", Array.Empty<string>(), "0", "0", "11111111b");
        var found = notes.Find(caf, model);
        Assert.AreEqual("Model Range", found?.Label);
        Assert.AreEqual("F020 / F021", found?.Meaning);
        Assert.IsTrue(found!.VersionVerified);
        Assert.IsFalse(notes.Find("cafd_00000f9b.caf.001_047_000", model)!.VersionVerified);
        Assert.IsNull(notes.Find(caf, model with { Mask = "00000001b" }));
        Assert.IsNull(notes.Find(caf, model with { BlockAddress = 0x3001 }));
        Assert.IsNull(CodingAnnotations.Load(AnnotationDir(), "cafd_00000ded.caf.003_015_016"));
    }

    [TestMethod]
    public void Find_DmeScreenshotMappingsRemainUnverified()
    {
        const string caf = "cafd_00000b0a.caf.000_115_002";
        var notes = CodingAnnotations.Load(AnnotationDir(), caf)!;
        var vmax = new CodingParameter("3300/4", 0x3300, null, "0:CB", Array.Empty<string>(), "2", "2", "11111111b");
        var exhaust = new CodingParameter("3320/10", 0x3320, null, "0:25", Array.Empty<string>(), "16", "16", "11111111b");
        Assert.AreEqual("VMAX-Codierung", notes.Find(caf, vmax)?.Label);
        Assert.AreEqual("UNKNOWN (current E-Sys read)", notes.Find(caf, vmax)?.Meaning);
        Assert.AreEqual("320 (E-Sys label; units not shown)", notes.Find(caf, vmax with { Value = "0:FA" })?.Meaning);
        Assert.AreEqual("Konfiguration Abgasklappensteuerung", notes.Find(caf, exhaust)?.Label);
        Assert.IsFalse(notes.Find(caf, vmax)!.VersionVerified);
        Assert.IsNull(notes.Find(caf, exhaust)!.Meaning);
    }
}
