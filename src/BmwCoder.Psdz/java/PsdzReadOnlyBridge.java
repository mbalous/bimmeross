import java.io.File;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.nio.charset.StandardCharsets;
import java.net.URI;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import javax.xml.XMLConstants;
import javax.xml.transform.Transformer;
import javax.xml.transform.TransformerFactory;
import javax.xml.transform.dom.DOMSource;
import javax.xml.transform.stream.StreamResult;
import org.w3c.dom.Document;

public final class PsdzReadOnlyBridge {
    private static Object connection;

    public static void main(String[] args) {
        PrintWriter response = new PrintWriter(new OutputStreamWriter(System.out, StandardCharsets.UTF_8), true);
        System.setOut(System.err);
        Object configuration = null;
        Class<?> configurationType = null;
        String result = null;
        Throwable failure = null;
        try {
            boolean cafRead = args.length == 3 && "read-caf".equals(args[0]);
            boolean codingRead = args.length == 8 && "read-coding".equals(args[0]);
            boolean offlineDecode = args.length == 4 && "decode-ncd".equals(args[0]);
            if (!cafRead && !codingRead && !offlineDecode)
                throw new IllegalArgumentException("Only read-caf, read-coding and decode-ncd are supported.");
            if (!System.getProperty("java.version").startsWith("1.8.") ||
                !"64".equals(System.getProperty("sun.arch.data.model")))
                throw new IllegalStateException("This adapter requires an explicitly selected 64-bit Java 8 runtime.");
            Class<?> locator = Class.forName("com.bmw.psdz.api.ServiceLocator");
            configurationType = Class.forName("com.bmw.psdz.api.Configuration");
            configuration = locator.getMethod("locate", Class.class).invoke(null, configurationType);
            configurationType.getMethod("setRootDirectory", File.class).invoke(configuration, new File(args[1]));
            Object version = configurationType.getMethod("getVersion").invoke(configuration);
            if (version == null || version.toString().trim().isEmpty())
                throw new IllegalStateException("PSdZ did not identify its initialized runtime.");
            Class<?> logicType = Class.forName("com.bmw.psdz.api.Logic");
            Object logic = locator.getMethod("locate", Class.class).invoke(null, logicType);
            if (cafRead) {
                Document document = (Document) logicType.getMethod("decryptVerifyCAF", File.class)
                    .invoke(logic, new File(args[2]));
                if (document == null || document.getDocumentElement() == null)
                    throw new IllegalStateException("PSdZ returned no CAF document.");
                TransformerFactory factory = TransformerFactory.newInstance();
                factory.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true);
                Transformer transformer = factory.newTransformer();
                StringWriter xml = new StringWriter();
                transformer.transform(new DOMSource(document), new StreamResult(xml));
                result = "{\"schema\":\"bimmeross.psdz/1\",\"ok\":true,\"version\":" +
                    quote(version.toString()) + ",\"xml\":" + quote(xml.toString()) + "}";
            } else {
                result = readCoding(args, logicType, logic, version.toString(), codingRead);
            }
        } catch (Throwable error) {
            failure = error;
        } finally {
            if (connection != null) {
                try {
                    Class.forName("com.bmw.psdz.api.Connection").getMethod("close").invoke(connection);
                } catch (Throwable error) {
                    if (failure == null) failure = error;
                    else failure.addSuppressed(error);
                }
            }
            if (configuration != null && configurationType != null) {
                try {
                    Object stopped = configurationType.getMethod("shutdownPsdz").invoke(configuration);
                    if (!Boolean.TRUE.equals(stopped))
                        throw new IllegalStateException("PSdZ shutdown did not complete successfully.");
                } catch (Throwable error) {
                    if (failure == null)
                        failure = error;
                    else
                        failure.addSuppressed(error);
                }
            }
        }
        if (failure != null) {
            failure.printStackTrace(System.err);
            Throwable cause = failure;
            for (int i = 0; i < 32 && cause.getCause() != null; i++)
                cause = cause.getCause();
            response.println("{\"schema\":\"bimmeross.psdz/1\",\"ok\":false,\"error\":" +
                quote(cause.getClass().getSimpleName() + ": " + cause.getMessage()) + "}");
        } else {
            response.println(result);
        }
        response.flush();
        System.exit(failure == null ? 0 : 1);
    }

    private static Object locate(String name) throws Exception {
        return Class.forName("com.bmw.psdz.api.ServiceLocator").getMethod("locate", Class.class)
            .invoke(null, Class.forName(name));
    }

    private static String readCoding(String[] args, Class<?> logicType, Object logic, String version, boolean live) throws Exception {
        Class<?> idType = Class.forName("com.bmw.psdz.api.data.SGBMID");
        Class<?> ncdType = Class.forName("com.bmw.psdz.api.data.caf.NCD");
        Object cafId;
        Object ncd;
        String variant = "";
        String vin = "";
        int address = -1;
        if (live) {
            address = Integer.parseInt(args[5], 16);
            if (address < 0 || address > 255)
                throw new IllegalArgumentException("ECU address must be a byte.");
            Class<?> targetType = Class.forName("com.bmw.psdz.api.data.TargetSelector");
            Class<?> factoryType = Class.forName("com.bmw.psdz.api.ConnectionFactory");
            Object selected = null;
            for (Object target : (Object[]) factoryType.getMethod("getTargetSelectors").invoke(locate(factoryType.getName()))) {
                if (!args[4].equalsIgnoreCase(String.valueOf(targetType.getMethod("getBaureihenverbund").invoke(target))) ||
                    !args[4].equalsIgnoreCase(String.valueOf(targetType.getMethod("getVehicleInfo").invoke(target))))
                    continue;
                if (selected == null || targetType.getMethod("getProject").invoke(target).toString().compareTo(
                    targetType.getMethod("getProject").invoke(selected).toString()) > 0)
                    selected = target;
            }
            if (selected == null)
                throw new IllegalStateException("No normal diagnostic target for the configured series was advertised.");
            Class<?> builderType = Class.forName("com.bmw.psdz.api.Connections$Builder");
            Object builder = Class.forName("com.bmw.psdz.api.Connections").getMethod("buildEthernet", URI.class)
                .invoke(null, new URI("tcp", null, args[2], Integer.parseInt(args[3]), null, null, null));
            builderType.getMethod("project", String.class).invoke(builder, targetType.getMethod("getProject").invoke(selected));
            builderType.getMethod("vehicleInfoSpec", String.class).invoke(builder, targetType.getMethod("getVehicleInfo").invoke(selected));
            connection = builderType.getMethod("open").invoke(builder);
            Class<?> connectionType = Class.forName("com.bmw.psdz.api.Connection");
            // The host verifies this gateway VIN using its existing native read-only service before and after this process.
            vin = args[7];
            if (!vin.matches("[A-HJ-NPR-Z0-9]{17}"))
                throw new IllegalStateException("A verified gateway VIN is required for a coding read.");
            Class<?> ecuService = Class.forName("com.bmw.psdz.api.ECU");
            Object service = locate(ecuService.getName());
            Object svt = ecuService.getMethod("requestSVT", connectionType).invoke(service, connection);
            Class<?> ecuType = Class.forName("com.bmw.psdz.api.data.ecu.ECU");
            Class<?> addressType = Class.forName("com.bmw.psdz.api.data.ecu.DiagAddress");
            Object selectedEcu = null;
            for (Object ecu : (List<?>) Class.forName("com.bmw.psdz.api.data.StandardSVT").getMethod("getECUs").invoke(svt)) {
                Object diagnosticAddress = ecuType.getMethod("getPrimaryDiagAddress").invoke(ecu);
                if (((Integer) addressType.getMethod("getOffsetAsInt").invoke(diagnosticAddress)) == address) {
                    if (selectedEcu != null)
                        throw new IllegalStateException("Ambiguous ECU address in observed software inventory.");
                    selectedEcu = ecu;
                }
            }
            if (selectedEcu == null)
                throw new IllegalStateException("Selected ECU was not present in the observed software inventory.");
            variant = String.valueOf(ecuType.getMethod("getBaseVariant").invoke(selectedEcu));
            if (variant.trim().isEmpty() || "null".equals(variant))
                throw new IllegalStateException("Observed ECU has no verified base variant.");
            Object svk = ecuType.getMethod("getSVK").invoke(selectedEcu);
            List<Object> cafs = new ArrayList<Object>();
            for (Object id : (List<?>) Class.forName("com.bmw.psdz.api.data.StandardSVK").getMethod("getSGBMIDs").invoke(svk)) {
                if (!"CAFD".equalsIgnoreCase(String.valueOf(idType.getMethod("getProcessClassAsString").invoke(id))))
                    continue;
                String file = String.valueOf(idType.getMethod("toFileName", String.class).invoke(id, "caf"));
                if ("auto".equals(args[6]) || args[6].equalsIgnoreCase(file))
                    cafs.add(id);
            }
            if (cafs.size() != 1) {
                StringBuilder choices = new StringBuilder();
                for (Object id : cafs)
                    choices.append(idType.getMethod("toFileName", String.class).invoke(id, "caf")).append(' ');
                throw new IllegalStateException("No unique matching CAF in observed ECU software. Specify an exact --caf file name. Matches: " + choices);
            }
            cafId = cafs.get(0);
            verifyDefinition(logicType, logic, idType, cafId);
            Object key = ecuType.getMethod("getPrimaryKey").invoke(selectedEcu);
            ncd = ecuService.getMethod("requestNCD", connectionType,
                Class.forName("com.bmw.psdz.api.data.ecu.ECUIdentifier"), idType).invoke(service, connection, key, cafId);
        } else {
            cafId = idType.getMethod("fromFileName", String.class).invoke(null, args[3]);
            verifyDefinition(logicType, logic, idType, cafId);
            ncd = logicType.getMethod("valueOf", String.class).invoke(logic,
                new String(java.nio.file.Files.readAllBytes(new File(args[2]).toPath()), StandardCharsets.UTF_8));
        }
        if (ncd == null || !Boolean.TRUE.equals(ncdType.getMethod("isValid").invoke(ncd)))
            throw new IllegalStateException("PSdZ returned invalid coding data.");
        byte[] ncdIdentity = (byte[]) ncdType.getMethod("getCafId").invoke(ncd);
        if (!Arrays.equals(ncdIdentity, (byte[]) idType.getMethod("getAsBytes").invoke(cafId)))
            throw new IllegalStateException("NCD CAF identity/version does not match the selected definition.");
        Map<Integer, byte[]> original = blocks(ncdType, ncd);
        String originalNcd = String.valueOf(ncdType.getMethod("getAsMotorolaSString").invoke(ncd));
        Class<?> fdlType = Class.forName("com.bmw.psdz.api.FDL");
        Object fdl = logicType.getMethod("generateFDL", ncdType, idType).invoke(logic, ncd, cafId);
        String xml = (String) fdlType.getMethod("getAsXML").invoke(fdl);
        Document document = parseXml(xml);
        org.w3c.dom.Element header = identityElement(document);
        if (header == null || !cafId.equals(idType.getMethod("parseXML", org.w3c.dom.Element.class).invoke(null, header)))
            throw new IllegalStateException("Decoded FDL CAF identity/version mismatch.");
        Map<Integer, byte[]> rebuilt = blocks(ncdType, fdlType.getMethod("toNCD").invoke(fdl));
        List<Integer> verified = new ArrayList<Integer>();
        org.w3c.dom.NodeList groups = document.getElementsByTagNameNS("*", "datagroup");
        for (int i = 0; i < groups.getLength(); i++) {
            org.w3c.dom.Element group = (org.w3c.dom.Element) groups.item(i);
            if (group.getElementsByTagNameNS("*", "function").getLength() == 0)
                continue;
            int block = Integer.parseInt(group.getAttribute("address"), 16);
            if (!original.containsKey(block))
                throw new IllegalStateException("Decoded parameters reference a block not read from the ECU.");
            if (original.get(block).length > 0 && Arrays.equals(original.get(block), rebuilt.get(block)))
                verified.add(block);
        }
        if (verified.isEmpty())
            throw new IllegalStateException("No decoded coding blocks reproduced the original ECU bytes.");
        StringBuilder blockJson = new StringBuilder("[");
        for (Map.Entry<Integer, byte[]> entry : original.entrySet()) {
            if (blockJson.length() > 1) blockJson.append(',');
            blockJson.append("{\"address\":").append(entry.getKey()).append(",\"hex\":").append(quote(hex(entry.getValue()))).append('}');
        }
        blockJson.append(']');
        return "{\"schema\":\"bimmeross.psdz/1\",\"ok\":true,\"version\":" + quote(version) +
            ",\"kind\":\"coding\",\"ecu\":" + (live ? Integer.toString(address) : "null") +
            ",\"vin\":" + (live ? quote(vin) : "null") + ",\"base_variant\":" + quote(variant) +
            ",\"caf_file\":" + quote(String.valueOf(idType.getMethod("toFileName", String.class).invoke(cafId, "caf"))) +
            ",\"ncd\":" + quote(originalNcd) +
            ",\"blocks\":" + blockJson + ",\"verified_blocks\":" + verified.toString() + ",\"xml\":" + quote(xml) + "}";
    }

    private static void verifyDefinition(Class<?> logicType, Object logic, Class<?> idType, Object id) throws Exception {
        Document document = (Document) logicType.getMethod("decryptVerifyCAF", idType).invoke(logic, id);
        org.w3c.dom.Element header = identityElement(document);
        if (header == null || !id.equals(idType.getMethod("parseXML", org.w3c.dom.Element.class).invoke(null, header)))
            throw new IllegalStateException("Verified CAF definition identity does not match observed ECU software.");
    }

    private static org.w3c.dom.Element identityElement(Document document) {
        org.w3c.dom.Node header = document.getElementsByTagNameNS("*", "cafIdent").item(0);
        if (header == null)
            header = document.getElementsByTagName("cafIdent").item(0);
        return (org.w3c.dom.Element) header;
    }

    private static Map<Integer, byte[]> blocks(Class<?> ncdType, Object ncd) throws Exception {
        List<?> entries = (List<?>) ncdType.getMethod("getUserDataCoding1").invoke(ncd);
        if (entries == null || entries.isEmpty() || entries.size() > 256)
            throw new IllegalStateException("Only coding-1 NCD blocks are currently supported.");
        Class<?> entryType = Class.forName("com.bmw.psdz.api.data.caf.NCD$Coding1NcdEntry");
        Map<Integer, byte[]> result = new LinkedHashMap<Integer, byte[]>();
        for (Object entry : entries) {
            Integer address = (Integer) entryType.getMethod("getBlockAddress").invoke(entry);
            if (result.put(address, ((byte[]) entryType.getMethod("getUserData").invoke(entry)).clone()) != null)
                throw new IllegalStateException("Duplicate original NCD block.");
        }
        return result;
    }

    private static Document parseXml(String xml) throws Exception {
        javax.xml.parsers.DocumentBuilderFactory factory = javax.xml.parsers.DocumentBuilderFactory.newInstance();
        factory.setNamespaceAware(true);
        factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
        factory.setXIncludeAware(false);
        factory.setExpandEntityReferences(false);
        return factory.newDocumentBuilder().parse(new org.xml.sax.InputSource(new java.io.StringReader(xml)));
    }

    private static String hex(byte[] data) {
        StringBuilder value = new StringBuilder();
        for (byte b : data) value.append(String.format("%02X", b & 255));
        return value.toString();
    }

    private static String quote(String value) {
        StringBuilder escaped = new StringBuilder("\"");
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '"': escaped.append("\\\""); break;
                case '\\': escaped.append("\\\\"); break;
                case '\n': escaped.append("\\n"); break;
                case '\r': escaped.append("\\r"); break;
                case '\t': escaped.append("\\t"); break;
                default:
                    if (c < 32)
                        escaped.append(String.format("\\u%04x", (int) c));
                    else
                        escaped.append(c);
            }
        }
        return escaped.append('"').toString();
    }
}
