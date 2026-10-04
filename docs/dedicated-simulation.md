# Dedicated simulation integration

The integration targets MistrCech's maintained valheim-serverside 1.11.0, commit `1a5dd9442d8106166af08ad45a8039e81351a42b`, and ValheimTune 0.7.8, commit `b24fabf8d9114c6b48b8f5a58bfe9a77085abdb4`. Vendor binaries are unchanged. This version was validated against the installed Windows Valheim client and dedicated server, both 1.0.16. The Linux 1.0.16 dedicated server assembly (SHA-256 `50035055F9B158A025CACD25E038B603943F7C2A465DA3021707B5F1E44E39FD`) is also accepted. Its IL differs from the Windows server (`7CAB9B49D31EC064591CA80402DD35C566E03B7297CFB7BF4696C38DA4E24D8B`) only in `Version.GetPlatform` and `UpscaledFrameBuffer.AutomaticRenderScaleSupported`, which no patched or verified method uses. The offline suite runs against the Windows server. A private Linux server in Docker passed the startup gates and a one-client session with HearthBelow terrain edits that persisted across a server restart. Any other game assembly needs verification if its hash differs.

## Object management

`SimulationPatch` runs after chainloading, when deferred Harmony registrations are available. It acts only when the fork's Core prefixes are registered. It removes VPO's `ZNetSceneObjectManagementPatch` registrations on `CreateDestroyObjects`, `AddToSector`, `InvalidateSector`, `HandleDestroyedZDO`, `Deserialize`, `AddInstance`, `Destroy`, and `Shutdown`, plus `ZDOManReleaseNearbyPatch.Prefix`. It also removes VCP's two `SceneIdleSkipPatch` registrations on `CreateDestroyObjects`. That removal is not required: HarmonyX runs every prefix, so VCP's skip cannot stop Core's prefix, and the hooks would only add an idle check. They are removed so Core is the single owner of the sweep.

All expected hooks, owners and patch kinds are checked before removal. Fork, game, VPO and VCP hashes must match; the fork hash is checked here, after Core registers, so a changed fork reaches the same fail-safe. A failure restores removed registrations, including their priority and ordering, and logs a failed launch gate. Core cannot safely run next to the restored VPO/VCP object management, so once Core is registered a failure also removes every fork Harmony owner except its server console and logs `Simulation: FORK DISABLED`. The server then runs vanilla object management and ownership, as the fork does when its own Core fails. If removal itself fails, the log says to stop the server before players join. Unrelated hooks remain. Clients keep their normal VPO/VCP registrations. VPO terrain collision threading stays enabled.

The VCP guard targets 0.32.3. Its sector-zero and resized-world corrections were reviewed against 0.32.2; the two idle-sweep hooks keep their signatures, and the fork still owns object creation and removal. The station-refund update also remains VCP's responsibility.

The fork already handles VCP's `CreateObjectsSorted` and `RemoveObjects` conflicts. In Lab it loads before VCP, and BepInEx adds each plugin to `PluginInfos` as it loads. The fork's early presence check therefore skips its VCP feature. After chainloading this integration registers the fork's existing `ZNetScene.Awake` postfix if it is missing; an existing registration is retained. Both `Compat` options remain false. The object-management fixes themselves are not duplicated. Core's pickable, ship, cooking-station and Leviathan fixes also remain the fork's responsibility.

## ImpactfulSkills

Server ownership makes ImpactfulSkills' uses of `Player.m_localPlayer` unsuitable for resource drops, taming, tamed loot, any-biome beehives and plants, and boat damage reduction. `OwnerSkillPatch` changes those specific handlers and helpers to use an explicit actor and replicated skill factors. It preserves the vendor's calculations and configuration values.

Clients publish normalized pickaxes, woodcutting and Animal Handling factors, plus Farming, Animal Handling and Voyaging levels (`DeepNorthCompat.level.*`), to their own player ZDO after the server confirms the integration is active. Publication follows skill changes and checks periodically; unchanged ZDO values do not add revisions. The handshake retries every five seconds. A protocol tag, finite factors in the range 0–1 and finite non-negative levels are required. Only a message from the connected server can activate publication or award Animal Handling XP, and the award must name that client's player.

The simulation fork belongs only on the dedicated server. Clients need DeepNorthCompat and ImpactfulSkills for this bridge, but no local simulation DLL. The server checks the fork's inspected build before preparing the bridge; clients activate publication only after the server handshake. A missing fork keeps the server bridge inactive.

Mining and woodcutting use the hit's actual player attacker. Taming uses the closest player within the existing 30-metre range. Tamed loot and slaughter XP use the closest player within the configured loot range. On the server, a beehive's or plant's any-biome flag uses the most skilled player with valid levels within 30 metres, and boat damage reduction uses the most skilled valid player aboard. The vendor's level thresholds and reduction formula are unchanged. Ties use owner ID; bonuses never stack. Each actor scope is restored by a finalizer, including nested calls and exceptions. Delayed mining drops run on the compatibility component so a player's logout does not destroy their coroutine runner.

Mining broadcasts reach every client. For server-owned `MineRock5`, only the server awards the bonus, once per destroyed area; repeated area-health notifications do not award it again. Existing client-owned behavior remains. Tree bonuses run at the felling branch: the installed vendor transpiler selected the earlier already-dead branch. The original game body must have the two inspected health branches or the fix is rejected.

Missing or invalid player data never blocks the game. Resource damage, taming, loot and deaths proceed with vanilla results and no owner-side bonus, and the server logs a warning naming the player. Flags and boat reduction skip players without valid levels, so clients on an older package are never selected. Check the clients' matching package if the warning persists.

This bridge does not transfer resources or creatures back to client ownership. Character control and the fork's original client-ownership exceptions remain unchanged.

## ValheimTune

In the shared pack, Tune binds its configuration normally. On the recognized client build, `TuneClientPatch` disables its component, including `Update`, and removes only Tune's Harmony owner. An early return from Tune's `Awake` would leave its configuration uninitialized, so that approach is not used. Dedicated processes retain Tune's hooks.

## Launch configuration

Both files belong under `BepInEx/config` on the server. They can be carried in the shared client profile; the fork disables itself on clients and this integration disables client Tune. Neither vendor synchronizes these settings, and routine mod synchronization does not deploy configuration.

| File | Section / key | Default | Launch value | Owner |
|---|---|---|---|---|
| `MVP.Valheim_Serverside_Simulations.cfg` | Networking / Enabled | true | false | Smoothbrain Network owns transport limits. |
| same | Performance / SendIntervalMs | 100 | 0 | Keep Network's adaptive send scheduling. |
| same | Performance / MaxCatchUpMs | 100 | 0 | Keep VPO's existing physics-step limit. |
| same | Fixes / SaveClientChanges | true | false | VCP owns the save-dirty fix. |
| same | Fixes / TeleportGhosts | true | false | VCP owns teleport sector invalidation. |
| `akoozie.valheimtune.cfg` | Sync / TopKSort | true | false | Keep Network's actor priority and vanilla ordering. |
| same | Sync / OverrideSendWindow | true | false | Network owns the queue window. |
| same | Steam / OverrideSendRate | true | false | Network owns Steam rate settings. |
| same | Fixes / SaveDirtyFix | true | false | VCP owns the save-dirty fix. |
| same | Fixes / DeadZdoPrune | true | false | Retain the existing dead-object lifecycle. |

Keep fork General / Enabled=true, Compat / ValheimCommunityPatchSpawnQueue=false and ValheimCommunityPatchUnload=false, DungeonLoadGuard=true, ServerTargetFps=60 and MaxZonesPerTick=1. Keep Tune DirtySets=true, ReconcileSeconds=30, AllPeersPerRound=false, RelayMinIntervalMs=0, MaxPacketsPerPeerPerFrame=0, TargetFrameRate=0, SkipRenderMesh=false, DeferAssetUnload=false, SpawnerLinkFix=true, DisconnectNoSleep=true, GlobalKeyDedupe=true, DisableOnUnknownBuild=true and both cleanup triggers false. Existing Network, VPO, VCP and gameplay configurations need no edits.

## Validation and launch gate

Run the full client and dedicated offline suites before packaging. They use locally installed assemblies without redistributing them. Tests cover all fork Core targets, Tune server targets, exact hook removal and rollback, role separation, skill replication, sender validation, actual resource/taming/loot formulas and mining deduplication. Native scenes and transport are controlled fixtures.

Before multiplayer testing, require `Simulation: APPLIED` and `OwnerSkills: APPLIED` on the server, and `Tune.Client: APPLIED` on clients. Also require the fork to report removal of VCP's spawn queue and zone-diff unload when the world starts. A rejected build, `FORK DISABLED`, missing registration or persistent missing-skill warning blocks launch. A startup check alone does not prove multiplayer correctness.

`python scripts/prepare-local-server.py --smoke` copies the local dedicated installation and Lab's mod/config files into an ignored disposable directory, starts a private world without crossplay, checks those server gates, requests a save, then stops the process. Set `DEEPNORTHCOMPAT_SERVER_PATH` first. It does not launch Gale or deploy remotely. The script and offline suite support configurable paths; the Windows runtime path was exercised. On Linux it sets Doorstop 4's `DOORSTOP_ENABLED` and `DOORSTOP_TARGET_ASSEMBLY` and adds the runtime's `linux64` and `doorstop_libs` to `LD_LIBRARY_PATH`.

Use a disposable local world. With two clients, test separate exploration, chest opening/renaming/simultaneous use, portals, logout/rejoin, resource bonuses at different skill levels, taming/loot attribution, raid/spawn locality, sailing and cooking. Save and restart; confirm buildings, container contents, spawner links and terrain edits persist. Increase to five or more players and inspect Tune's dirty-set watchdog, queues and frame times. Keep VPO threaded terrain collision baking for a targeted terrain/collision test.

DeepNorthCompat is distributed through the existing GitHub-release-to-Thunderstore workflow under `Talent/DeepNorthCompat`. Install that published package in the shared client profile so Gale's export, publication and server staging include it normally. The simulation fork remains a local server package and does not need distribution to friends. Local Gale packages are excluded from the published mod manifest and its server staging; include the simulation package separately in a later authorized server deployment. Do not edit Gale's database or represent a local package as a published package.
