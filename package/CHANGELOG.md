# Changelog

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
