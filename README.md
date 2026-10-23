# DeepNorthCompat

This is a Valheim mod for a personal Valheim modpack.

It currently fixes bugs and incompatibilities involving:

- ImpactfulSkills and AzuCraftyBoxes quality-aware ingredient selection and consumption.
- AAA Crafting's crafting speed setting, which did not apply to the Forge of Potential.
- AAA Crafting's Upgrade pagination, which accumulated hidden recipe rows during repeated refreshes.
- ReforgedPotential Forge of Potential upgrades, which removed the item being upgraded before the chests holding the ingredients had handed over their contents.
- ImpactfulSkills bow stamina scaling and tooltip reporting, in builds before 0.25.0.
- SeaAnimals and AirAnimals custom creature drop ranges.
- AzuEPI inventory character preview, which restarted its animation whenever a nearby creature or player changed equipment.
- Deep North profile policy that disables ValheimTune on clients after its configuration binds.
- VPO native water-wave initialization on Windows dedicated servers.
- RuneMagic water-walking ice initialization, which can corrupt physics and saved vehicle positions on Linux dedicated servers.
- RuneMagic Ocean Currents and Decumberance rune sprites, which each left a material loaded after they faded. Sailing with Ocean Currents added about a thousand materials a second, so Valheim Visuals' material scans stalled the game for longer the longer you played.
- MultiUserChest stale chest inventories after ownership changes.
- AzuCraftyBoxes chest ownership during crafting.
- OdinShip canoe propulsion, replicated ship names, mounted ammunition refunds, input handling, manual FishPress payment, and the Decorative Ship Construction piece, which no longer reshapes terrain or clears cave roofs and now has hull and support collision.
- BetterMap exploration with CartographySkill, session cleanup, saved pin categories, portal labels, and vehicle map updates.
- TeleportEverything configuration-panel focus, which left some gameplay hotkeys active while the panel was open.
- PlantEasily planting grids, which checked only the first ingredient of a multi-ingredient recipe.
- PlantEverything placement dust, which came back after joining a server with NoCultivatorDust or NoBuildDust installed.
- My Little UI ammo counts, which redrew every hotbar update even when the number hadn't changed.
- HearthBelow pickaxe cuts, which came back after Hoe leveling, raising, or smoothing, or a RuneMagic Foundation Rune, filled them.

Requires ConditionalConfigSync 1.0.11 or newer. Install the same DeepNorthCompat version and ConditionalConfigSync on the dedicated server and every client, then restart.

## Supported versions

- Valheim 1.0.16, 1.0.17, and 1.0.20 on Windows clients and Windows/Linux dedicated servers

Each fix is written against one exact build of the mods it touches:

- ImpactfulSkills 0.21.1, 0.21.3, 0.22.0, 0.22.1, 0.23.0, 0.24.0, and 0.25.0
- AzuCraftyBoxes 1.8.27 and 1.8.28
- AAA Crafting 2.1.11 (forge speed and recipe row fixes on Valheim 1.0.20 Windows clients only)
- ReforgedPotential 2.0.8 (chest crafting wait fix on Valheim 1.0.20 Windows clients)
- SeaAnimals 0.3.9
- AirAnimals 0.3.2
- AzuEPI 2.6.3 and 2.6.4
- ValheimTune 0.7.8, 0.7.9, and 0.7.10
- ValheimPerformanceOptimizations 1.2.3
- RuneMagic 1.5.1 for water-walking ice initialization and rune sprite materials on Valheim 1.0.20 Windows clients and Windows/Linux dedicated servers
- MultiUserChest 0.6.2
- Quick Stack - Store - Sort - Trash - Restock 1.4.15 and 1.4.16
- OdinShip 0.8.7
- BetterMap 1.1.0
- CartographySkill 3.2.0 (for BetterMap's exploration integration)
- TeleportEverything 3.0.9, 3.0.10, and 3.0.11 (configuration-panel input fix on Valheim 1.0.17 and 1.0.20 Windows clients)
- PlantEasily 2.3.0 (grid cost fix on Valheim 1.0.17 and 1.0.20 Windows clients)
- PlantEverything 1.21.3 and 1.21.4 with NoCultivatorDust 1.0.7 or NoBuildDust 1.0.7 (placement dust fix on Valheim 1.0.17 and 1.0.20 Windows clients)
- My Little UI 1.2.31 (ammo count redraw fix on Valheim 1.0.17 and 1.0.20 Windows clients)
- HearthBelow 0.7.0, with RuneMagic 1.5.1 for the Foundation Rune (cut filling on Valheim 1.0.17 and 1.0.20 Windows clients and Windows/Linux dedicated servers)

Mod patches only activate if the corresponding mod and supported version are present.

On Valheim 1.0.20 Windows clients with AAA Crafting 2.1.11, the Forge of Potential uses AAA Crafting's `Crafting Speed` when `Override Crafting Speed` is on. The existing crafting skill reduction still applies.

The `Forge of Potential speed` setting defaults to `true`. To disable it, set `Forge of Potential speed = false` in the `[Crafting]` section of `BepInEx/config/DeepNorthCompat.cfg`. With ReforgedPotential installed, turning it off makes the Forge of Potential use ReforgedPotential's upgrade crafting time instead.

ConditionalConfigSync gives the server control of this setting by default. Server policy can allow per-client values. Changes made through a config manager or received from the server take effect immediately.

BetterMap pin categories survive local reloads and cartography-table sharing even when their map slots change. Automatic portal pins follow accepted portal tags while preserving their checked state and ownership. A place that already shows an automatic pin, including one read from a cartography table, gets no second pin. If a later table read removes a shared pin, the place is pinned again as your own the next time you pass it. An automatic pin you remove by hand stays removed. Older pins without category metadata are recovered only when their discovery records and labels identify one category. Ambiguous older pins retain their saved type.

Manual FishPress feeding checks that the owner supports the fix before consuming a fish. Rejected fish return to your inventory once space is available. If delivery is unconfirmed, the fish is not automatically restored. Pending refunds are lost when you leave the session or restart.

## Diagnostics

Diagnostics are off by default. To use them, set `Enabled = true` in the `[Diagnostics]` section of `BepInEx/config/DeepNorthCompat.cfg` on the server and on each client, then restart.

With diagnostics on, these chat commands report problems:

- `/dnc_mark <note>` records a problem with a short note, for example `/dnc_mark chest showed old items`.
- `/dnc_report` saves a full report, copies it to the clipboard, and includes the server's report for admins.
- `/dnc_report last` sends the report from your previous session, for example after a crash. After an unexpected exit, chat suggests it when you rejoin the same world.

On a server that collects reports, both commands also send your report to the server, and chat confirms when the server saved it. Your report goes to the server's operator. It includes your character name, Steam or platform ID, mod list, recent game events, and errors from your mods. Folder paths that contain your user name, and network addresses, are removed before sending. To keep reports on your computer, set `Send reports to server = false`.

Servers save received reports in `BepInEx/DeepNorthCompat/diagnostics/submissions/`, one file per report, named with the time, the player's character name and their Steam or platform ID. `index.tsv` in the same folder lists every report. Reports are deleted after 30 days, or sooner once a player has more than 20 or the folder holds more than 200 reports or 100 MiB. To refuse reports, set `Accept player reports = false` on the server.
