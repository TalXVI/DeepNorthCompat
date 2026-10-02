# Changelog

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
