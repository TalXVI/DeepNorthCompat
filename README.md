# DeepNorthCompat

This is a Valheim mod for a personal Valheim modpack.

It currently fixes bugs and incompatibilities involving:

- ImpactfulSkills and AzuCraftyBoxes quality-aware ingredient selection and consumption.
- ImpactfulSkills bow stamina scaling and tooltip reporting.
- SeaAnimals and AirAnimals custom creature drop ranges.
- AzuEPI inventory character preview, which restarted its animation whenever a nearby creature or player changed equipment.
- Serverside Simulation with VPO/VCP object management, ImpactfulSkills owner-side bonuses, and ValheimTune client loading.
- MultiUserChest stale chest inventories after ownership changes.
- AzuCraftyBoxes chest ownership during crafting.

Install the same version on the dedicated server and every client, then restart.

## Supported versions

- Valheim 1.0.16

Each fix is written against one exact build of the mods it touches:

- ImpactfulSkills 0.21.0
- AzuCraftyBoxes 1.8.27
- AAA Crafting 2.1.11
- SeaAnimals 0.3.9
- AirAnimals 0.3.2
- AzuEPI 2.6.1
- [MistrCech Serverside Simulation](https://github.com/MistrCech/valheim-serverside) 1.11.0
- ValheimTune 0.7.8
- ValheimPerformanceOptimizations 1.2.3
- ValheimCommunityPatch 0.32.3
- MultiUserChest 0.6.2
- Quick Stack - Store - Sort - Trash - Restock 1.4.15

Mod patches only activate if the corresponding mod and supported version are present.
