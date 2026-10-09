# DeepNorthCompat

This is a Valheim mod for a personal Valheim modpack.

It currently fixes bugs and incompatibilities involving:

- ImpactfulSkills and AzuCraftyBoxes quality-aware ingredient selection and consumption.
- ImpactfulSkills bow stamina scaling and tooltip reporting.
- SeaAnimals and AirAnimals custom creature drop ranges.
- AzuEPI inventory character preview, which restarted its animation whenever a nearby creature or player changed equipment.
- Deep North profile policy that disables ValheimTune on clients after its configuration binds.
- VPO native water-wave initialization on Windows dedicated servers.
- MultiUserChest stale chest inventories after ownership changes.
- AzuCraftyBoxes chest ownership during crafting.

Install the same version on the dedicated server and every client, then restart.

Version 1.2.0 transfers dedicated simulation takeover and the ImpactfulSkills skill bridge to Serverbound. Install the same Serverbound candidate on the server and participating skill clients. DeepNorthCompat no longer supplies those integrations or requires a predecessor simulation DLL. Keep its independent chest and crafting fixes if you use them. Older DeepNorthCompat builds must be removed before using Serverbound. See [the migration guide](docs/serverbound-migration.md).

## Supported versions

- Valheim 1.0.16 and 1.0.17

Each fix is written against one exact build of the mods it touches:

- ImpactfulSkills 0.21.1
- AzuCraftyBoxes 1.8.27
- AAA Crafting 2.1.11
- SeaAnimals 0.3.9
- AirAnimals 0.3.2
- AzuEPI 2.6.3
- ValheimTune 0.7.8 and 0.7.9
- ValheimPerformanceOptimizations 1.2.3
- MultiUserChest 0.6.2
- Quick Stack - Store - Sort - Trash - Restock 1.4.15

Mod patches only activate if the corresponding mod and supported version are present.
