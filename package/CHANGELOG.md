# Changelog

## 1.5.2

- Fixes a slowdown with AAA Crafting 2.1.11 on Valheim 1.0.20 Windows clients. Each time the Upgrade list refreshed, AAA Crafting hid the recipe rows outside the current page but never deleted them. In one long session they grew into the thousands, and game code that scans the interface had to walk through all of them. This likely caused the stutters of about 200 ms when opening inventories, chests, and crafting stations. Those rows are now deleted. Pages, filters, favorites, and the selected recipe work as before.
- The dedicated server and every client must run the same DeepNorthCompat version.

## 1.5.1

- Fixes ReforgedPotential 2.0.8 Forge of Potential upgrades that use ingredients from chests. ReforgedPotential removed the item being upgraded while DeepNorthCompat was still waiting for the chests to hand over their contents. Now the upgrade waits with the rest of the craft. Upgrade rules and odds don't change.
- With ReforgedPotential installed, setting `Forge of Potential speed = false` gives the Forge of Potential ReforgedPotential's upgrade crafting time.
- The dedicated server and every client must run the same DeepNorthCompat version.

## 1.5.0

Diagnostics changes. Gameplay is unchanged.

- Diagnostics are now off by default. To keep using `/dnc_mark` and `/dnc_report`, set `Enabled = true` in the `[Diagnostics]` section on the server and on each client.
- `/dnc_mark` and `/dnc_report` now also send the player's report to the server. The server saves it in `BepInEx/DeepNorthCompat/diagnostics/submissions/` and chat confirms the save, so players no longer need to find and send report files themselves. Each saved report names the player by the character name and Steam or platform ID their connection logged in with.
- Before sending, the client removes folder paths that contain the player's user name, and network addresses, from the report and the marker note.
- `/dnc_report last` sends the report from the previous session, for example after a crash. After an unexpected exit, chat suggests it when the player rejoins the same world.
- Servers keep received reports for 30 days, at most 20 per player and 200 or 100 MiB in total.
- New settings: `Send reports to server` on clients and `Accept player reports` on servers. Both default to on and only take effect with diagnostics on.
- The dedicated server and every client must run the same DeepNorthCompat version.

## 1.4.2

- Supports ImpactfulSkills 0.25.0 while retaining 0.21.1 through 0.24.0 support. The quality-aware crafting integration works unchanged.
- ImpactfulSkills 0.25.0 fixes the bow draw stamina scaling itself, so DeepNorthCompat's own bow correction stays inactive with it and still applies to the earlier supported builds. `Bow: fixed upstream` in the log is expected.

## 1.4.1

- Fixes RuneMagic 1.5.1 rune sprites, which left a material loaded after each one faded. Sailing with Ocean Currents added about a thousand materials a second, so Valheim Visuals' material scans, which run every 20 seconds, stalled the game for longer the longer you played. Decumberance had the same leak. Rune colors and fading are unchanged.

## 1.4.0

- Applies AAA Crafting 2.1.11's crafting speed override to the Forge of Potential. Adds `Crafting` / `Forge of Potential speed` config toggle. Enabled by default.
- Requires ConditionalConfigSync 1.0.11 or newer to sync the forge toggle.
- The dedicated server and every client must run the same DeepNorthCompat version.

## 1.3.16

- Supports AzuEPI 2.6.4 while retaining the preview animation fix and 2.6.3 support.
- Supports PlantEverything 1.21.4 while keeping placement dust removed after configuration sync and retaining 1.21.3 support.

## 1.3.15

- Fixes OdinShip 0.8.7's Decorative Ship Construction piece. It no longer raises ground a second time where terrain was already raised, and it no longer lowers the ground above it when placed in a cave, which removed the cave roof. Its hull and supports now block players and creatures, and the hammer can remove it. Pieces already placed get the fix the next time their area loads.

## 1.3.14

Diagnostics changes. Gameplay is unchanged.

- Quick Stack and Restock records now include the player's position. When a press leaves items in your inventory, the record names the nearest chest outside the range that holds each one, with its distance. This shows when a chest was simply too far away.
- Moving ships and carts no longer flood the event log. Their position updates used to be logged as container writes up to 250 times a second, which pushed older events out of the log within an hour.

## 1.3.13

- Prevents RuneMagic 1.5.1 water-walking ice from starting at zero scale, which can corrupt physics and saved vehicle positions on Linux dedicated servers. Ice growth and decay keep their existing behavior. This fix does not repair positions already corrupted in a world save.

## 1.3.12

- Supports ImpactfulSkills 0.24.0, Quick Stack 1.4.16, ValheimTune 0.7.10, and TeleportEverything 3.0.10 while retaining the previously supported builds.

## 1.3.11

- Supports the Valheim 1.0.20 Linux dedicated build, restoring chest, terrain, map-session, and ship compatibility patches on that build.

## 1.3.10

Performance improvements:

- Startup no longer re-hashes the game files for each check.
- HearthBelow terrain rebuilds no longer allocate per record.

## 1.3.9

- Supports Valheim 1.0.20 Windows clients and dedicated servers and ImpactfulSkills 0.23.0. Previously supported builds remain supported.

## 1.3.8

- Hoe leveling, raising, and smoothing, and RuneMagic's Foundation Rune, now fill HearthBelow pickaxe cuts under the ground they edit. These edits used to change only the surface height, so the cut came back when the terrain reloaded. A fill follows later height edits, never reaches above the surface the tool leaves, and is removed with the area's other dig records. A player or server without this version shows the original cut.

## 1.3.7

- `/dnc_report` shows the saved report path with forward slashes, so the chat no longer turns `\v` and `\r` in a Windows path into control characters.

## 1.3.6

- Support My Little UI 1.2.31 for the ammo count redraw fix. The audited ammo code is unchanged from 1.2.30.

## 1.3.5

- My Little UI 1.2.30 ammo labels now skip formatting and redrawing when the displayed count is unchanged. Bait labels keep their existing behavior.
- Support TeleportEverything 3.0.9 for the configuration-panel input fix, which blocks gameplay hotkeys while the panel is open.

## 1.3.4

- `/dnc_report` now describes the world where you stand, on your client and on the server: ground height, stored and applied terrain edits, the mods that patch terrain height limits, base protection areas with their radius, and nearby creatures with their owners. Look at a creature before you run the command to mark it in both reports. A server on an earlier version still sends its report, without this section.

## 1.3.3

- PlantEasily 2.3.0 planting grids now check every ingredient of a multi-ingredient recipe, such as PlantEverything's Ashwood sapling. Cells you cannot pay for show as lacking resources. Single-ingredient recipes are unchanged.
- Keeps placement dust removed from PlantEverything 1.21.3 bushes, pickables, and decorative pieces after joining a server, when NoCultivatorDust 1.0.7 or NoBuildDust 1.0.7 is installed. The placement sound still plays.

## 1.3.2

- Supports audited AzuCraftyBoxes 1.8.28 while retaining 1.8.27 support. Quality-aware crafting and chest ownership during crafting are unchanged.

## 1.3.1

- Supports audited ImpactfulSkills 0.22.0 and 0.22.1 while retaining 0.21.1 and 0.21.3 support. Preserves upstream refund eligibility, upgrade refund amounts, crossbow reload fixes, perk thresholds, and unlock messages.
- Blocks gameplay input while TeleportEverything 3.0.8's configuration panel is open, including OdinShip's customization and turret hotkeys. Closing the panel restores the original input behavior.

## 1.3.0

- Adds audited BetterMap 1.1.0 support for Valheim 1.0.17. Preserves CartographySkill 3.2.0 exploration progression after loading skills and changing settings.
- Flushes BetterMap discovery records before character saves and clears transient map and vehicle state between sessions. Keeps saved discovery, vehicle and death history.
- Stores stable pin categories through local saves and cartography tables, including optional boss-altar pins. Restores both type and icon after slot changes, preserves hidden filters, and retains pending recovery for older pins across saves.
- Updates identified automatic portal pins from accepted portal tags without replacing them or changing checked state, author or ownership. Portal destruction preserves nearby manual pins.
- Stops automatic pins from stacking on a place that already shows one, including pins read from a cartography table. A place is pinned again after a table read removes its shared pin, and an automatic pin removed by hand stays removed.
- Keeps character saves and map loads running when BetterMap pin data cannot be read or written, and logs the failure.
- Redraws vehicle pins when remote positions or headings change.

## 1.2.3

- Supports ImpactfulSkills 0.21.3 while retaining audited 0.21.1 support. Preserves upstream crafting bonus limits and material refund eligibility.
- Adds audited OdinShip 0.8.7 compatibility: canoe ownership and timestep handling, replicated nameplate updates, mounted turret ammunition refunds, and input checks for gameplay, text entry, maps, and radial menus. Preserves OdinShip's original keys and hover hints.
- Manual FishPress feeding confirms owner support before payment and restores explicitly rejected fish to the payer's inventory. Full inventories retain the refund until space is available; uncertain deliveries are not retried or refunded automatically.

## 1.2.2

- Supports AzuExtendedPlayerInventory 2.6.3 after auditing its preview hooks. Retains smooth inventory-preview animation and local weapon-stance synchronization.

## 1.2.1

- Supports ImpactfulSkills 0.21.1 after auditing its bow and quality-crafting hooks. Retains the bow stamina correction and quality-aware crafting from AzuCraftyBoxes containers.

## 1.2.0

- Removes dedicated object-management integration and the ImpactfulSkills server/client bridge, along with their hooks, protocol handlers and simulation dependency.
- Retains independent chest-close, MultiUserChest refresh, Crafty handoff, gameplay and VPO native-loading fixes.
- Retains client ValheimTune disabling as an explicit Deep North profile policy.
- Verifies downloaded release packages against every validated source file. Validation records remain stable after committing them.

## 1.1.6

- Supports ValheimCommunityPatch 0.34.1 while preserving its loading confirmation, portal prefetch and HearthBelow compatibility fixes.
- Fixes VPO 1.2.3 native-library initialization on Windows dedicated servers, preventing recursive water-wave calls. Servers verify native loading and the Burst job probe before reporting the fix applied.
- Records settled Quick Stack and restock transfers after MultiUserChest's delayed returns. Diagnostic reports and markers retain item movements and flag unaccounted items when the touched chests have current data.

## 1.1.5

- Supports Valheim 1.0.17 clients and dedicated servers (Windows and Linux) and ValheimTune 0.7.9. Neither update changes a method this mod patches.
- Keep recipes stocked from other players' chests craftable in the crafting list that vanilla rebuilds right after a craft. They were drawn greyed out until the next rebuild.

## 1.1.4

- Targets ValheimCommunityPatch 0.32.4.

## 1.1.3

- Clear stuck chest-use flags after ownership loss and refresh nearby inventories within Quick Stack's range.
- Wait for chest ownership and current inventory data before crafting. The owner verifies the requester, explains denials, and keeps chests its own craft needs. The client checks the inventory hash and retries when ownership moves.
- Hand off only the chests Crafty consumes from, including for single-ingredient recipes.
- Add `/dnc_report`, which copies a combined client and server diagnostic report to the clipboard, and `/dnc_mark <note>`, which marks a problem on both sides. Server reports go to admins by default.
- Record chest, crafting, network, version and error events on clients and servers in `BepInEx/DeepNorthCompat/diagnostics`. Flag saves by non-owners and crafts that create or remove items without the matching change.

## 1.1.2

- A failed simulation launch gate on a server now disables the simulation fork, so the server runs vanilla object management instead of fork Core next to restored VPO/VCP hooks. A changed fork build is now detected at this gate.
- Missing skill data no longer pauses resource damage, taming, loot or deaths; they proceed without the owner-side bonus and the server logs a warning.
- Server-owned beehives and plants use the most skilled nearby player's any-biome flags, and boat damage reduction uses the most skilled player aboard.
- Corrected the documented reason for removing VCP's idle-sweep hooks.

## 1.1.1

- Recognizes the Linux 1.0.16 dedicated server, so dedicated simulation and owner-side skill bonuses apply on Linux hosts. Its game assembly differs from the Windows server only in platform reporting.

## 1.1.0

- Integrate dedicated simulation with VPO/VCP and preserve owner-side ImpactfulSkills bonuses.
- Keep ValheimTune inactive on clients in the shared pack.
- Targets VCP 0.32.3 after reviewing its sector and scene-management changes.
- Registers the fork's existing VCP takeover when load order skipped it.
- Client skill publication works with the simulation fork installed only on the server.

## 1.0.4

- Supports AzuCraftyBoxes 1.8.27 and AAA Crafting 2.1.11. Quality crafting turned itself off with these versions until now.
- Targets Valheim 1.0.16, ImpactfulSkills 0.21.0, AzuCraftyBoxes 1.8.27, AAA Crafting 2.1.11, SeaAnimals 0.3.9, AirAnimals 0.3.2 and AzuEPI 2.6.1.

## 1.0.3

- The AzuEPI 2.6.1 inventory character preview no longer restarts its animation whenever a nearby creature or player equips or unequips something. It still follows your equipment and weapon stance.
- Other AzuEPI builds keep AzuEPI's original behavior until they're reviewed.

## 1.0.2

- Supports ImpactfulSkills 0.21.0. The bow and quality crafting fixes stay active with the updated build.

## 1.0.1

- Quality crafting no longer cancels when a chest owned by another player supplies a whole stack (MultiUserChest).
- A failed quality craft returns the item being upgraded and refreshes the player's inventory.
- The crafting panel's quality preview reflects remaining ingredients right after a craft.
- Drawer-style containers without an inventory are skipped instead of blocking quality crafts.
- A creature drop parser that doesn't match the expected code now stays unpatched and gets reported, instead of breaking startup patching. This also holds when a startup optimizer delays Harmony patching until after plugin load.
- After loading finishes, the mod checks the drop-range patches and logs any parser it couldn't patch.

## 1.0.0

- Quality-aware crafting integration for ImpactfulSkills and AzuCraftyBoxes.
- Corrected ImpactfulSkills bow stamina scaling and reporting.
- Corrected SeaAnimals and AirAnimals custom drop-range parsing.
