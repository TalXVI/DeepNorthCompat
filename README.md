# DeepNorthCompat

This is a Valheim mod for a personal Valheim modpack.

It currently fixes bugs and incompatibilities involving:

- ImpactfulSkills and AzuCraftyBoxes quality-aware ingredient selection and consumption.
- ImpactfulSkills bow stamina scaling and tooltip reporting.
- SeaAnimals and AirAnimals custom creature drop ranges.
- AzuEPI inventory character preview, which restarted its animation whenever a nearby creature or player changed equipment.
- Serverside Simulation with VPO/VCP object management, ImpactfulSkills owner-side bonuses, and ValheimTune client loading.

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

Mod patches only activate if the corresponding mod and supported version are present.
