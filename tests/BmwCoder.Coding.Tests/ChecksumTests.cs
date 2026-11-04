using System;
using System.Text;
using BmwCoder.Coding;
using BmwCoder.Coding.Checksum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Coding.Tests;

[TestClass]
public class ChecksumTests
{
    [TestMethod]
    public void Crc16Ccitt_KnownVector_ComputesAccurately()
    {
        // "123456789" with CRC16-CCITT (poly 0x1021, init 0xFFFF) = 0x29B1
        var crc = new Crc16Ccitt(0xFFFF);
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        ushort result = crc.Compute(data);

        Assert.AreEqual(0x29B1, result);
    }

    [TestMethod]
    public void Crc16Ccitt_ApplyAndVerify_SucceedsAndDetectsTampering()
    {
        var crc = new Crc16Ccitt(0xFFFF);
        byte[] block = new byte[64];
        for (int i = 0; i < 62; i++) block[i] = (byte)(i * 3);

        var range = new ChecksumRange(0, 62, 62, 2);

        // Apply checksum
        crc.Apply(block, range);
        Assert.IsTrue(crc.Verify(block, range));

        // Tamper with one byte
        block[10] ^= 0x01;
        Assert.IsFalse(crc.Verify(block, range));

        // Re-apply and verify restoration
        crc.Apply(block, range);
        Assert.IsTrue(crc.Verify(block, range));
    }

    [TestMethod]
    public void XorChecksum_ComputeAndVerify_MatchesExpected()
    {
        var xor = new XorChecksum();
        byte[] block = new byte[] { 0x10, 0x20, 0x05, 0x00 }; // 0x10 ^ 0x20 ^ 0x05 = 0x35
        var range = new ChecksumRange(0, 3, 3, 1);

        xor.Apply(block, range);

        Assert.AreEqual(0x35, block[3]);
        Assert.IsTrue(xor.Verify(block, range));

        // Tamper
        block[0] = 0x11;
        Assert.IsFalse(xor.Verify(block, range));
    }

    [TestMethod]
    public void Sum8Checksum_ComputeAndVerify_MatchesExpected()
    {
        var sum8 = new Sum8Checksum();
        byte[] block = new byte[] { 0x50, 0x60, 0x70, 0x00 }; // 0x50 + 0x60 + 0x70 = 0x120 -> mod 256 = 0x20
        var range = new ChecksumRange(0, 3, 3, 1);

        sum8.Apply(block, range);

        Assert.AreEqual(0x20, block[3]);
        Assert.IsTrue(sum8.Verify(block, range));
    }

    [TestMethod]
    public void ChecksumRegistry_UnregisteredEcu_BlocksWriting()
    {
        var registry = ChecksumRegistry.Default;

        // ECU 0x99, DID 0x9999 has no registered checksum
        var block = new CodingBlock(0x99, 0x9999, new byte[64]);

        Assert.IsFalse(registry.HasChecksum(0x99, 0x9999));
        Assert.IsFalse(registry.Verify(block));
        Assert.ThrowsException<InvalidOperationException>(() => registry.Apply(block));
    }

    [TestMethod]
    public void ChecksumRegistry_HuNbt3000_VerifiesAndAppliesSuccessfully()
    {
        var registry = ChecksumRegistry.Default;
        byte[] raw = new byte[64];
        for (int i = 0; i < 62; i++) raw[i] = 0xAA;

        var block = new CodingBlock(0x63, 0x3000, raw);

        Assert.IsTrue(registry.HasChecksum(0x63, 0x3000));
        registry.Apply(block);
        Assert.IsTrue(registry.Verify(block));
    }
}
