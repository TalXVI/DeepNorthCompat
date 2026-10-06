# Changelog

## 1.1.5

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
