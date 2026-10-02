# DeepNorthCompat

This is a Valheim mod for a personal Valheim modpack.

It currently fixes bugs and incompatibilities involving:

- ImpactfulSkills and AzuCraftyBoxes quality-aware ingredient selection and consumption.
- ImpactfulSkills bow stamina scaling and tooltip reporting.
- SeaAnimals and AirAnimals custom creature drop ranges.
- AzuEPI inventory character preview, which restarted its animation whenever a nearby creature or player changed equipment.

## Supported versions

Each fix is written against one exact build of the mods it touches:

- Valheim 1.0.16
- ImpactfulSkills 0.21.0
- AzuCraftyBoxes 1.8.27
- AAA Crafting 2.1.11
- SeaAnimals 0.3.9
- AirAnimals 0.3.2
- AzuEPI 2.6.1

If one of those mods isn't installed, or a different version is, the fixes that depend on it don't activate and that mod behaves as it normally would. The log says which fixes were applied at startup.
