# Changelog

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
