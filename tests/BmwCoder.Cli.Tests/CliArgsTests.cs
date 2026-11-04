using System.Text.Json;
using BmwCoder.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Cli.Tests;

[TestClass]
public class CliArgsTests
{
    [TestMethod]
    public void Parse_ReadCodingRequiresExplicitHostAndEcuWithoutRawDid()
    {
        string[] runtime = { "--data", "synthetic-data", "--ista", "synthetic-ista", "--java", "synthetic-java" };
        Assert.ThrowsException<CliUsageException>(() => CliArgs.Parse(new[] { "read-coding", "--ecu", "63" }.Concat(runtime).ToArray()));
        Assert.ThrowsException<CliUsageException>(() => CliArgs.Parse(new[] { "read-coding", "--host", "127.0.0.1" }.Concat(runtime).ToArray()));
        var parsed = CliArgs.Parse(new[] { "read-coding", "--host", "127.0.0.1", "--ecu", "63" }.Concat(runtime).ToArray());
        Assert.AreEqual(90000, parsed.TimeoutMs);
        Assert.IsFalse(parsed.HasDid);
        Assert.AreEqual((byte)0x63, parsed.Ecu);
    }

    [TestMethod]
    public void Parse_OfflineDecodeRequiresNcdAndExactCafButNoVehicle()
    {
        string[] runtime = { "--data", "synthetic-data", "--ista", "synthetic-ista", "--java", "synthetic-java" };
        Assert.ThrowsException<CliUsageException>(() => CliArgs.Parse(new[] { "decode", "--file", "synthetic.ncd" }.Concat(runtime).ToArray()));
        var parsed = CliArgs.Parse(new[] { "decode", "--file", "synthetic.ncd", "--caf", "cafd_00000001.caf.001_002_003" }.Concat(runtime).ToArray());
        Assert.IsFalse(parsed.HasHost);
        Assert.IsFalse(parsed.HasEcu);
    }

    [TestMethod]
    public void Parse_CafRequiresExplicitOfflineInputsWithoutEcuArguments()
    {
        Assert.ThrowsException<CliUsageException>(() => CliArgs.Parse(new[] { "caf", "--file", "test.caf" }));
        var parsed = CliArgs.Parse(new[] { "caf", "--file", "test.caf", "--ista", "local-ista", "--java", "local-java" });
        Assert.AreEqual("test.caf", parsed.FilePath);
        Assert.AreEqual("local-ista", parsed.IstaPath);
        Assert.AreEqual("local-java", parsed.JavaPath);
        Assert.IsFalse(parsed.HasEcu);
    }

    [TestMethod]
    public void Parse_GatewaysRequiresExplicitInterfaceAndBoundedTimeout()
    {
        var parsed = CliArgs.Parse(new[] { "gateways", "--local-ip", "169.254.1.1", "--timeout", "2000" });
        Assert.AreEqual("169.254.1.1", parsed.LocalIp);
        Assert.AreEqual(2000, parsed.TimeoutMs);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-an-ip")]
    [DataRow("::1")]
    [DataRow("0.0.0.0")]
    public void Parse_GatewaysRejectsMissingOrInvalidInterface(string address) =>
        Assert.ThrowsException<CliUsageException>(() =>
            CliArgs.Parse(new[] { "gateways", "--local-ip", address }));

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("30001")]
    public void Parse_GatewaysRejectsUnboundedTimeout(string timeout) =>
        Assert.ThrowsException<CliUsageException>(() =>
            CliArgs.Parse(new[] { "gateways", "--local-ip", "169.254.1.1", "--timeout", timeout }));

    [TestMethod]
    public void Parse_ReadAcceptsHexWithAndWithoutPrefix()
    {
        var args = CliArgs.Parse(new[] { "read", "--ecu", "0x63", "--did", "3000" });

        Assert.AreEqual("read", args.Command);
        Assert.AreEqual((byte)0x63, args.Ecu);
        Assert.AreEqual((ushort)0x3000, args.Did);
        Assert.IsFalse(args.Human);
        Assert.AreEqual(5000, args.TimeoutMs);
    }

    [TestMethod]
    public void Parse_MissingEcuIsUsage()
    {
        var ex = Assert.ThrowsException<CliUsageException>(() => CliArgs.Parse(new[] { "scan", "--from", "0x3000" }));
        Assert.IsTrue(ex.Message.Contains("--ecu"));
    }

    [TestMethod]
    public void Parse_PduStripsSpaces()
    {
        var args = CliArgs.Parse(new[] { "raw", "--ecu", "63", "--pdu", "22 F1 90" });
        CollectionAssert.AreEqual(new byte[] { 0x22, 0xF1, 0x90 }, args.Pdu);
    }

    [TestMethod]
    public void Envelope_FailIncludesCodeAndOmitsData()
    {
        string json = JsonEnvelope.Fail("read", "UDS_NRC", "negative", "0x31");
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("bmwcoder/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("UDS_NRC", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual("0x31", doc.RootElement.GetProperty("error").GetProperty("nrc").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("data", out _));
    }

    [TestMethod]
    public void Envelope_OkUsesSnakeCase()
    {
        string json = JsonEnvelope.Ok("discover", new { EcuCount = 2 });
        using var doc = JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(2, doc.RootElement.GetProperty("data").GetProperty("ecu_count").GetInt32());
        Assert.AreEqual(0, doc.RootElement.GetProperty("warnings").GetArrayLength());
    }
}
