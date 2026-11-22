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
ZGW voltage reading is not available: no gateway request and scaling have
been verified for this vehicle. A nominal ENET voltage is not a measurement.

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
# Default: live read-only mode; connects only when you choose Connect
dotnet run --project src\BmwCoder.Tui

# Synthetic demo, no network connections
dotnet run --project src\BmwCoder.Tui -- --demo

# Optional separate settings file
dotnet run --project src\BmwCoder.Tui -- --demo --settings C:\local\bimmeross-settings.json
```

Use an interactive terminal at least 80 columns by 24 rows. Redirected input/output
is rejected; use the existing `tools\BmwCoder.Cli` JSON tool for automation.
`--help` works without an interactive terminal.

On startup, **Local tools setup** checks saved paths, common local installation
locations on fixed drives, `JAVA_HOME` and `PATH`. It pre-fills only paths that pass layout checks:
ISTA PSdZ jars, providers, native-library directory and metadata; PSdZData CAF
files; Java 8 release metadata and a 64-bit Windows executable; and optional E-Sys
runtime files. The packaged helper is also checked. These checks do not run the
discovered programs or establish compatibility. Missing paths may be left blank.
**Use** applies the displayed paths for this session, **Save** persists them, and
**Skip** leaves existing settings unchanged. Reopen the dialog under Settings >
Local tool paths. Detection is bounded to common locations, so installations
elsewhere may need to be entered manually. A missing `mainseries` dataset can
still limit ODX diagnostics even when CAF files were found.

Suggested-DID snapshots use default-session reads only. The legacy GUI's
extended-session block reads are not exposed; some targets may therefore be
unavailable. The raw editor and legacy checksum functions are offline only and
do not establish that a block is safe to write.

| Key / menu | Action |
|---|---|
| F2 / Vehicle > Connect | Connect to the demo or configured host and verify VIN |
| F3 / Vehicle > Discover ECUs | List ECU candidates after connecting; fallback catalog names are not verified variants |
| F5 / Vehicle > Find gateway | Select an ENET IPv4 interface; send one bounded UDP identification broadcast; select the sole reply without connecting |
| F4 | Cancel the current operation; cancellation disconnects the vehicle |
| Ctrl+Q | Exit and release terminal/vehicle resources |
| Tab / Shift+Tab, Alt+menu key | Move focus / open menus |
| Search | Filter ECUs, FA/CAF fields, or decoded parameter identifiers/names/values |
| Vehicle > Read DID / Read faults | Single-DID reads or DTC status reads; no session changes, resets or clears |
| Vehicle > Check ECU response / Read DME report | Probe a selected ECU or run the fixed-address read-only DME report; the demo does not model a DME report |
| Vehicle > Read-only request | Enter an ECU address and a single DID read, DTC status read, or TesterPresent request |
| Vehicle > Load ODX DIDs / Configure ISTA fault texts | Load configured local data explicitly for candidate DID browsing and diagnostic decoding or fault descriptions |
| Vehicle > Show diagnostic results | Browse readings and export results as JSON or a selected payload as binary |
| FA/VO | Import XML, browse/edit selected fields offline, review diff, export to a new file |
| Coding | Read coding/decode parameters, open an NCD offline, export the original NCD, browse CAF definitions or inspect raw files |
| Coding > Open selected DID as raw draft | Edit, diff, revert, export, or apply a legacy checksum to a diagnostic payload offline; no vehicle write is available |
| Backups | Read-only suggested-DID snapshots, integrity inspection and restore review; snapshots are not full-vehicle or validated coding backups, and live restore is disabled |
| Settings | Set connection/tool paths, inspect local installations, explicitly save settings |
| Settings > Diagnostic timeout | Set a per-request timeout for the current session (not saved) |
| Help > Show logs | Browse and filter the recent operation log |

In live mode, use F5 to find a gateway (or configure a host in Settings), then F2
to connect and F3 to discover ECUs. Gateway discovery and ECU discovery are separate
actions; neither runs automatically. The activity indicator and F4 cancellation are
shown while an operation is running.

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

Configure **PSdZData**, **ISTA** and **64-bit Java 8** under Settings. In live
mode (the default, or `--live`), connect (F2), discover (F3), select an ECU, then choose
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

The JSON CLI remains available alongside the TUI. The release package contains
no proprietary BMW components.

## Hardware & Network Setup

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

## Console Diagnostic Harness (`HsfzPing`)

Before launching the TUI, you can verify physical connectivity using the included lightweight console utility:

```pwsh
# Usage: HsfzPing [host] [port] [ecu_address_hex]
dotnet run --project tools\HsfzPing\HsfzPing.csproj -- 169.254.16.17 6801 40
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

## Clean-Room Repository Statement

This repository contains **NO proprietary BMW intellectual property, binary databases, SGBD (.prg) scripts, PSdZData archives, or NCD templates**. All framing, UDS service definitions, and checksum algorithms are clean-room implementations constructed from open diagnostic standards (ISO 14229-1) and public protocol analysis.

---

## Legal Disclaimer

> [!CAUTION]
> This software is provided under the PolyForm Noncommercial License 1.0.0.
> Modifying vehicle electronic control units carries inherent risks. Incorrect configuration can result in module faults, loss of vehicle functions, or bricked control units. The authors and contributors assume no liability for damages, loss of vehicle warranty, or regulatory violations resulting from the use of this software. Always connect an automotive-grade battery maintainer during diagnostic operations.

---

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** — see the [LICENSE](LICENSE) file for the complete terms.
