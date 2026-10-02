# SimCity

## Pack metadata

- Platform: `SNES`
- Connector type: `SNESConnector`

## What this pack provides
This Crowd Control pack integrates **SimCity** with Crowd Control through its SNES pack implementation. Its source defines the game-state checks and effect handling.

## Requirements
- A compatible game ROM. This pack does not provide a ROM.

## Connection context
The pack source contains the platform-specific connection and game-state logic used by Crowd Control; this is explanatory context, not an additional requirement.

## Supported ROMs
Entries are derived from the primary definition. Status is shown only when declared by source metadata.

| ROM name | Checksum | Notes |
| --- | --- | --- |
| SimCity (v1.0) (U) (Headered) | MD5: ee177068d94ede4c95ec540b0db255db | <span style="color: green">Supported</span>; Patch: `SimCitySNES.bps` |
| SimCity (v1.0) (U) (Unheadered) | MD5: 23715fc7ef700b3999384d5be20f4db5 | <span style="color: green">Supported</span>; Patch: `SimCitySNES.bps` |
| SimCity - Crowd Control | MD5: d1077c8e9e8926cdb540f364925aaa9f | <span style="color: green">Supported</span> |

## Contents
- `SimCitySNES.cs` - primary pack definition and ROM metadata.
- `SimCitySNES.bps` - BPS ROM patch referenced by the pack's ROM metadata.
