# Upgrade with Serverbound

DeepNorthCompat 1.2.0 transfers dedicated simulation compatibility to Serverbound. The migrated code includes VPO/VCP object management and the ImpactfulSkills client/server skill bridge. DeepNorthCompat no longer registers those patches or skill RPCs.

Stop the server and clients before upgrading. Remove predecessor simulation DLLs and DeepNorthCompat 1.1.6 or earlier. Serverbound rejects older DeepNorthCompat to prevent competing object ownership, duplicate XP and conflicting protocol handlers.

Install matching Serverbound on the dedicated server and participating ImpactfulSkills clients. Basic Serverbound simulation accepts vanilla clients. A client without Serverbound cannot publish skill state or receive the migrated owner-side XP protocol. DeepNorthCompat remains optional for Serverbound's migrated integrations.

Copy the required predecessor configuration into org.serverbound.valheim.cfg while stopped. Preserve the Deep North profile's existing allocation of networking features across Network, Tune, VPO and VCP. Serverbound's standalone defaults are a different configuration; they are not a pack upgrade preset.

DeepNorthCompat retains these independent fixes:

- The accepted non-owner chest-close correction also needed with vanilla ownership.
- MultiUserChest inventory refresh and Crafty ownership handoffs, including authority checks and cancelled-craft safety.
- VPO's Windows native-library initialization and recursive water-wave repair.
- The explicit Deep North policy that disables ValheimTune's client component while retaining its dedicated hooks.
- Quality, bow, drop, equipment-preview and diagnostic patches.

Serverbound does not disable Tune for every installation. Do not remove these independent fixes merely because their tests include server-owned objects.

The coordinated candidates passed their client and dedicated Mono fixtures and isolated dedicated startup. Multiplayer acceptance remains incomplete. Read Serverbound's README and release notes before live deployment.
