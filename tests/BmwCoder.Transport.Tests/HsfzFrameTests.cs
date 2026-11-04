using System;
using BmwCoder.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Transport.Tests;

[TestClass]
public class HsfzFrameTests
{
    [TestMethod]
    public void Encode_TesterPresentToCas_ProducesExpectedWireBytes()
    {
        // Arrange
        // TesterPresent: 0x3E 0x00
        // Target: CAS (0x40), Source: 0xF4, Control: 0x0001
        // Length = 2 + 2 = 4 (0x00 0x00 0x00 0x04)
        var frame = new HsfzFrame(0x40, new byte[] { 0x3E, 0x00 }, 0xF4, 0x0001);

        // Act
        byte[] bytes = frame.ToByteArray();

        // Assert
        byte[] expected = new byte[]
        {
            0x00, 0x00, 0x00, 0x04, // Length uint32 BE (2 + 2 = 4)
            0x00, 0x01,             // ControlWord uint16 BE (0x0001)
            0xF4,                   // SourceAddress (0xF4)
            0x40,                   // TargetAddress (0x40)
            0x3E, 0x00              // Payload (TesterPresent)
        };

        CollectionAssert.AreEqual(expected, bytes);
    }

    [TestMethod]
    public void Decode_ValidBytes_ProducesMatchingFrame()
    {
        // Arrange
        byte[] wire = new byte[]
        {
            0x00, 0x00, 0x00, 0x05, // length = 5 (2 + 3 payload bytes)
            0x00, 0x01,             // control
            0x10,                   // src (ZGW Gateway)
            0xF4,                   // dst (Tester)
            0x62, 0xF1, 0x90        // payload (positive response to ReadDataByIdentifier VIN)
        };

        // Act
        var frame = HsfzFrame.FromBytes(wire);

        // Assert
        Assert.AreEqual(0x0001, frame.ControlWord);
        Assert.AreEqual(0x10, frame.SourceAddress);
        Assert.AreEqual(0xF4, frame.TargetAddress);
        CollectionAssert.AreEqual(new byte[] { 0x62, 0xF1, 0x90 }, frame.Payload);
    }

    [TestMethod]
    public void RoundTrip_EncodeThenDecode_PreservesAllFields()
    {
        // Arrange
        byte[] payload = new byte[] { 0x22, 0x30, 0x00 }; // ReadDataByIdentifier 0x3000
        var original = new HsfzFrame(0x63, payload, 0xF4, 0x0001);

        // Act
        byte[] encoded = original.ToByteArray();
        var decoded = HsfzFrame.FromBytes(encoded);

        // Assert
        Assert.AreEqual(original.ControlWord, decoded.ControlWord);
        Assert.AreEqual(original.SourceAddress, decoded.SourceAddress);
        Assert.AreEqual(original.TargetAddress, decoded.TargetAddress);
        CollectionAssert.AreEqual(original.Payload, decoded.Payload);
        Assert.AreEqual(original, decoded);
    }

    [TestMethod]
    public void Decode_TooShortHeader_ThrowsHsfzException()
    {
        byte[] invalid = new byte[] { 0x00, 0x00, 0x00, 0x02 }; // only 4 bytes
        Assert.ThrowsException<HsfzException>(() => HsfzFrame.FromBytes(invalid));
    }

    [TestMethod]
    public void Decode_LengthFieldTooSmall_ThrowsHsfzException()
    {
        byte[] invalid = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, // length 1 is invalid (must be >= 2 for src+dst)
            0x00, 0x01,
            0xF4,
            0x40
        };

        Assert.ThrowsException<HsfzException>(() => HsfzFrame.FromBytes(invalid));
    }

    [TestMethod]
    public void Decode_IncompletePayload_ThrowsHsfzException()
    {
        byte[] truncated = new byte[]
        {
            0x00, 0x00, 0x00, 0x08, // claims 8 bytes (6 payload bytes)
            0x00, 0x01,
            0xF4,
            0x40,
            0x01, 0x02 // only 2 payload bytes provided
        };

        Assert.ThrowsException<HsfzException>(() => HsfzFrame.FromBytes(truncated));
    }
}
