using System.Buffers.Binary;

namespace BmwCoder.Core.Diagnostics;

public static class VehicleTopology
{
    public const ushort GatewayEcuListDid = 0x3F15;

    public static IReadOnlyList<byte> DecodeEcuList(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            throw new FormatException("Gateway ECU list has no count header.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (count == 0 || count > 256 || data.Length != count + 2)
            throw new FormatException($"Gateway ECU list count {count} does not match {data.Length - 2} address bytes.");
        var addresses = data[2..].ToArray();
        if (addresses.Distinct().Count() != count)
            throw new FormatException("Gateway ECU list contains duplicate addresses.");
        return addresses;
    }
}
