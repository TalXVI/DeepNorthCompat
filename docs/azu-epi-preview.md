# AzuEPI preview animation resets

AzuEPI 2.6.1 restarts the inventory character preview whenever any humanoid equips or unequips an item. Live testing in Deep North - Lab on October 2, 2026 traced the resets to two Harmony postfixes. Removing only those two made the animation smooth, and restoring them brought the resets back in the same session.

## Supported build

- Plugin and Harmony owner: `Azumatt.AzuExtendedPlayerInventory`, version `2.6.1`.
- AzuEPI DLL SHA-256: `41ED9378929090C5C46363A319C6A1F63F85DCF18DDE2DA1C0CAB49C39DBECFA`.
- Valheim assembly SHA-256: `96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127`.

Any other AzuEPI build fails the hash check and keeps vendor behavior until someone reviews it.

## What the fix changes

It removes these two parameterless postfixes in `AzuEPI.Game.PlayerPreview`:

- `PreviewInstantRefresh_EquipItem` on `Humanoid.EquipItem(ItemDrop.ItemData, bool)`.
- `PreviewInstantRefresh_UnequipItem` on `Humanoid.UnequipItem(ItemDrop.ItemData, bool)`.

Both call `PlayerPreviewManager.TryRefresh()` for every humanoid. `TryRefresh` runs `SetPreviewPose`, which calls `animator.Play("Movement", 0, 0f)` and `animator.Update(0f)`, then forces a preview camera render.

AzuEPI already updates the preview for the local player through two other hooks, and the fix requires both to be installed:

- `PreviewInstantRefresh_VisEquipmentPatch` on `VisEquipment.UpdateEquipmentVisuals()` mirrors changed equipment and renders without touching the pose.
- `UpdatePlayerPreviewVisuals` on `Humanoid.SetupAnimationState()` calls `PlayerPreviewManager.UpdatePlayerPreview(Humanoid)`.

`SetPreviewPose` was also the only code that copied the weapon stance parameters `statei` and `statef` into the preview. The fix adds a postfix to `UpdatePlayerPreview` that copies those two values when they differ, for the local player only. It never plays or updates the animator and never renders.

Nothing else needs ongoing sync. Valheim writes `statei`/`statef` only in `Humanoid.SetAnimationState`, which only `SetupAnimationState` calls, and no installed mod writes them. The clone's `Player`, `ZSyncAnimation` and `CharacterAnimEvent` components are disabled, so nothing else changes its animator parameters. Opening the inventory still runs the full `SetPreviewPose`.

## Observed cause

At frame 34689 an unrelated `Wolf(Clone)` called `EquipItem` for its already equipped `WolfAttack2` through `MonsterAI.SelectBestAttack` → `Humanoid.EquipBestWeapon`. The call returned `false`. AzuEPI's postfix still called `TryRefresh`. Equipment mirroring found no change, but the preview's normalized animation time jumped from 0.5172413 to 0 in the same state, followed by one forced render. Several nearby wolves repeated this, which explains the irregular reset pattern.

## Measurements

A temporary tracer counted events only while the preview was visible. Rates normalize the counts to one minute.

| State | Visible time | Unrelated equipment calls | Unnecessary `TryRefresh` calls | Unnecessary forced renders |
| --- | ---: | ---: | ---: | ---: |
| Original hooks | 82.11 seconds | 234 (170.98/min) | 234 | 234 |
| Only the two hooks removed | 184.08 seconds | 518 (168.84/min) | 0 | 0 |
| Original hooks restored | 466.10 seconds | 1,205 (155.12/min) | 1,205 | 1,205 |

The counts cover explicit `Camera.Render()` calls. The preview camera still renders every frame while the inventory is open. These runs don't measure FPS. Window focus and workload weren't matched closely enough, and the tracer itself costs time.

Removal alone left stale stance parameters. At frame 119875 the player held a sword and shield (`statei=4`, `statef=4`) while the preview still had the bow values `3`, `3`, and it stayed there until the inventory was reopened. With the stance postfix, 32 local preview updates (25 with inventory open, 7 closed) all ended with matching values and unchanged normalized animation time. Over 314.78 visible seconds, 552 unrelated equipment calls caused no `TryRefresh`, and the six `SetPreviewPose` calls matched six inventory openings. Armor, weapons, shields, quickslots, vanity appearance and reopening showed no visible problem. Breakage, death/respawn and loadouts weren't exercised live. All three go through vanilla equip and unequip, which reach `SetupAnimationState`.

## Harmony handling

The module runs in `Start`, because StartupAccelerator can defer AzuEPI's registrations past `Awake`. HarmonyX removes a patch method from all five patch kinds (prefix, postfix, transpiler, finalizer, IL manipulator) under any owner. So before changing anything, the module requires each removed postfix to be the only registration of its method on the target, owned by AzuEPI. Unpatching keeps every other registration and its insertion index.

If any step fails, the module restores missing vendor postfixes with their owner, priority, ordering constraints and debug flag, removes its own postfix, and logs the failure. Harmony gives restored patches new insertion indices. No installed mod depends on the old order, because `TryRefresh` only reads the local player and writes the preview.

Other owners on the equipment methods stay active, including AzuEPI's slot exclusivity and custom equipment visuals, ZenCombat automatic shields, World Advancement Progression, RuneMagic, Dive In, MyLittleUI and Recycle N Reclaim. ZenCombat's direct hand-item swaps call `SetupEquipment`, so they also reach the stance sync.

## Upstream

The public repository's `master` was at [`847a1022`](https://github.com/AzumattDev/AzuEPI/commit/847a1022f10c298139339b418606f07834426cc4) on September 11, 2026, labeled 2.4.14. Its [preview code](https://github.com/AzumattDev/AzuEPI/blob/847a1022f10c298139339b418606f07834426cc4/Game/PlayerPreview/PreviewUI.cs) still has both broad hooks and the animation restart. That source predates 2.6.1, and no later public fix was found.

## Manual check

Confirm the startup log contains `Preview.AzuEPI: APPLIED`. Open the inventory near active wolves and leave the preview visible. Change armor, swap sword, bow and shield, use quickslots, change vanity appearance and reopen the inventory. The preview should stay smooth and match the player. The previous DeepNorthCompat DLL should bring the resets back under the same conditions.
