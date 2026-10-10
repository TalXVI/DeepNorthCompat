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
- OdinShip canoe propulsion, replicated ship names, mounted ammunition refunds, input handling, and manual FishPress payment.

Install the same version on the dedicated server and every client, then restart.

## Supported versions

- Valheim 1.0.16 and 1.0.17

Each fix is written against one exact build of the mods it touches:

- ImpactfulSkills 0.21.1 and 0.21.3
- AzuCraftyBoxes 1.8.27
- AAA Crafting 2.1.11
- SeaAnimals 0.3.9
- AirAnimals 0.3.2
- AzuEPI 2.6.3
- ValheimTune 0.7.8 and 0.7.9
- ValheimPerformanceOptimizations 1.2.3
- MultiUserChest 0.6.2
- Quick Stack - Store - Sort - Trash - Restock 1.4.15
- OdinShip 0.8.7

Mod patches only activate if the corresponding mod and supported version are present.

Manual FishPress feeding checks that the owner supports the fix before consuming a fish. Rejected fish return to your inventory once space is available. If delivery is unconfirmed, the fish is not automatically restored. Pending refunds are lost when you leave the session or restart.
