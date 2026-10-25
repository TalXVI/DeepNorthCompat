using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    // Describes what this peer holds around one point: terrain heights and who owns them, terrain
    // height-limit hooks, base protection areas and creatures. `/dnc_report` captures it on the
    // client and on the server for the same point, so the two blocks compare line by line.
    // It only reads state. Every part reports its own failure as a line.
    internal static class WorldProbe
    {
        internal const string Legend = "What this peer holds at the position the report was requested from. The client and server reports each carry "
            + "this block for the same point, so compare them line by line. `ground` is the terrain collider that creatures and spawn checks use. "
            + "`generated` is the height before any edit, `stored` is the saved terrain edit and `applied` is how much of it this peer's terrain "
            + "shows. `LIMITED` marks a peer that applies less than it stores, which happens when its terrain height limit is lower than the one "
            + "used to make the edit. `owner` is `self`, `server`, `none` or a peer ID. A creature line ends with `BELOW GROUND` when the "
            + "creature is more than 1 m under this peer's terrain. In the synced-object census, `settled` means the next vanilla sync would change "
            + "nothing, and `skipped` counts sync calls the patch avoided.";

        private const float AreaRange = 150f, CreatureRange = 64f;
        private const int AreaLimit = 30, CreatureLimit = 25;
        private const string HeightsGuid = "digitalroot.mods.heightmapunlimitedjvl", BenchGuid = "Azumatt.AzuWorkbenchTweaks";
        private static readonly string[] TerrainMethods = { "LevelTerrain", "RaiseTerrain", "ApplyToHeightmap" };

        internal static string[] Capture(Vector3 point, ZDOID looked)
        {
            var lines = new List<string>();
            Part(lines, "position", () => lines.Add("position: " + Text(point) + "; zone " + Zone(point) + "; water level "
                + (ZoneSystem.instance == null ? "unknown" : Number(ZoneSystem.instance.m_waterLevel))));
            float? ground = null;
            Part(lines, "ground", () =>
            {
                ground = Ground(point);
                lines.Add("ground: " + (ground == null ? "no terrain collider here. This peer has not loaded the zone." : Number(ground.Value)));
            });
            Part(lines, "terrain", () => Terrain(lines, point));
            Part(lines, "terrain hooks", () => lines.Add("terrain hooks: " + Hooks()));
            Part(lines, "height limit mod", () => lines.Add("height limit mod: " + Mod(HeightsGuid, key => key.EndsWith(" Height", StringComparison.Ordinal))));
            Part(lines, "spawn system", () => lines.Add("spawn system: " + Spawner(point)));
            Part(lines, "workbench mod", () => lines.Add("workbench mod: " + Mod(BenchGuid, key => key.StartsWith("WorkBenchRange", StringComparison.Ordinal))));
            Part(lines, "base protection", () => Areas(lines, point, ground));
            Part(lines, "creatures", () => Creatures(lines, point, looked));
            Part(lines, "synced objects", () => Synced(lines));
            return lines.ToArray();
        }

        private static void Part(List<string> lines, string name, Action capture)
        {
            try { capture(); }
            catch (Exception error) { lines.Add(name + ": unavailable (" + error.GetType().Name + ": " + error.Message.Split('\n')[0] + ")"); }
        }

        private static float? Ground(Vector3 point) => ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out float height) ? height : (float?)null;

        private static void Terrain(List<string> lines, Vector3 point)
        {
            Heightmap map = Heightmap.FindHeightmap(point);
            if (map == null) { lines.Add("heightmap: none loaded here"); return; }
            var data = (HeightmapBuilder.HMBuildData?)AccessTools.Field(typeof(Heightmap), "m_buildData").GetValue(map);
            List<float>? generated = data?.m_baseHeights;
            int side = map.m_width + 1;
            if (generated == null || generated.Count != side * side) { lines.Add("heightmap: loaded, but its generated heights are not ready"); return; }
            map.WorldToVertex(point, out int x, out int y);
            if (x < 0 || y < 0 || x >= side || y >= side) { lines.Add("heightmap: position is outside the loaded heightmap"); return; }
            int index = y * side + x;
            float lift = map.transform.position.y, applied = map.GetHeight(x, y) - generated[index];
            string heights = "heightmap: nearest vertex " + Number(map.GetHeight(x, y) + lift) + "; generated " + Number(generated[index] + lift)
                + "; applied " + Signed(applied);

            TerrainComp edits = TerrainComp.FindTerrainCompiler(point);
            float[]? level = edits == null ? null : (float[]?)AccessTools.Field(typeof(TerrainComp), "m_levelDelta").GetValue(edits);
            float[]? smooth = edits == null ? null : (float[]?)AccessTools.Field(typeof(TerrainComp), "m_smoothDelta").GetValue(edits);
            if (level == null || smooth == null || level.Length != side * side || smooth.Length != side * side)
            {
                lines.Add(heights);
                lines.Add("terrain edits: " + (edits == null ? "none stored for this zone on this peer" : "stored, but not loaded yet"));
                return;
            }
            float stored = level[index] + smooth[index];
            lines.Add(heights + "; stored " + Signed(stored) + " (level " + Signed(level[index]) + ", smooth " + Signed(smooth[index]) + "); "
                + Verdict(stored, applied));
            int edited = 0, limited = 0; float low = float.MaxValue, high = float.MinValue;
            for (int i = 0; i < level.Length; i++)
            {
                float change = level[i] + smooth[i];
                if (change == 0) continue;
                edited++; low = Math.Min(low, change); high = Math.Max(high, change);
                if (Limited(change, map.GetHeight(i % side, i / side) - generated[i])) limited++;
            }
            var view = (ZNetView?)AccessTools.Field(typeof(TerrainComp), "m_nview").GetValue(edits);
            lines.Add("terrain edits in this zone: owner " + Owner(view) + "; " + AccessTools.Field(typeof(TerrainComp), "m_operations").GetValue(edits)
                + " operations; " + edited + " edited vertices" + (edited == 0 ? "" : ", stored from " + Signed(low) + " to " + Signed(high)
                + "; " + limited + " LIMITED"));
        }

        // The game adds the stored edit to the generated height and clamps the sum to its height limit.
        private static bool Limited(float stored, float applied) => Math.Abs(stored) - Math.Abs(applied) > 0.1f && stored * applied >= 0;

        private static string Verdict(float stored, float applied)
        {
            if (Limited(stored, applied)) return "LIMITED: this peer stores " + Signed(stored) + " and applies " + Signed(applied);
            return Math.Abs(stored - applied) <= 0.1f ? (stored == 0 ? "not edited" : "applied in full") : "applied differs from stored";
        }

        private static string Hooks() => string.Join("; ", TerrainMethods.Select(name =>
        {
            MethodInfo method = AccessTools.DeclaredMethod(typeof(TerrainComp), name);
            if (method == null) return name + " not found";
            Patches patches = Harmony.GetPatchInfo(method);
            var owners = patches == null ? new List<string>() : patches.Transpilers.Select(patch => "transpiler " + patch.owner)
                .Concat(patches.Prefixes.Select(patch => "prefix " + patch.owner)).Concat(patches.Postfixes.Select(patch => "postfix " + patch.owner))
                .Concat(patches.Finalizers.Select(patch => "finalizer " + patch.owner)).ToList();
            return name + " " + (owners.Count == 0 ? "none" : string.Join(", ", owners));
        }));

        private static string Mod(string guid, Func<string, bool> wanted)
        {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info)) return guid + " absent";
            ConfigFile? config = info.Instance == null ? null : info.Instance.Config;
            string settings = config == null ? "settings unavailable" : string.Join("; ", config.Keys.Where(key => wanted(key.Key))
                .Select(key => key.Key + " = " + Convert.ToString(config[key].BoxedValue, CultureInfo.InvariantCulture)));
            return guid + " " + info.Metadata.Version + "; " + (settings.Length == 0 ? "no matching settings" : settings);
        }

        private static string Spawner(Vector3 point)
        {
            var systems = (List<SpawnSystem>)AccessTools.Field(typeof(SpawnSystem), "m_instances").GetValue(null);
            Vector2s zone = ZoneSystem.GetZone(point);
            SpawnSystem? system = systems.FirstOrDefault(candidate => candidate != null && ZoneSystem.GetZone(candidate.transform.position) == zone);
            if (system == null) return "none loaded for this zone (" + systems.Count + " loaded on this peer)";
            return "owner " + Owner((ZNetView?)AccessTools.Field(typeof(SpawnSystem), "m_nview").GetValue(system));
        }

        private static void Areas(List<string> lines, Vector3 point, float? ground)
        {
            Vector3 floor = ground == null ? point : new Vector3(point.x, ground.Value, point.z);
            lines.Add("base protection at position: " + Protector(point) + (ground == null ? "" : "; at ground: " + Protector(floor)));
            var areas = EffectArea.GetAllAreas().Where(area => area != null && (area.m_type & EffectArea.Type.PlayerBase) != 0)
                .Select(area => (Area: area, Collider: area.GetComponent<Collider>())).Where(entry => entry.Collider != null)
                .Select(entry => (entry.Area, entry.Collider, Shape: Shape(entry.Collider)))
                .Select(entry => (entry.Area, entry.Collider, entry.Shape.Centre, entry.Shape.Radius, Distance: Vector3.Distance(entry.Shape.Centre, point)))
                .Where(entry => entry.Distance <= AreaRange).OrderBy(entry => entry.Distance).ToArray();
            lines.Add("base areas within " + Number(AreaRange) + " m: " + areas.Length + (areas.Length == 0 ? "" : " (" + string.Join(", ",
                areas.GroupBy(entry => Name(entry.Area) + " r=" + Number(entry.Radius)).OrderByDescending(group => group.Count())
                    .Select(group => group.Key + " x" + group.Count())) + ")"));
            foreach (var entry in areas.Take(AreaLimit))
            {
                ZNetView view = entry.Area.GetComponentInParent<ZNetView>();
                CraftingStation station = entry.Area.GetComponentInParent<CraftingStation>();
                lines.Add("  " + Name(entry.Area) + " id=" + Id(view) + " owner=" + Owner(view) + " centre=" + Text(entry.Centre) + " radius=" + Number(entry.Radius)
                    + (entry.Collider is SphereCollider sphere ? " collider_radius=" + Number(sphere.radius) : " collider=" + entry.Collider.GetType().Name)
                    + (station == null ? "" : " build_range=" + Number(station.m_rangeBuild)) + (entry.Collider.enabled ? "" : " DISABLED")
                    + " distance=" + Number(entry.Distance) + " covers_position=" + YesNo(entry.Distance <= entry.Radius)
                    + (ground == null ? "" : " covers_ground=" + YesNo(Vector3.Distance(entry.Centre, floor) <= entry.Radius)));
            }
            if (areas.Length > AreaLimit) lines.Add("  (" + (areas.Length - AreaLimit) + " farther areas not listed)");
        }

        // The same query the game's spawn checks make.
        private static string Protector(Vector3 point)
        {
            EffectArea area = EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase);
            return area == null ? "no" : "yes, " + Name(area) + " id=" + Id(area.GetComponentInParent<ZNetView>());
        }

        // A base area protects a sphere, so a point far above or below its centre is outside it.
        // The sphere comes from the collider's own values because an inactive collider has empty bounds.
        private static (Vector3 Centre, float Radius) Shape(Collider collider)
        {
            if (!(collider is SphereCollider sphere)) return (collider.bounds.center, collider.bounds.extents.x);
            Vector3 scale = sphere.transform.lossyScale;
            return (sphere.transform.TransformPoint(sphere.center), sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        }

        private static string Name(Component part)
        {
            ZNetView view = part.GetComponentInParent<ZNetView>();
            return Utils.GetPrefabName((view != null ? view.gameObject : part.gameObject).name);
        }

        private static void Creatures(List<string> lines, Vector3 point, ZDOID looked)
        {
            var near = Character.GetAllCharacters().Where(creature => creature != null && !creature.IsPlayer())
                .Select(creature => (Creature: creature, Distance: Vector3.Distance(creature.transform.position, point)))
                .Where(entry => entry.Distance <= CreatureRange || !looked.IsNone() && entry.Creature.GetZDOID() == looked)
                .OrderBy(entry => entry.Distance).ToArray();
            lines.Add("creatures within " + Number(CreatureRange) + " m: " + near.Count(entry => entry.Distance <= CreatureRange)
                + (looked.IsNone() ? "" : "; `*` marks the one the player looked at"));
            bool found = false;
            foreach (var entry in near.Where((entry, index) => index < CreatureLimit || entry.Creature.GetZDOID() == looked))
            {
                Character creature = entry.Creature;
                Vector3 position = creature.transform.position;
                float? ground = Ground(position);
                bool marked = !looked.IsNone() && creature.GetZDOID() == looked;
                found |= marked;
                lines.Add((marked ? "* " : "  ") + Utils.GetPrefabName(creature.gameObject.name) + " id=" + creature.GetZDOID() + " owner=" + Owner(creature.GetOwner())
                    + " tamed=" + YesNo(creature.IsTamed()) + " position=" + Text(position) + " ground=" + (ground == null ? "none" : Number(ground.Value))
                    + " liquid=" + Number(creature.GetLiquidLevel()) + " swimming=" + YesNo(creature.IsSwimming()) + " distance=" + Number(entry.Distance)
                    + (ground != null && position.y < ground.Value - 1f ? " BELOW GROUND by " + Number(ground.Value - position.y) + " m" : ""));
            }
            if (near.Length > CreatureLimit) lines.Add("  (" + (near.Length - CreatureLimit) + " farther creatures not listed)");
            if (looked.IsNone() || found) return;
            ZDO? record = ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(looked);
            lines.Add("* looked-at creature id=" + looked + ": " + (record == null ? "unknown to this peer"
                : "known but not instantiated on this peer; owner=" + Owner(record.GetOwner()) + " position=" + Text(record.GetPosition())));
        }

        private static void Synced(List<string> lines)
        {
            var totals = new SyncedCounts();
            var prefabs = new Dictionary<string, SyncedCounts>(StringComparer.Ordinal);
            foreach (IMonoUpdater updater in ZSyncTransform.Instances)
            {
                ZSyncTransform? sync = updater as ZSyncTransform;
                if (!sync) continue;

                ZNetView? view = sync.GetComponent<ZNetView>();
                ZDO? zdo = view ? view.GetZDO() : null;
                string? name = zdo == null ? null : ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name;
                name ??= Utils.GetPrefabName(sync.gameObject);
                if (!prefabs.TryGetValue(name, out SyncedCounts? counts))
                {
                    counts = new SyncedCounts();
                    prefabs.Add(name, counts);
                }

                Rigidbody? body = sync.GetComponent<Rigidbody>();
                bool hasBody = body;
                bool sleeping = body && body.IsSleeping();
                Floating? floating = sync.GetComponent<Floating>();
                bool hasFloating = floating;
                bool inLiquid = floating && floating.HaveLiquidLevel();
                bool settled = IdleSyncPatch.IsSettled(sync);
                OwnerBucket owner = Bucket(zdo == null ? 0 : zdo.GetOwner());
                Add(totals, owner, hasBody, sleeping, hasFloating, inLiquid, settled);
                Add(counts, owner, hasBody, sleeping, hasFloating, inLiquid, settled);
            }

            if (totals.Count == 0)
            {
                lines.Add("synced objects: none loaded");
                return;
            }

            string idle = IdleSyncPatch.Active
                ? IdleSyncPatch.Settled.ToString(CultureInfo.InvariantCulture) + " settled now, skipped "
                    + IdleSyncPatch.Skipped.ToString(CultureInfo.InvariantCulture) + " of "
                    + IdleSyncPatch.Checked.ToString(CultureInfo.InvariantCulture) + " checks since start"
                : "inactive on this peer";
            lines.Add("synced objects: " + totals.Count.ToString(CultureInfo.InvariantCulture) + " loaded; owner self "
                + totals.Self + ", server " + totals.Server + ", other " + totals.Other + ", none " + totals.None
                + "; bodies " + totals.Bodies + ", sleeping " + totals.Sleeping + "; floating " + totals.Floating
                + ", in liquid " + totals.InLiquid + "; idle sync: " + idle);

            foreach (KeyValuePair<string, SyncedCounts> prefab in prefabs.OrderByDescending(entry => entry.Value.Count)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal).Take(15))
            {
                SyncedCounts count = prefab.Value;
                lines.Add("synced prefab " + prefab.Key + ": " + count.Count + "; owner self/server/other/none "
                    + count.Self + "/" + count.Server + "/" + count.Other + "/" + count.None
                    + "; sleeping " + count.Sleeping + " of " + count.Bodies + " bodies; in liquid " + count.InLiquid
                    + "; settled " + count.Settled);
            }
        }

        private static void Add(SyncedCounts counts, OwnerBucket owner, bool hasBody, bool sleeping, bool floating, bool inLiquid, bool settled)
        {
            counts.Count++;
            switch (owner)
            {
                case OwnerBucket.Self: counts.Self++; break;
                case OwnerBucket.Server: counts.Server++; break;
                case OwnerBucket.Other: counts.Other++; break;
                case OwnerBucket.None: counts.None++; break;
            }
            if (hasBody) counts.Bodies++;
            if (sleeping) counts.Sleeping++;
            if (floating) counts.Floating++;
            if (inLiquid) counts.InLiquid++;
            if (settled) counts.Settled++;
        }

        private static OwnerBucket Bucket(long owner) => Owner(owner) switch
        {
            "self" => OwnerBucket.Self,
            "server" => OwnerBucket.Server,
            "none" => OwnerBucket.None,
            _ => OwnerBucket.Other
        };

        private static string Id(ZNetView? view) => view != null && view.GetZDO() != null ? view.GetZDO().m_uid.ToString() : "none";

        private static string Owner(ZNetView? view) => view != null && view.GetZDO() != null ? Owner(view.GetZDO().GetOwner()) : "no network object";

        private static string Owner(long owner)
        {
            if (owner == 0) return "none";
            if (ZDOMan.instance != null && owner == ZDOMan.GetSessionID()) return "self";
            ZNet net = ZNet.instance;
            if (net == null) return owner.ToString(CultureInfo.InvariantCulture);
            if (!net.IsServer()) return net.GetServerPeer()?.m_uid == owner ? "server" : owner.ToString(CultureInfo.InvariantCulture);
            ZNetPeer? peer = net.GetPeer(owner);
            return owner.ToString(CultureInfo.InvariantCulture) + (peer == null ? " (not connected)" : " (" + peer.m_playerName + ")");
        }

        private static string Zone(Vector3 point) { Vector2s zone = ZoneSystem.GetZone(point); return "(" + zone.x + ", " + zone.y + ")"; }
        private static string YesNo(bool value) => value ? "yes" : "no";
        private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Signed(float value) => value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
        internal static string Text(Vector3 value) => "(" + Number(value.x) + ", " + Number(value.y) + ", " + Number(value.z) + ")";

        private enum OwnerBucket { Self, Server, Other, None }

        private sealed class SyncedCounts
        {
            internal int Count, Self, Server, Other, None, Bodies, Sleeping, Floating, InLiquid, Settled;
        }
    }
}
