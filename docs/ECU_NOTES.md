# BMW F-Series ECU Diagnostic & Coding Notes

This document provides technical reference notes regarding Electronic Control Units (ECUs) found on BMW F-Series chassis (F10, F20, F30, F80, F15, etc.) accessed via standard ENET / HSFZ protocol.

---

## 1. Diagnostic Address & ECU Catalog

| Address | Module | Name | Bus | Suggested Coding DID | Checksum Algorithm | Writable in MVP |
|---|---|---|---|---|---|---|
| `0x10` | **ZGW** | Central Gateway Module | Ethernet / CAN | `0xF190` (VIN) | N/A | **No** (Gateway) |
| `0x40` | **CAS / FEM** | Car Access System / Front Electronic Module | K-CAN2 | `0x3000` | XOR-8 | **Yes** |
| `0x60` | **KOMBI** | Instrument Cluster | K-CAN2 / MOST | `0x3000` | CRC16-CCITT (`0x0000`) | **Yes** |
| `0x63` | **HU_NBT** | Head Unit (NBT / EntryNav) | K-CAN2 / Ethernet | `0x3000` | CRC16-CCITT (`0xFFFF`) | **Yes** |
| `0x64` | **HU_CIC** | Head Unit (CIC - Legacy F-Series) | MOST | `0x3000` | CRC16-CCITT (`0xFFFF`) | **Yes** |
| `0x12` | **DME / DDE** | Engine Control Module | PT-CAN | — | Proprietary | **NO (Locked)** |
| `0x20` | **REM / JBBF**| Rear Electronic Module / Junction Box | K-CAN | `0x3000` | XOR-8 | **Yes** |
| `0x30` | **ACSM** | Airbag / Crash Safety Module | K-CAN2 | — | Proprietary | **NO (Locked)** |

---

## 2. Safety Guidelines for Coding Operations

### Safe Modules for MVP Testing
Testing and custom raw coding byte modifications should **strictly** be conducted on non-critical comfort modules:
- **`HU_NBT` (`0x63`)**: Head unit functions (e.g. startup animation, warning messages, volume limits). If an invalid block is written, the head unit may reboot to default or show an error screen without impacting vehicle drivability.
- **`KOMBI` (`0x60`)**: Instrument cluster display options (digital speed display, trip computer formats).

### Strictly Prohibited Modules (MVP Non-Goals)
The application hard-locks writing to:
- **`DME / DDE` (`0x12`)**: Engine calibration, emissions, immobilizer synchronization.
- **`ACSM` (`0x30`)**: Supplementary Restraint System (airbags, seatbelt pretensioners). Modifying raw blocks can disable passive safety systems or trigger airbag deployment fault codes.

---

## 3. Checksum Specifications

### CRC16-CCITT
- **Polynomial**: `0x1021` ($x^{16} + x^{12} + x^5 + 1$)
- **Endianness**: Big-endian (High byte first)
- **HU_NBT Variant**: Initial value `0xFFFF`, calculated over bytes `0..61`, stored at bytes `62..63`.
- **KOMBI Variant**: Initial value `0x0000`, calculated over bytes `0..61`, stored at bytes `62..63`.

### XOR Checksum
- **Algorithm**: Byte-by-byte cumulative XOR over target parameter span.
- **CAS / FEM**: Calculated over bytes `0..30`, stored at byte `31`.

---

## 4. Post-Write ECU Sequences

1. **Routine Control (`0x31 0x01 0x0200`)**:
   Certain ECUs require invoking RoutineControl `0x0200` to commit non-volatile memory or perform flash buffer flush prior to reboot.
2. **ECU Reset (`0x11 0x01`)**:
   After writing coding parameters, a hard reset command (`0x11 0x01`) instructs the module to re-read NVRAM into running RAM and reinitialize bus communications.
3. **Post-Write Re-Read Verification**:
   The application always opens an extended session and performs `0x22 <DID>` to confirm that data stored in the ECU matches the transmitted block byte-for-byte. If any byte differs, the write is declared failed and a warning is raised.
