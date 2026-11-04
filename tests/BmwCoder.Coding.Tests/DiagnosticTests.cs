using System.Buffers.Binary;
using BmwCoder.Core;
using BmwCoder.Core.Diagnostics;
using BmwCoder.Transport;
using BmwCoder.Uds;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Xml.Linq;

namespace BmwCoder.Coding.Tests;

[TestClass]
public class DiagnosticTests
{
    [TestMethod]
    public void OperatingTime_UsesInferred100MsTicksAndPreservesRaw()
    {
        var reading = DiagnosticDecoder.Decode(0x12, 0x4AB4, Convert.FromHexString("034555B3"));
        Assert.AreEqual("91459.285", reading.Values["Operating time (minutes, inferred)"]);
        Assert.AreEqual("1524.321", reading.Values["Operating time (hours, inferred)"]);
        Assert.AreEqual("54875571", reading.Values["Raw ticks"]);
        Assert.AreEqual("034555B3", reading.PayloadHex);
        StringAssert.Contains(reading.Warnings.Single(), "not independently confirmed");
        Assert.AreEqual(64, reading.Sha256!.Length);
    }

    [DataTestMethod]
    [DataRow("00000000", "0")]
    [DataRow("00000258", "1")]
    [DataRow("FFFFFFFF", "7158278.825")]
    public void OperatingTime_HandlesBounds(string hex, string minutes)
    {
        var reading = DiagnosticDecoder.Decode(0x12, 0x4AB4, Convert.FromHexString(hex));
        Assert.AreEqual(minutes, reading.Values["Operating time (minutes, inferred)"]);
    }

    [TestMethod]
    public void Decoder_DoesNotApplyDmeScaleToOtherEcus()
    {
        var reading = DiagnosticDecoder.Decode(0x63, 0x4AB4, Convert.FromHexString("034555B3"));
        Assert.IsFalse(reading.Values.ContainsKey("Raw ticks"));
    }

    [TestMethod]
    public void Decoder_DateAndSoftwareInventory()
    {
        var date = DiagnosticDecoder.Decode(0x12, 0xF18B, Convert.FromHexString("140811"));
        Assert.AreEqual("2014-08-11", date.Values["Manufacturing date"]);
        var svk = new byte[25];
        svk[0] = svk[1] = 1;
        svk[3] = 1;
        Convert.FromHexString("0600001A830A3B02").CopyTo(svk, 17);
        var software = DiagnosticDecoder.Decode(0x12, 0xF101, svk);
        Assert.AreEqual("00001A83", software.Software.Single().Id);
        Assert.AreEqual("10.59.2", software.Software.Single().Version);
        Assert.ThrowsException<FormatException>(() => DiagnosticDecoder.Decode(0x12, 0xF18B, Convert.FromHexString("14FF11")));
    }

    [TestMethod]
    public void Faults_DecodeStatusWithoutGuessingDescriptions()
    {
        var reading = new DiagnosticReading();
        DiagnosticDecoder.DecodeFaults(reading, Convert.FromHexString("FF1381042F138A042F"));
        Assert.AreEqual(2, reading.Faults.Count);
        Assert.AreEqual("138104", reading.Faults[0].Code);
        Assert.IsTrue(reading.Faults[0].TestFailed && reading.Faults[0].Confirmed && reading.Faults[0].Pending);
        Assert.IsFalse(reading.Faults[0].WarningIndicatorRequested);
        Assert.ThrowsException<FormatException>(() => DiagnosticDecoder.DecodeFaults(reading, new byte[2]));
    }

    [DataTestMethod]
    [DataRow("2EF19000")]
    [DataRow("1101")]
    [DataRow("1003")]
    [DataRow("2701")]
    [DataRow("14FFFFFF")]
    [DataRow("31010000")]
    public async Task ReadOnlyRequest_BlocksStateChangingServices(string hex)
    {
        var fake = new FakeUdsClient();
        using var session = new CodingSession(uds: fake);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            session.ReadOnlyRequestAsync(0x12, Convert.FromHexString(hex)));
        Assert.IsNull(fake.LastRawPdu);
    }

    [TestMethod]
    public async Task ReadDiagnosticDid_NrcAndMalformedDataPreserveResponses()
    {
        var fake = new FakeUdsClient
        {
            RawHandler = (ecu, pdu, token) => Task.FromResult(
                UdsResponse.FromPdu(ecu, Convert.FromHexString("7F2231")))
        };
        using var session = new CodingSession(uds: fake);
        var negative = await session.ReadDiagnosticDidAsync(0x12, 0xF187);
        Assert.AreEqual("nrc", negative.Status);
        Assert.AreEqual("0x31", negative.Nrc);
        Assert.AreEqual("7F2231", negative.ResponseHex);
        StringAssert.Contains(negative.Error!, "identifier");

        fake.RawHandler = (ecu, pdu, token) => Task.FromResult(
            UdsResponse.FromPdu(ecu, Convert.FromHexString("624AB401")));
        var malformed = await session.ReadDiagnosticDidAsync(0x12, 0x4AB4);
        Assert.AreEqual("01", malformed.PayloadHex);
        Assert.AreEqual("624AB401", malformed.ResponseHex);
        StringAssert.Contains(malformed.Warnings.Single(), "Expected 4");
    }

    [TestMethod]
    public async Task ReadDiagnosticDid_RejectsWrongDidEcho()
    {
        var fake = new FakeUdsClient
        {
            RawHandler = (ecu, pdu, token) => Task.FromResult(
                UdsResponse.FromPdu(ecu, Convert.FromHexString("62F19001")))
        };
        using var session = new CodingSession(uds: fake);
        await Assert.ThrowsExceptionAsync<HsfzException>(() => session.ReadDiagnosticDidAsync(0x12, 0x4AB4));
    }

    [TestMethod]
    public async Task ReadDiagnosticDid_RejectsNegativeReplyForAnotherService()
    {
        var fake = new FakeUdsClient
        {
            RawHandler = (ecu, _, _) => Task.FromResult(UdsResponse.FromPdu(ecu, Convert.FromHexString("7F1931")))
        };
        using var session = new CodingSession(uds: fake);
        await Assert.ThrowsExceptionAsync<HsfzException>(() => session.ReadDiagnosticDidAsync(0x12, 0xF190));
    }

    [TestMethod]
    public async Task DmeReport_UsesOnlyReadServicesAndContinuesAfterFailure()
    {
        var requests = new List<string>();
        var fake = new FakeUdsClient
        {
            RawHandler = (ecu, pdu, token) =>
            {
                Assert.AreEqual((byte)0x12, ecu);
                string request = Convert.ToHexString(pdu);
                requests.Add(request);
                if (request == "22F18B") throw new TimeoutException("Simulated offline read");
                if (request == "1902AF")
                    return Task.FromResult(UdsResponse.FromPdu(ecu, Convert.FromHexString("5902FF1381042F")));
                if (request == "224AB4")
                    return Task.FromResult(UdsResponse.FromPdu(ecu, Convert.FromHexString("624AB4034555B3")));
                return Task.FromResult(UdsResponse.FromPdu(ecu, Convert.FromHexString("7F2231")));
            }
        };
        using var session = new CodingSession(uds: fake);
        var report = await session.ReadDmeReportAsync();
        Assert.AreEqual(11, report.Count);
        Assert.AreEqual(11, requests.Count);
        Assert.IsTrue(requests.All(p => p.StartsWith("22", StringComparison.Ordinal) || p == "1902AF"));
        Assert.AreEqual("timeout", report.Single(r => r.Did == 0xF18B).Status);
        Assert.AreEqual("91459.285", report.Single(r => r.Did == 0x4AB4).Values["Operating time (minutes, inferred)"]);
        Assert.AreEqual("138104", report.Last().Faults.Single().Code);
    }

    [TestMethod]
    public async Task DmeReport_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var session = new CodingSession(uds: new FakeUdsClient());
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            session.ReadDmeReportAsync(cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task Faults_DoNotTreatIncompleteTestsAsFailuresOrLookUpTheirDescriptions()
    {
        var fake = new FakeUdsClient
        {
            RawHandler = (ecu, pdu, _) => Task.FromResult(UdsResponse.FromPdu(ecu,
                Convert.FromHexString("5902FF12345650123457401234582F")))
        };
        using var session = new CodingSession(uds: fake);
        var reading = await session.ReadFaultMemoryAsync(0x72);
        CollectionAssert.AreEqual(Convert.FromHexString("1902AF"), fake.LastRawPdu!);
        Assert.AreEqual("3", reading.Values["DTC status records returned"]);
        Assert.AreEqual("1", reading.Values["Failure/history records"]);
        Assert.AreEqual("2", reading.Values["Test-status-only records (not faults)"]);
        Assert.IsFalse(reading.Faults[0].HasFailureEvidence);
        Assert.IsTrue(reading.Faults[0].TestNotCompletedSinceClear);
        Assert.IsTrue(reading.Faults[1].TestNotCompletedThisCycle);
        Assert.IsTrue(reading.Faults[2].HasFailureEvidence);
        Assert.IsTrue(reading.Faults[2].TestFailedSinceClear);
        Assert.IsTrue(reading.Faults[2].TestFailedThisCycle);
    }

    [TestMethod]
    public void PsdzCatalog_LoadsOnlyReadDidMappingsAndSkipsRanges()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-odx-{Guid.NewGuid():N}");
        try
        {
            DiagnosticDataTests.CreateOdx(root, "Z999_01", "1");
            string file = Path.Combine(root, "mainseries", "Z999", "Z999_01", "odx", "src", "odx-d", "FUTURE_ENGINE.odx-d");
            var document = XDocument.Load(file);
            var did = document.Descendants("REQUEST").Single().Descendants("PARAM")
                .Single(p => p.Element("BYTE-POSITION")?.Value == "1");
            did.Element("CODED-VALUE")!.Remove();
            did.Add(new XElement("DOP-REF", new XAttribute("ID-REF", "identifiers")));
            document.Root!.Add(XElement.Parse("""
                <DATA-OBJECT-PROP ID="identifiers"><COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY>
                <COMPU-SCALE><LOWER-LIMIT>19124</LOWER-LIMIT><UPPER-LIMIT>19124</UPPER-LIMIT><COMPU-CONST><VT>OperatingTime</VT></COMPU-CONST></COMPU-SCALE>
                <COMPU-SCALE><LOWER-LIMIT>61440</LOWER-LIMIT><UPPER-LIMIT>61455</UPPER-LIMIT><COMPU-CONST><VT>Reserved</VT></COMPU-CONST></COMPU-SCALE>
                </COMPU-METHOD></DATA-OBJECT-PROP>
                """));
            document.Root.Add(XElement.Parse("""
                <DATA-OBJECT-PROP ID="unused-write-identifiers"><COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY>
                <COMPU-SCALE><LOWER-LIMIT>1234</LOWER-LIMIT><COMPU-CONST><VT>WriteOnly</VT></COMPU-CONST></COMPU-SCALE>
                </COMPU-METHOD></DATA-OBJECT-PROP>
                """));
            document.Save(file);
            var definitions = DiagnosticCatalog.LoadPsdzDids(root, "Z999", 0xA2);
            Assert.AreEqual(1, definitions.Count);
            Assert.AreEqual((ushort)0x4AB4, definitions[0].Did);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
