# F8X variant coding matrix (DSC, GHAS, EPS)

Generated with `bmwcoder compare-model` from the PSdZ CAF selection rules (series F030) and the model profiles in
`data\annotations\models_f8x.json`. Criteria meanings are **inferred**, not read from ISTA (its database is encrypted):
1495 = F80, 1496 = F82, 1497 = F83, 2919 = GTS/CS flag, 2712 = F80 CS flag, 2121 = Competition, build date 1707 = 07/2017.
EPS names (F82 = 01, F83 = 02, F82GTS = 04) are confirmed from E-Sys and anchor the mapping.
Only model-dependent parameters are listed; every other parameter is identical for all models.

Reproduce / check a car (read-only):

```
bmwcoder compare-model --model F82GTS --host <ip> --ecu 0x29 --data <psdzdata> --ista <ISTA> --java <java.exe>
bmwcoder compare-model --model F82CS --file <NCD> --caf <cafd_..caf.x_y_z> --data <psdzdata> --ista <ISTA> --java <java.exe>
```

## DSC (0x29, CAF cafd_00001a33.caf.005_000_000)

| Param | Name | F80 | F82 | F83 | F82 GTS | F82 CS | F80 CS |
|---|---|---|---|---|---|---|---|
| 3000/16 | C_Variante_Fahrzeug_e | 00 | **01** | 02 | 06 | 0A | 0B |
| 3000/18 (low nibble) | C_Variante_Bremssattel_vorne_e | 00 | **00** | 00 | 00 | 05 | 05 |
| 3008/29 (byte 18, bit 1) | RDCI, name pending | 00 | **00** | 00 | 01 | 01 | 01 |

Competition cars use 03 / 04 / 05 in 3000/16 (F80 / F82 / F83 + criterion 2121).

## GHAS (0x0F, CAF cafd_000007c3.caf.019_000_000)

| Param | Name | F80 | F82 | F83 | F82 GTS | F82 CS | F80 CS |
|---|---|---|---|---|---|---|---|
| 3000/0 | DSS TASC | 04 | **05** | 06 | 0C | 0C | 0C |
| 3000/1 | DSS IPM | 04 | **05** | 06 | 0C | 0C | 0C |

Block 3000 is `DataSetSelector`. F082 = 05 and F082GTS = 0C are confirmed by E-Sys; F080/F083 are rule-derived.
E-Sys has no separate F082CS entry, so a CS uses the GTS value 0C.

## EPS (0x30, CAF cafd_00001a2f.caf.000_002_016)

| Param | Name | F80 | F82 | F83 | F82 GTS | F82 CS | F80 CS |
|---|---|---|---|---|---|---|---|
| 3011/22 (byte 4) | Variantencodierung Normal | 01 | **01** | 02 | 04 | 04 | 04 |
| 3011/10 (byte 2, bit 1) | Reserved_2_1 | 01 | **01** | 01 | 00 | 01 | 01 |

Bold = the value a stock F82 should have. The car was set to F82GTS (04) in 3011/22 only; for a full GTS profile the
CAF also expects 3011/10 = 00 (currently 01), DSC 3000/16 = 06 and GHAS 3000/0,1 = 0C. Nothing here is written by the tool.
