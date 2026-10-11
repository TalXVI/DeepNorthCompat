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
- BetterMap exploration with CartographySkill, session cleanup, saved pin categories, portal labels, and vehicle map updates.

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
- BetterMap 1.1.0
- CartographySkill 3.2.0 (for BetterMap's exploration integration)

Mod patches only activate if the corresponding mod and supported version are present.

BetterMap pin categories survive local reloads and cartography-table sharing even when their map slots change. Automatic portal pins follow accepted portal tags while preserving their checked state and ownership. A place that already shows an automatic pin, including one read from a cartography table, gets no second pin. If a later table read removes a shared pin, the place is pinned again as your own the next time you pass it. An automatic pin you remove by hand stays removed. Older pins without category metadata are recovered only when their discovery records and labels identify one category. Ambiguous older pins retain their saved type.

Manual FishPress feeding checks that the owner supports the fix before consuming a fish. Rejected fish return to your inventory once space is available. If delivery is unconfirmed, the fish is not automatically restored. Pending refunds are lost when you leave the session or restart.
