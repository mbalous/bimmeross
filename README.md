# BimmerOSS - Terminal.Gui F-series workspace

This project is source-available under the PolyForm Noncommercial License 1.0.0,
not an OSI-approved open-source license. Commercial use is not generally permitted;
see [LICENSE](LICENSE) for the full terms and the noncommercial organization provision.

The new application is a keyboard-first **Terminal.Gui TUI** in `src\BmwCoder.Tui`.
It references and extends the existing `BmwCoder.Core`, `Coding`, `Transport`, and `Uds`
libraries in this solution; there is no copied backend or separate repository.

**This is a read-only/offline preview, not an E-Sys replacement yet.** Offline CAF
definitions can be read through a configured local ISTA/Java 8 installation.
Coding-1 NCD reads and parameter decoding are available with exact matching CAFs.
Vehicle FA acquisition, FDL editing/writes, FA writes, VO coding, flashing, resets,
and service actuations are unavailable. Installed E-Sys jars alone do not establish
a supported coding backend. The TUI does not expose legacy raw-write APIs.

## Coding annotations (parameter names)

PSdZ CAFs ship with blanked parameter names. The CLI `read-coding`/`decode` output adds
`label`/`meaning` for parameters listed in `data\annotations\<cafd id>.json` (copied to
`annotations\` next to the executable; override with `BIMMEROSS_ANNOTATIONS`). Files are
display-only and keyed by CAF id, block and byte. Currently included: ASD (`cafd_00000f9b`,
verified against version 001_046_001 only).

### Model comparison (F8X variants)

`compare-model --model <F80|F82|F83|F82GTS|F82CS|F80CS>` evaluates the CAF selection rules
(criteria mapped in `data\annotations\models_f8x.json`) and lists model-dependent parameters with
actual vs expected values and the value for every model. Use it live (`--host --ecu`) or offline
(`--file <NCD> --caf <CAF>`); it is read-only and mismatches are informational. The criteria
meanings are inferred, see `docs\F8X_VARIANT_MATRIX.md` for the current DSC/GHAS/EPS matrix.

## Run the TUI

Build the `.slnx` solution with .NET SDK 9 or newer (SDK 10 is verified). Projects
target .NET 8; the .NET 8 runtime is required for framework-dependent executables.
Terminal.Gui is pinned to 1.19.0 to retain the existing .NET 8 target.
A JDK with `javac --release 8` support (JDK 17 or newer recommended) must be on PATH
when building the TUI/CLI to compile our own helper. Published packages include
that helper; using it requires an explicitly configured **64-bit Java 8** runtime,
not a compiler. Ordinary tests do not require Java 8, BMW files, or a vehicle.

```pwsh
# Safe default: synthetic demo, no network connections
dotnet run --project src\BmwCoder.Tui -- --demo

# Explicit live mode: connects only when you choose Connect
dotnet run --project src\BmwCoder.Tui -- --live

# Optional separate settings file
dotnet run --project src\BmwCoder.Tui -- --demo --settings C:\local\bimmeross-settings.json
```

Use an interactive terminal at least 80 columns by 24 rows. Redirected input/output
is rejected; use the existing `tools\BmwCoder.Cli` JSON tool for automation.
`--help` works without an interactive terminal.

| Key / menu | Action |
|---|---|
| F2 / Vehicle > Connect | Connect to the demo or configured host and verify VIN |
| F3 / Vehicle > Discover | List ECU candidates; fallback catalog names are not verified variants |
| Vehicle > Find gateway | Select an ENET IPv4 interface; send one bounded UDP identification broadcast; select the sole reply without connecting |
| F4 | Cancel the current operation; cancellation disconnects the vehicle |
| Ctrl+Q | Exit and release terminal/vehicle resources |
| Tab / Shift+Tab, Alt+menu key | Move focus / open menus |
| Search | Filter ECUs, FA/CAF fields, or decoded parameter identifiers/names/values |
| Vehicle > Read DID / Read faults | Single-DID reads or DTC status reads; no session changes, resets or clears |
| FA/VO | Import XML, browse/edit selected fields offline, review diff, export to a new file |
| Coding | Read coding/decode parameters, open an NCD offline, export the original NCD, browse CAF definitions or inspect raw files |
| Backups | Inspect an existing backup directory and verify snapshot sizes/SHA-256 |
| Settings | Set connection/tool paths, inspect local installations, explicitly save settings |

Try F2, F3, then **FA/VO > Load synthetic FA > Show fields** in demo mode.
Select a field and press Enter to edit it, then review Diff and Export XML.
The demo VIN, ECUs and FA are synthetic and do not model a supported coding variant.

FA XML handling preserves unknown fields and preserves exact original bytes when
unchanged. Edits are **XML syntax-checked only**, not validated against vehicle order
rules or an I-step. Exports never overwrite an existing file. Importing a coding
file using **Import file (raw preview)** does not decode its parameters or authorize
any vehicle operation. Use **Open NCD / decode** with its exact CAF for decoding.

Settings use the existing `%APPDATA%\BmwCoder\settings.json` unless overridden.
The new strict settings APIs report malformed files/save errors rather than hiding
them; unknown settings fields are preserved. No settings are saved automatically.

Local-install inspection accepts PSdZData, E-Sys and ISTA directories. It inventories
series/CAF files and reads local jar version metadata, without executing E-Sys,
copying BMW files, or claiming compatibility. Inspection does not automatically load
ODX definitions into a diagnostic session. Leave paths empty to use the demo alone.

### Read-only CAF definitions and gateway discovery

In **Settings > Local tool paths**, configure the ISTA root (for example
`C:\EC-APPS\ISTA`) and the explicit Java 8 `java.exe` path. Then use
**Coding > Read CAF definition** to select a trusted local `*.caf.*` file and
**Show CAF fields** to browse/search its XML fields. These are definition fields,
not decoded NCD values or validated writable FDL parameters. No ECU connection
is opened for this operation.

The adapter uses ISTA's own `PSdZ\binx64` jars/native libraries and
`PSdZ\data\psdzdata\extLibs` providers together. It does not mix E-Sys jars or
copy proprietary components. Offline CAF import and clean shutdown have been
verified with ISTA PSdZ **5.14.03**, CodeSysLib **001.014.000**, and 64-bit Java
**1.8.0_511**, reading an existing CAF from the separate configured dataset.
Other versions are not qualified. Vendor warnings are visible in the CAF
summary; a successful file import does not qualify programming or vehicle coding.
Only trusted local CAF files are supported.

Each read uses a separate owned JVM with bounded heap/output and a 90-second
maximum lifetime. The TUI's shorter operation timeout also applies. Cancellation
terminates/reaps that JVM, and temporary vendor logs are removed. The protocol
rejects malformed responses, missing version/XML, failed imports and failed
shutdowns. Only CAF reads, coding reads and offline NCD decoding are exposed;
no write, reset or programming method is exposed.

The JSON CLI shares these services; both commands below run **before** TCP setup:

```pwsh
dotnet run --project tools\BmwCoder.Cli -- gateways --local-ip 169.254.1.1 --timeout 2000
dotnet run --project tools\BmwCoder.Cli -- caf --file C:\local\definition.caf.001_000_000 --ista C:\EC-APPS\ISTA --java "C:\Program Files\Java\jre1.8.0_511\bin\java.exe" --timeout 90000
```

Use the IPv4 address assigned to your own ENET adapter, not the vehicle address.
Gateway discovery does not connect automatically or persist a host selection.
The CAF command returns a summary rather than dumping proprietary XML.

### Read coding -> decode parameters

Configure **PSdZData**, **ISTA** and **64-bit Java 8** under Settings. In `--live`
mode, connect (F2), discover (F3), select an ECU, then choose
**Coding > Read coding / decode**. Leave the CAF prompt blank for an ECU with one
observed CAF, or specify the exact filename for a multi-CAF ECU. The reader obtains
the ECU's real base variant/software inventory; it never substitutes a catalog
variant or a similarly numbered CAF.

The native connection is released while PSdZ reads coding. Gateway VIN is verified
before and after the serialized read using the existing native read-only service.
Every returned coding block is independently reread through native UDS; any byte
mismatch or failed verification rejects the result.
The SDK verifies the exact CAF, reads NCD, and generates FDL. Parameters are exposed
only for blocks whose SDK reconstruction matches the original bytes. Other blocks
remain opaque and are preserved, not regenerated. Press F2 to reconnect afterward.

Use **Show parameters** to browse/search current values and available enum meanings.
Current values come from the SDK-selected FDL interpretation, never CAF defaults.
Missing current values are explicitly unavailable. Missing/trimmed names stay
unknown with stable block/function identifiers: this does **not** restore launcher
mapping databases or invent friendly feature labels. No parameter editor or
vehicle-changing operation is enabled.

Live read/decode has been qualified locally for HU_NBT with
`cafd_00000ded.caf.003_015_016`: **1,049 current values**
and one preserved opaque block (`3007`). Only one function name is present
in that CAF; the remaining names are trimmed. This is not a compatibility claim for
every ECU, CAF version, secure-coding format or F-series vehicle.

The runtime uses a temporary Windows junction-based data view: ISTA's own matching
metadata/providers plus CAF files from the configured PSdZData directory. The older
dataset's incompatible KIS database is not loaded. No BMW files are copied or
bundled; links are explicitly removed before temporary-directory cleanup.

**Export read NCD** saves the original SDK-serialized Motorola S-record NCD to a
new file, including opaque data. **Open NCD / decode** decodes a trusted saved
NCD offline against its exact CAF, without opening vehicle connections.

```pwsh
.\artifacts\cli\bmwcoder.exe read-coding --host 169.254.64.104 --ecu 63 --data C:\Data\psdzdata --ista C:\EC-APPS\ISTA --java "C:\Program Files\Java\jre1.8.0_511\bin\java.exe" --out C:\local\read.ncd
.\artifacts\cli\bmwcoder.exe decode --file C:\local\read.ncd --caf cafd_00000ded.caf.003_015_016 --data C:\Data\psdzdata --ista C:\EC-APPS\ISTA --java "C:\Program Files\Java\jre1.8.0_511\bin\java.exe"
```

Replace the gateway address and target ECU with your explicitly selected vehicle.
`--caf` also selects an exact observed CAF for live multi-CAF reads. JSON includes
decoded/current-value counts, explicit unavailable fields, opaque block addresses,
parameter details and the original NCD hash. `--out` is optional and never overwrites.

### Validation and packaging

```pwsh
dotnet test BmwCoder.slnx -c Release
dotnet publish src\BmwCoder.Tui\BmwCoder.Tui.csproj -c Release -r win-x64 --self-contained false -o artifacts\tui
dotnet publish tools\BmwCoder.Cli\BmwCoder.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts\cli
```

The TUI tests exercise actual Terminal.Gui views with its fake console driver,
keyboard handlers, resizing, demo workflows, read-only protocol boundaries, XML
round trips, backup corruption, and operation cancellation. They require no BMW
installations or connected vehicle. NuGet access is needed for initial restore.

Native VIN/ECU reads and ENET discovery have been checked on the user's connected
vehicle without writes, as has the exact HU_NBT coding-read/decode profile above.
Broader vehicle compatibility and real-terminal behavior
still require qualification. Do not assume a backup, checksum, ECU address, or module
name establishes that a write would be safe. A validated PSdZ integration and
target-specific bench/vehicle evidence are required before enabling FDL writes.

The existing JSON CLI and WinForms source remain available. The new TUI release
package excludes the WinForms app and all proprietary BMW components.

---

## Legacy WinForms prototype

The documentation below describes the older raw-block prototype, **not the TUI**.
Its ECU tables and write/checksum assumptions are not a verified compatibility list
and must not be treated as instructions for E-Sys-style FDL coding.

A fast, lightweight, and safety-first Windows desktop application (.NET 8 WinForms) for reading, modifying, verifying, and writing raw ECU coding blocks on BMW F-Series vehicles via standard ENET cables.

---

## 1. What It Does & What It Doesn't

### What It Does (MVP Scope)
- Connects directly to BMW F-Series vehicles over standard Ethernet OBD (ENET cable) via the native **HSFZ protocol** on TCP port `6801`.
- Probes and discovers reachable Electronic Control Units (ECUs) on the vehicle diagnostic bus.
- Automatically reads and displays the vehicle's unique 17-character VIN from the Central Gateway (`0x10`).
- Reads raw diagnostic coding blocks via UDS service `0x22` (ReadDataByIdentifier) for user-selected DIDs (e.g. `0x3000`).
- Provides an integrated owner-drawn hex editor with color-coded diffing against the baseline block.
- Recalculates and verifies critical ECU checksums (CRC16-CCITT, XOR-8, Sum-8).
- Implements a strict, multi-layer safe write pipeline:
  1. Read-only by default.
  2. Automatic, deterministic disk snapshot to `%APPDATA%\BmwCoder\backups` before every write.
  3. Write hard-blocked if checksum algorithm is unknown or invalid.
  4. Write hard-blocked on powertrain or safety-critical modules (DME/DDE, ACSM).
  5. Modal confirmation requiring the user to explicitly type `WRITE`.
  6. Automatic post-write re-read and byte-for-byte verification.
- Complete full-car backup and selective block-by-block restoration.

### What It Does NOT Do (Explicit Non-Goals for MVP)
- **No PSdZData / SGBD / NCD parsing**: Does not parse proprietary BMW XML/PRG/NCD template databases.
- **No named toggles**: No high-level toggles (e.g. "Enable Video in Motion"); works strictly with raw block memory.
- **No FA / VO editing**: Vehicle order editing is out of scope.
- **No flashing**: Does not flash firmware images (`*.swfl`, `*.swfk`).
- **No G-Series / I-Series / E-Series**: F-Series ENET only.
- **No wireless adapters**: Requires wired Ethernet (ENET cable).

---

## 2. Supported ECUs

| Address | Module | Name | Default DID | Checksum | Writable |
|---|---|---|---|---|---|
| `0x10` | **ZGW** | Central Gateway | `0xF190` (VIN) | N/A | No (Gateway) |
| `0x40` | **CAS / FEM** | Car Access System / Front Electronic Module | `0x3000` | XOR-8 | Yes |
| `0x60` | **KOMBI** | Instrument Cluster | `0x3000` | CRC16-CCITT (`0x0000`) | Yes |
| `0x63` | **HU_NBT** | Head Unit (NBT / EntryNav) | `0x3000` | CRC16-CCITT (`0xFFFF`) | Yes |
| `0x64` | **HU_CIC** | Head Unit (CIC - Legacy) | `0x3000` | CRC16-CCITT (`0xFFFF`) | Yes |
| `0x20` | **REM / JBBF** | Rear Electronic Module / Junction Box | `0x3000` | XOR-8 | Yes |
| `0x12` | **DME / DDE** | Engine Control Module | — | Locked | **NO (Locked)** |
| `0x30` | **ACSM** | Airbag / Crash Safety Module | — | Locked | **NO (Locked)** |

*For detailed notes, see [docs/ECU_NOTES.md](docs/ECU_NOTES.md).*

---

## 3. Hardware & Network Setup

1. **Cable**: Standard BMW ENET (OBD-II to RJ45 Ethernet) cable.
2. **Connection**: Plug the OBD-II connector into the vehicle diagnostic port (under the driver-side dashboard) and the RJ45 jack into your Windows PC.
3. **Vehicle State**: Switch ignition ON (engine off, KL15 active, or attach a battery maintainer providing > 12.5V).
4. **PC Network Adapter Configuration**:
   - By default, BMW gateways assign an APIPA / Auto-IP address in the `169.254.x.x` range (typically gateway is at `169.254.16.17` or `169.254.1.1`).
   - If Windows shows "Identifying..." or fails to obtain an IP:
     - Open Windows Network Connections (`ncpa.cpl`).
     - Right-click your Ethernet adapter &rarr; Properties &rarr; Internet Protocol Version 4 (TCP/IPv4).
     - Configure static IP:
       - **IP address**: `169.254.1.50`
       - **Subnet mask**: `255.255.0.0`
       - **Default gateway**: leave blank.

---

## 4. Console Diagnostic Harness (`HsfzPing`)

Before launching the GUI, you can verify physical connectivity using the included lightweight console utility:

```pwsh
# Usage: HsfzPing [host] [port] [ecu_address_hex]
dotnet run --project tools/HsfzPing/HsfzPing.csproj -- 169.254.16.17 6801 40
```

Expected output upon successful communication with CAS (`0x40`):
```text
=== HsfzPing Utility ===
Target Host:   169.254.16.17:6801
Target ECU:    0x40
Connecting...
Connected! Sending TesterPresent (3E 00)...
Received response from 0x40:
Payload (hex): 7E00
SUCCESS: Positive TesterPresent response (7E 00) received!
```

---

## 5. Building & Packaging

### Run Unit Tests
```pwsh
dotnet test BmwCoder.slnx -c Release
```

### Build Single-File Distributable Executable
```pwsh
dotnet publish src/BmwCoder.App/BmwCoder.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
The resulting self-contained, zero-dependency executable is located at:
`src\BmwCoder.App\bin\Release\net8.0-windows\win-x64\publish\BmwCoder.App.exe`.

---

## 6. Legacy Coding Walkthrough (Unverified)

This historical walkthrough describes an experimental write path, not a validated
or recommended procedure. Do not use it to modify a vehicle.

1. Launch `BmwCoder.App.exe`.
2. Click **Connect...**, verify IP `169.254.16.17`, and click Connect.
3. The app connects and runs **Discover ECUs**, listing online modules and reading the vehicle VIN.
4. Double-click **HU_NBT** or **KOMBI** to open the **Coding Block Editor**.
5. Select DID `0x3000` and click **Read Block**.
6. The hex editor populates with raw memory, and an uncompressed backup is saved to `%APPDATA%\BmwCoder\backups\<VIN>\<timestamp>\`.
7. Modify desired byte(s). Notice modified bytes are highlighted in orange and diff count updates in real time.
8. Click **Apply Checksum** to recalculate the block checksum. Checksum badge turns green (`VALID`).
9. Click **Write to ECU**.
10. In the confirmation dialog, review the modified byte count, check the backup acknowledgment, type **`WRITE`**, and confirm.
11. The application switches to extended session, writes the block, reboots the module if required, and verifies the write by reading back the live data.

---

## 7. Clean-Room Repository Statement

This repository contains **NO proprietary BMW intellectual property, binary databases, SGBD (.prg) scripts, PSdZData archives, or NCD templates**. All framing, UDS service definitions, and checksum algorithms are clean-room implementations constructed from open diagnostic standards (ISO 14229-1) and public protocol analysis.

---

## 8. Legal Disclaimer

> [!CAUTION]
> This software is provided under the PolyForm Noncommercial License 1.0.0.
> Modifying vehicle electronic control units carries inherent risks. Incorrect configuration can result in module faults, loss of vehicle functions, or bricked control units. The authors and contributors assume no liability for damages, loss of vehicle warranty, or regulatory violations resulting from the use of this software. Always connect an automotive-grade battery maintainer during diagnostic operations.

---

## 9. License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** — see the [LICENSE](LICENSE) file for the complete terms.
