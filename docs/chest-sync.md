# Chest synchronization and diagnostics

DeepNorthCompat 1.1.3 fixes chest state and save defects reproduced in offline tests. It also records diagnostics on clients and servers, so you can collect a single report when something still goes wrong.

## Setup

Install the same version on the dedicated server and every client, then restart. Check the startup log for these lines:

- `Chests.Registry: APPLIED` on every peer. This peer answers other players' crafting handoff requests.
- `ChestCraft: APPLIED` on clients with AzuCraftyBoxes. Dedicated servers report it as inactive.
- `Chests: APPLIED` on every peer.
- `Diagnostics: APPLIED` unless you turned diagnostics off.

Investigate any `NOT APPLIED` message. Keep the existing [simulation launch checks](dedicated-simulation.md).

A peer running an older DeepNorthCompat can't answer handoff requests. After eight seconds, the client cancels the craft without consuming ingredients, and the report names the peer that didn't reply.

## Changes

- Clear the local use flag when a non-owner closes a chest or refreshes it through MultiUserChest. This lets the inventory load again while preserving an owner's active edits and other peers' use state.
- Refresh chests within Quick Stack's configured range before quick storing or restocking. MultiUserChest still handles transfers through its owner RPC.
- Ask the current owner to approve a handoff before Crafty consumes chest ingredients. The owner checks player identity, privacy, wards, range and chest use, and denies chests that its own pending craft needs. The client waits for ownership and current data, checks that its inventory matches the owner's by hash, then reselects ingredients.
- Select handoff chests the way Crafty consumes them, including single-ingredient recipes and the leave-one setting.
- Send a request again if ownership moved before the owner answered.
- Track chest lifetime by network view, so unloaded chests leave every lookup at once.

The crafting fix covers vanilla chests, upgrades and multicrafting. Backpacks, drawers, building and automatic feeding keep their existing behavior. Diagnostics flag non-owner saves in those paths as `NON_OWNER_WRITE` with the calling code.

## Collect a report

1. When something goes wrong, open chat and type `/dnc_mark` followed by a short note, for example `/dnc_mark chest showed old items`. The client and the server both record the marker with the events before it.
1. Reproduce the problem if you can.
1. Type `/dnc_report`. The client builds its report, asks the server for the server's report and waits up to 10 seconds. It saves the combined report and copies it to the clipboard.
1. Paste the clipboard where you report the problem. If the clipboard is empty, attach the saved file the command prints.

The server sends its report only to admins listed in `adminlist.txt` by default. Otherwise, the combined report explains how to get the server's report. Servers also save `DeepNorthCompat-report-server-latest.md` every five minutes when something changes, and when the world shuts down. Admins can attach that file instead.

A report is Markdown. It contains these sections:

- **Overview:** version, role, player, game build, server version and finding counts.
- **Player markers:** each `/dnc_mark` note with the events of the previous 90 seconds.
- **Problems and warnings:** each finding with that chest's earlier events.
- **Errors and exceptions from all mods:** errors and exceptions from any mod's log, deduplicated with counts and the events before the first occurrence.
- **Connected peers:** servers only. Each player's DeepNorthCompat version and applied patches.
- **Recent events, Event counts and DeepNorthCompat log.**
- **Settings and mods:** relevant settings, relevant mods with file hashes, and every loaded plugin.

## Files

Each client profile and server writes to `BepInEx/DeepNorthCompat/diagnostics`. The startup log prints the full path.

| Path | Contents |
|---|---|
| `DeepNorthCompat-events-<role>-<start>-<session>-<part>.tsv` | Every event, one tab-separated row each. Parts rotate at 8 MiB. The newest 20 sessions are kept, each with its first part and its newest 16 parts. |
| `reports/DeepNorthCompat-report-<role>-latest.md` | Written every five minutes when something changes, and when the world shuts down. Survives a crash. |
| `reports/DeepNorthCompat-report-<role>-<time>-combined.md` | Saved by `/dnc_report`. The newest 30 timestamped reports are kept. |
| `reports/DeepNorthCompat-report-server-<time>-requested.md` | Saved on the server whenever a player requests its report. |

Diagnostics never stop gameplay. If a file can't be written, reports still work from memory and the report lists the fault.

## Settings

Settings are in `BepInEx/config/DeepNorthCompat.cfg`. Restart after changing them.

```ini
[Diagnostics]
Enabled = true
Directory =
Server report access = Admins
```

- `Enabled`: records events and enables `/dnc_report` and `/dnc_mark`.
- `Directory`: leave empty for the default path, or set an absolute path.
- `Server report access`: servers only. Who may fetch the server's report: `Nobody`, `Admins` or `Everyone`.

## Events

Report lines use this format:

```text
time [severity] event [operation] chest=<id> owner=self|none|<peer> rev=<data>/<loaded> use=<local>/<replicated> payload=<hash> items=<name:quality:world level=count> | details
```

A chest shows current contents when its data and loaded revisions match. `loaded` is `none` before the first load.

| Event | Meaning |
|---|---|
| `container-loaded`, `container-unloaded` | A chest entered or left this peer's scene. |
| `open-use`, `close-use`, `state-change` | Use flags, owner or revisions changed. |
| `inventory-refreshed`, `inventory-saved`, `inventory-changed` | Contents changed by a load, a save or a local edit. |
| `owner-assignment` | This peer assigned a new owner. Includes the calling code. |
| `network-arrival` | Received a different owner or revision for a loaded chest. `applies_data` shows whether the game keeps the incoming data. |
| `local-use-release` | Warning. Cleared a non-owner's local use flag after an ownership change. |
| `NON_OWNER_WRITE` | Problem. This peer saved a chest it doesn't own. The game keeps the higher revision from any writer, so this can overwrite the owner's inventory. Includes the calling code. |
| `operation-start`, `operation-chest`, `operation-end` | Quick Stack or restock, with the chests it refreshed and the player's item change. |
| `craft-plan` | The chests a craft draws from and which need a handoff. |
| `craft-handoff-request`, `-owner`, `-reply`, `-ready`, `-owned` | Handoff progress on the requester and the owner. |
| `craft-handoff-cancelled` | The craft stopped before consuming anything. Details give the reason, such as `handoff-timeout`, `owner-denied`, `payload-diverged` or `chest-unloaded`. |
| `craft-result` | Items each craft moved across the player and the chests it can draw from. |
| `craft-output-without-consumption`, `craft-consumed-without-output` | Problem. A craft created or removed items without the matching change. |
| `peer-hello`, `server-hello` | Versions exchanged at login. A Problem means the versions differ. |
| `player-marker` | A `/dnc_mark` note. |
| `log-error` | An error or exception from any mod's log. |

## Gameplay checks

Test storing, restocking and crafting after shared chest use, ownership changes and reconnects. Include upgrades, multicrafting and single-ingredient recipes. Failed or cancelled handoffs should produce no items and consume no ingredients.

Offline tests simulate scenes, transport and output creation. Startup checks confirm loading; live sessions must verify multiplayer behavior.
