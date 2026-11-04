namespace BmwCoder.Core.Workspace;

public static class PreviewCapabilities
{
    public const string CodingUnavailable =
        "Read-only CAF definitions can be imported through local ISTA/Java 8. " +
        "Coding-1 NCD reads/parameter decoding are available through the configured ISTA/Java 8 reader and exact CAF versions. " +
        "Trimmed names, unavailable current values and opaque blocks stay explicit. FDL editing/writes remain unavailable. " +
        "Raw DID writes are not FDL coding. Firmware flashing, FA writes, VO coding, resets and service actuations are disabled.";

    public const string FaReadUnavailable =
        "Vehicle FA acquisition is unavailable until a verified read backend is integrated. Import an FA XML file for offline editing.";
}
