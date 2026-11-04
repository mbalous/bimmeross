using BmwCoder.Core;
using BmwCoder.Core.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Xml.Linq;

namespace BmwCoder.Coding.Tests;

[TestClass]
public sealed class DiagnosticDataTests : IDisposable
{
    private readonly string _backupDirectory = Path.Combine(Path.GetTempPath(), $"bmwcoder-backups-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_backupDirectory))
            Directory.Delete(_backupDirectory, true);
    }

    [TestMethod]
    public async Task Discovery_UsesGatewayListIncludingUnknownReadOnlyModules()
    {
        var fake = new FakeUdsClient
        {
            TopologyData = Convert.FromHexString("000212A2"),
            ProbeHandler = (_, _) => Task.CompletedTask
        };
        using var session = new CodingSession(uds: fake, backupManager: new BackupManager(_backupDirectory));
        var ecus = await session.DiscoverEcusAsync();
        CollectionAssert.AreEqual(new byte[] { 0x12, 0xA2 }, ecus.Select(e => e.Address).ToArray());
        Assert.IsTrue(ecus.All(e => e.Responds));
        Assert.AreEqual("Unknown(0xA2)", ecus[1].Name);
        Assert.IsFalse(ecus[1].IsWritable);
        StringAssert.Contains(ecus[1].DiscoverySource, "0x3F15");
        Assert.AreEqual(0, session.DiscoveryWarnings.Count);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("00")]
    [DataRow("0000")]
    [DataRow("000212")]
    [DataRow("00011278")]
    [DataRow("00021212")]
    [DataRow("010112")]
    public void Topology_RejectsInvalidCountsAndDuplicateAddresses(string hex) =>
        Assert.ThrowsException<FormatException>(() => VehicleTopology.DecodeEcuList(Convert.FromHexString(hex)));

    [TestMethod]
    public async Task Discovery_MalformedInventoryHasExplicitFallbackWarning()
    {
        var fake = new FakeUdsClient { TopologyData = new byte[] { 0 }, ProbeHandler = (_, _) => Task.CompletedTask };
        using var session = new CodingSession(uds: fake, backupManager: new BackupManager(_backupDirectory));
        var ecus = await session.DiscoverEcusAsync();
        StringAssert.Contains(session.DiscoveryWarnings.Single(), "not a complete vehicle inventory");
        Assert.IsTrue(ecus.All(e => e.DiscoverySource.Contains("fallback")));
    }

    [TestMethod]
    public async Task Discovery_DividesProbeBudgetByActualInventorySize()
    {
        var fake = new FakeUdsClient
        {
            TopologyData = new byte[] { 0, 24 }.Concat(Enumerable.Range(1, 24).Select(i => (byte)i)).ToArray(),
            ProbeHandler = async (_, token) => await Task.Delay(Timeout.Infinite, token)
        };
        using var session = new CodingSession(uds: fake, backupManager: new BackupManager(_backupDirectory));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ecus = await session.DiscoverEcusAsync(cancellationToken: deadline.Token,
            probeTimeout: TimeSpan.FromSeconds(5), discoveryBudget: TimeSpan.FromMilliseconds(900));
        Assert.AreEqual(24, ecus.Count);
        Assert.IsTrue(ecus.All(e => !e.Responds && e.ProbeError != null));
    }

    [TestMethod]
    public async Task Odx_UsesArbitrarySeriesNamesAddressesFieldsAndUpdatedVersions()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-data-{Guid.NewGuid():N}");
        try
        {
            CreateOdx(root, "Z999_01", "0.5");
            CreateOdx(root, "Z999_02", "1");
            var data = PsdzDiagnosticData.Load(root, "Z999");
            Assert.AreEqual("FUTURE_ENGINE", data.GetEcuName(0xA2));
            Assert.IsNull(data.GetEcuName(0x12));
            Assert.AreEqual((ushort)0x9876, data.GetDids(0xA2).Single().Did);
            var reading = data.Decode(0xA2, 0x9876, Convert.FromHexString("24012C01"))!;
            StringAssert.Contains(reading.DecoderSource!, "Z999_02");
            Assert.AreEqual("24", reading.Values["Payload / Year"]);
            Assert.AreEqual("Ready (1)", reading.Values["Payload / State"]);
            Assert.AreEqual("260 degC", reading.Values["Payload / Temperature"]);
            Assert.IsNull(data.Decode(0xA2, 0x9999, new byte[4]));
            var fake = new FakeUdsClient { TopologyData = Convert.FromHexString("0001A2"), ProbeHandler = (_, _) => Task.CompletedTask };
            using var session = new CodingSession(uds: fake, backupManager: new BackupManager(_backupDirectory)) { DiagnosticData = data };
            var ecu = (await session.DiscoverEcusAsync()).Single();
            Assert.AreEqual("FUTURE_ENGINE", ecu.Name);
            Assert.IsFalse(ecu.IsWritable);
            var malformed = data.Decode(0xA2, 0x9876, Convert.FromHexString("FF012C"))!;
            Assert.IsTrue(malformed.Warnings.Any(w => w.Contains("BCD")));
            Assert.IsTrue(malformed.Warnings.Any(w => w.Contains("beyond the response")));
            Assert.AreEqual("Ready (1)", malformed.Values["Payload / State"]);
            Assert.AreEqual("FF012C", malformed.PayloadHex);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Odx_UnknownDmeIdentityDoesNotReceiveObservedOperatingTimeScale()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-identity-{Guid.NewGuid():N}");
        try
        {
            CreateOdx(root, "Z999_01", "1");
            var fake = new FakeUdsClient
            {
                RawHandler = (ecu, pdu, _) => Task.FromResult(BmwCoder.Uds.UdsResponse.FromPdu(ecu,
                    Convert.FromHexString(pdu[1] == 0xF1 ? "62F1500F1EA1" : "624AB4034555B3")))
            };
            using var session = new CodingSession(uds: fake) { DiagnosticData = PsdzDiagnosticData.Load(root, "Z999") };
            await session.ReadDiagnosticDidAsync(0x12, 0xF150);
            var reading = await session.ReadDiagnosticDidAsync(0x12, 0x4AB4);
            Assert.IsFalse(reading.Values.Keys.Any(k => k.Contains("Operating time")));
            Assert.AreEqual("034555B3", reading.PayloadHex);
            Assert.IsTrue(reading.Warnings.Any(w => w.Contains("No matching ODX")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Odx_ResolvesContainerAliasesInheritanceAndResponseSwitches()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-inheritance-{Guid.NewGuid():N}");
        try
        {
            CreateOdx(root, "Z999_01", "1");
            string directory = Path.Combine(root, "mainseries", "Z999", "Z999_01", "odx", "src", "odx-d");
            string engine = Path.Combine(directory, "FUTURE_ENGINE.odx-d");
            var document = XDocument.Load(engine);
            document.Descendants("DIAG-SERVICE").Single().Add(new XElement("SHORT-NAME", "ReadFuture"));
            var did = document.Descendants("REQUEST").Single().Descendants("PARAM")
                .Single(p => p.Element("BYTE-POSITION")?.Value == "1");
            did.Element("CODED-VALUE")!.Remove();
            did.Add(new XElement("DOP-REF", new XAttribute("ID-REF", "did-choices")));
            document.Root!.Add(XElement.Parse("""
                <DATA-OBJECT-PROP ID="did-choices"><DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
                <COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY><COMPU-SCALE><LOWER-LIMIT>39030</LOWER-LIMIT><UPPER-LIMIT>39030</UPPER-LIMIT>
                <COMPU-CONST><VT>Future payload</VT></COMPU-CONST></COMPU-SCALE></COMPU-METHOD></DATA-OBJECT-PROP>
                """));
            var payload = document.Descendants("POS-RESPONSE").Single().Descendants("PARAM").Last();
            payload.Element("BYTE-POSITION")!.Value = "1";
            payload.Element("DOP-REF")!.SetAttributeValue("ID-REF", "switch");
            document.Root.Add(XElement.Parse("""
                <MUX ID="switch"><BYTE-POSITION>2</BYTE-POSITION><SWITCH-KEY><BYTE-POSITION>0</BYTE-POSITION>
                <DATA-OBJECT-PROP-REF ID-REF="did-choices"/></SWITCH-KEY><CASES><CASE>
                <LOWER-LIMIT>39030</LOWER-LIMIT><UPPER-LIMIT>39030</UPPER-LIMIT><STRUCTURE-REF ID-REF="structure"/>
                </CASE></CASES></MUX>
                """));
            new XDocument(new XElement("ODX", new XElement("DIAG-LAYER-CONTAINER",
                new XElement("SHORT-NAME", "Shared_container"), new XElement("ECU-SHARED-DATAS",
                    new XElement("ECU-SHARED-DATA", new XAttribute("ID", "shared"),
                        new XElement("SHORT-NAME", "Shared_layer"), document.Root.Elements())))))
                .Save(Path.Combine(directory, "Shared_definitions.odx-d"));
            var parent = new XElement("PARENT-REF", new XAttribute("DOCREF", "Shared_container"),
                new XAttribute("ID-REF", "shared"));
            new XDocument(new XElement("ODX", new XElement("DIAG-LAYER-CONTAINER",
                new XElement("SHORT-NAME", "Engine_container"), new XElement("BASE-VARIANTS",
                    new XElement("BASE-VARIANT", new XAttribute("ID", "engine"),
                        new XElement("SHORT-NAME", "FUTURE_ENGINE"), new XElement("PARENT-REFS", parent))))))
                .Save(engine);
            var data = PsdzDiagnosticData.Load(root, "Z999");
            Assert.AreEqual("Future payload", data.GetDids(0xA2).Single().Name);
            var reading = data.Decode(0xA2, 0x9876, Convert.FromHexString("24012C01"))!;
            Assert.AreEqual("260 degC", reading.Values["Payload / Temperature"]);
            StringAssert.Contains(reading.DecoderSource!, "Shared_definitions.odx-d");
            var excluded = XDocument.Load(engine);
            excluded.Descendants("PARENT-REF").Single().Add(new XElement("NOT-INHERITED-DIAG-COMMS",
                new XElement("DIAG-COMM-SNREF", new XAttribute("SHORT-NAME", "ReadFuture"))));
            excluded.Save(engine);
            Assert.AreEqual(0, PsdzDiagnosticData.Load(root, "Z999").GetDids(0xA2).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Odx_ConflictingResponseLayoutsDoNotSelectAnArbitraryInterpretation()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-conflict-{Guid.NewGuid():N}");
        try
        {
            CreateOdx(root, "Z999_01", "1");
            string file = Path.Combine(root, "mainseries", "Z999", "Z999_01", "odx", "src", "odx-d", "FUTURE_ENGINE.odx-d");
            var document = XDocument.Load(file);
            var alternative = new XElement(document.Descendants("POS-RESPONSE").Single());
            alternative.SetAttributeValue("ID", "alternative");
            alternative.Descendants("PARAM").Last().Element("LONG-NAME")!.Value = "Different layout";
            document.Root!.Add(alternative);
            document.Descendants("DIAG-SERVICE").Single().Add(new XElement("POS-RESPONSE-REF", new XAttribute("ID-REF", "alternative")));
            document.Save(file);
            var reading = PsdzDiagnosticData.Load(root, "Z999").Decode(0xA2, 0x9876, Convert.FromHexString("24012C01"))!;
            Assert.AreEqual(0, reading.Values.Count);
            StringAssert.Contains(reading.Warnings.Single(), "Conflicting ODX layouts");
            Assert.AreEqual("24012C01", reading.PayloadHex);
            Assert.AreEqual(64, reading.Sha256!.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Odx_PhysicalDefaultDoesNotAssignASpecializedResponseToUnrelatedDids()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-default-{Guid.NewGuid():N}");
        try
        {
            CreateOdx(root, "Z999_01", "1");
            string file = Path.Combine(root, "mainseries", "Z999", "Z999_01", "odx", "src", "odx-d", "FUTURE_ENGINE.odx-d");
            var document = XDocument.Load(file);
            var did = document.Descendants("REQUEST").Single().Descendants("PARAM")
                .Single(p => p.Element("BYTE-POSITION")?.Value == "1");
            did.Element("CODED-VALUE")!.Remove();
            did.Add(new XElement("PHYSICAL-DEFAULT-VALUE", "Specialized"));
            did.Add(new XElement("DOP-REF", new XAttribute("ID-REF", "identifiers")));
            document.Root!.Add(XElement.Parse("""
                <DATA-OBJECT-PROP ID="identifiers"><COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY>
                <COMPU-SCALE><LOWER-LIMIT>39030</LOWER-LIMIT><UPPER-LIMIT>39030</UPPER-LIMIT><COMPU-CONST><VT>Specialized</VT></COMPU-CONST></COMPU-SCALE>
                <COMPU-SCALE><LOWER-LIMIT>39031</LOWER-LIMIT><UPPER-LIMIT>39031</UPPER-LIMIT><COMPU-CONST><VT>Unrelated</VT></COMPU-CONST></COMPU-SCALE>
                </COMPU-METHOD></DATA-OBJECT-PROP>
                """));
            document.Save(file);
            var data = PsdzDiagnosticData.Load(root, "Z999");
            Assert.AreEqual((ushort)0x9876, data.GetDids(0xA2).Single().Did);
            Assert.IsNull(data.Decode(0xA2, 0x9877, Convert.FromHexString("24012C01")));
            Assert.AreEqual("260 degC", data.Decode(0xA2, 0x9876, Convert.FromHexString("24012C01"))!.Values["Payload / Temperature"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Ista_ExactIndexedDescriptionsMissingCodesAndAmbiguityPreserveFaults()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bmwcoder-ista-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "xmlvalueprimitive_ENGB.sqlite")};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE xmlvalueprimitive (id INTEGER, modified INTEGER, deleted INTEGER, data TEXT, PRIMARY KEY (id,modified));
                    CREATE VIRTUAL TABLE fts USING fts5(id,data);
                    INSERT INTO xmlvalueprimitive VALUES (10,1,0,'<TEXT><LISTENTRY>123456 Sample actuator, open circuit</LISTENTRY></TEXT>');
                    INSERT INTO xmlvalueprimitive VALUES (11,1,0,'<TEXT><LISTENTRY>123457 Different description</LISTENTRY></TEXT>');
                    INSERT INTO xmlvalueprimitive VALUES (12,1,0,'<TEXT><LISTENTRY>123457 Conflicting description</LISTENTRY></TEXT>');
                    INSERT INTO xmlvalueprimitive VALUES (13,1,0,'<TEXT><LISTENTRY>0x123459 Prefixed code description</LISTENTRY></TEXT>');
                    INSERT INTO fts SELECT id,data FROM xmlvalueprimitive;
                    """;
                command.ExecuteNonQuery();
            }
            var lookup = new IstaFaultLookup(root);
            Assert.AreEqual("Sample actuator, open circuit", lookup.Lookup("123456").Single().Description);
            Assert.AreEqual(0, lookup.Lookup("123458").Count);
            Assert.AreEqual(2, lookup.Lookup("123457").Count);
            Assert.AreEqual("Prefixed code description", lookup.Lookup("123459").Single().Description);
            Assert.ThrowsException<ArgumentException>(() => lookup.Lookup("1234 OR 1"));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => lookup.Lookup("123456", canceled.Token));
            var fake = new FakeUdsClient
            {
                RawHandler = (ecu, _, _) => Task.FromResult(BmwCoder.Uds.UdsResponse.FromPdu(ecu,
                    Convert.FromHexString("5902FF1234562F1234572F1234582F")))
            };
            using var session = new CodingSession(uds: fake) { FaultDescriptions = lookup };
            var reading = await session.ReadFaultMemoryAsync(0xA2);
            Assert.AreEqual("Sample actuator, open circuit", reading.Faults[0].Description);
            Assert.AreEqual("10", reading.Faults[0].SourceDocumentId);
            Assert.IsNull(reading.Faults[1].Description);
            Assert.AreEqual("ambiguous", reading.Faults[1].DescriptionStatus);
            Assert.AreEqual("not_found", reading.Faults[2].DescriptionStatus);
            Assert.AreEqual("FF1234562F1234572F1234582F", reading.PayloadHex);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void CreateOdx(string root, string version, string multiplier)
    {
        string source = Path.Combine(root, "mainseries", "Z999", version, "odx", "src");
        foreach (string directory in new[] { "odx-c", "odx-v", "odx-d" })
            Directory.CreateDirectory(Path.Combine(source, directory));
        File.WriteAllText(Path.Combine(source, "odx-c", "WIRE.odx-c"), """
            <ODX><COMPARAM ID="new-address-id"><SHORT-NAME>DIAG_ADDRESS</SHORT-NAME></COMPARAM></ODX>
            """);
        File.WriteAllText(Path.Combine(source, "odx-v", "Z999.odx-v"), """
            <ODX><LOGICAL-LINK><BASE-VARIANT-REF DOCREF="FUTURE_ENGINE"/>
            <LINK-COMPARAM-REF DOCREF="WIRE" ID-REF="new-address-id"><VALUE>162</VALUE></LINK-COMPARAM-REF>
            </LOGICAL-LINK></ODX>
            """);
        File.WriteAllText(Path.Combine(source, "odx-d", "FUTURE_ENGINE.odx-d"), $$"""
            <ODX>
            <DIAG-SERVICE ID="read"><REQUEST-REF ID-REF="req"/><POS-RESPONSE-REF ID-REF="resp"/><LONG-NAME>Future data</LONG-NAME></DIAG-SERVICE>
            <REQUEST ID="req"><PARAMS>
            <PARAM><BYTE-POSITION>0</BYTE-POSITION><CODED-VALUE>34</CODED-VALUE></PARAM>
            <PARAM><BYTE-POSITION>1</BYTE-POSITION><CODED-VALUE>39030</CODED-VALUE></PARAM>
            </PARAMS></REQUEST>
            <POS-RESPONSE ID="resp"><PARAMS>
            <PARAM><BYTE-POSITION>0</BYTE-POSITION><CODED-VALUE>98</CODED-VALUE></PARAM>
            <PARAM><LONG-NAME>Payload</LONG-NAME><BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="structure"/></PARAM>
            </PARAMS></POS-RESPONSE>
            <STRUCTURE ID="structure"><PARAMS>
            <PARAM><LONG-NAME>Year</LONG-NAME><BYTE-POSITION>0</BYTE-POSITION><DOP-REF ID-REF="bcd"/></PARAM>
            <PARAM><LONG-NAME>State</LONG-NAME><BYTE-POSITION>1</BYTE-POSITION><DOP-REF ID-REF="state"/></PARAM>
            <PARAM><LONG-NAME>Temperature</LONG-NAME><BYTE-POSITION>2</BYTE-POSITION><DOP-REF ID-REF="temp"/></PARAM>
            </PARAMS></STRUCTURE>
            <DATA-OBJECT-PROP ID="bcd"><COMPU-METHOD><CATEGORY>IDENTICAL</CATEGORY></COMPU-METHOD><DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" BASE-TYPE-ENCODING="BCD-P"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></DATA-OBJECT-PROP>
            <DATA-OBJECT-PROP ID="state"><DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            <COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY><COMPU-SCALE><LOWER-LIMIT>1</LOWER-LIMIT><UPPER-LIMIT>1</UPPER-LIMIT><VT>Ready</VT></COMPU-SCALE></COMPU-METHOD></DATA-OBJECT-PROP>
            <DATA-OBJECT-PROP ID="temp"><DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" IS-HIGHLOW-BYTE-ORDER="false"><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
            <UNIT-REF ID-REF="unit"/><COMPU-METHOD><CATEGORY>LINEAR</CATEGORY><COMPU-SCALE><COMPU-RATIONAL-COEFFS>
            <COMPU-NUMERATOR><V>-40</V><V>{{multiplier}}</V></COMPU-NUMERATOR><COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>
            </COMPU-RATIONAL-COEFFS></COMPU-SCALE></COMPU-METHOD></DATA-OBJECT-PROP>
            <UNIT ID="unit"><DISPLAY-NAME>degC</DISPLAY-NAME></UNIT>
            </ODX>
            """);
    }
}
