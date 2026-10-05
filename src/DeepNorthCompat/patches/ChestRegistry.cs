using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace DeepNorthCompat
{
    // Compares Unity objects by identity. Unity's equality treats destroyed objects as
    // null and compares instance IDs, which is not a stable dictionary key.
    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    // Tracks loaded chests for the chest synchronization, crafting handoff and diagnostics
    // groups. ZNetScene resets a view's ZDO before destroying it, so lifetime is keyed on
    // the view and the chest ID is remembered from load time.
    internal static class ChestRegistry
    {
        internal const string Owner = Plugin.Guid + ".Chests.Registry";
        private static readonly FieldInfo view = AccessTools.Field(typeof(Container), "m_nview");
        private static readonly FieldInfo lastRevision = AccessTools.Field(typeof(Container), "m_lastRevision");
        private static readonly MethodInfo load = AccessTools.Method(typeof(Container), "Load");
        private static readonly Dictionary<ZNetView, Container> views = new Dictionary<ZNetView, Container>(ReferenceComparer<ZNetView>.Instance);
        private static readonly Dictionary<Container, ZDOID> ids = new Dictionary<Container, ZDOID>(ReferenceComparer<Container>.Instance);
        private static readonly Dictionary<ZDOID, Container> containers = new Dictionary<ZDOID, Container>();
        private static readonly Dictionary<Inventory, Container> inventories = new Dictionary<Inventory, Container>();
        private static readonly Dictionary<ZDOID, (uint Revision, string Hash)> hashes = new Dictionary<ZDOID, (uint, string)>();
        internal static bool Installed { get; private set; }

        internal static void Install(Assembly? crafty)
        {
            Guard.Build(typeof(Container).Assembly, new[] { ExpectedBuilds.Valheim }.Concat(SimulationPatch.ServerHashes).ToArray());
            ChestCraftPatch.PrepareOwner(crafty);
            var harmony = new Harmony(Owner);
            harmony.Patch(Guard.Method(typeof(Container), "Awake", typeof(void)), postfix: Guard.Hook(typeof(ChestRegistry), nameof(Track), Priority.Last));
            harmony.Patch(Guard.Method(typeof(ZNetView), "OnDestroy", typeof(void)), prefix: Guard.Hook(typeof(ChestRegistry), nameof(Forget)));
            harmony.Patch(Guard.Method(typeof(ZNet), "Shutdown", typeof(void), typeof(bool)), postfix: Guard.Hook(typeof(ChestRegistry), nameof(Reset)));
            Installed = true;
            CompatibilityInstaller.Info("Chests.Registry: APPLIED; loaded chests are tracked and this peer answers crafting handoff requests.");
        }

        internal static ZNetView? View(Container? chest) => chest == null ? null : (ZNetView?)view.GetValue(chest);
        // Null once the scene has reset the view, even before Unity destroys the object.
        internal static ZDO? LiveZdo(Container? chest) => View(chest)?.GetZDO();
        internal static ZDOID? IdOf(Container? chest) => chest is object && ids.TryGetValue(chest, out ZDOID id) ? id : LiveZdo(chest)?.m_uid;
        internal static uint LoadedRevision(Container chest) => (uint)lastRevision.GetValue(chest);
        internal static void SetLoadedRevision(Container chest, uint revision) => lastRevision.SetValue(chest, revision);
        internal static Container? Find(ZDOID id) => containers.TryGetValue(id, out Container chest) && LiveZdo(chest) != null ? chest : null;
        internal static Container? Find(Inventory inventory) => inventories.TryGetValue(inventory, out Container chest) && LiveZdo(chest) != null ? chest : null;
        internal static Container[] Containers => containers.Values.Where(chest => LiveZdo(chest) != null).ToArray();
        internal static int Count => containers.Count;

        internal static void Refresh(Container chest)
        {
            ZNetView? nview = View(chest);
            if (nview != null && nview.IsValid()) load.Invoke(chest, null);
        }

        // SHA-256 of the replicated inventory payload. Owners include it in handoff replies so
        // the requester can prove it consumes the same inventory the owner approved.
        internal static string PayloadHash(ZDO zdo)
        {
            if (hashes.TryGetValue(zdo.m_uid, out var cached) && cached.Revision == zdo.DataRevision) return cached.Hash;
            byte[]? payload = zdo.GetByteArray(ZDOVars.s_items);
            string hash = "";
            if (payload != null)
                using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
            if (hashes.Count > 4096) hashes.Clear();
            hashes[zdo.m_uid] = (zdo.DataRevision, hash);
            return hash;
        }

        internal static void Track(Container __instance)
        {
            ZNetView? nview = View(__instance);
            if (nview == null || !nview.IsValid() || __instance.GetInventory() == null || views.ContainsKey(nview)) return;
            ZDOID id = nview.GetZDO().m_uid;
            views[nview] = __instance; ids[__instance] = id; containers[id] = __instance; inventories[__instance.GetInventory()] = __instance;
            ChestCraftPatch.Register(__instance, nview);
            ChestDiagnostics.Record("container-loaded", __instance);
        }

        private static void Forget(ZNetView __instance)
        {
            if (!views.TryGetValue(__instance, out Container chest)) return;
            ZDOID id = ids[chest];
            ChestDiagnostics.Record("container-unloaded", chest);
            views.Remove(__instance); ids.Remove(chest); inventories.Remove(chest.GetInventory()); hashes.Remove(id);
            if (containers.TryGetValue(id, out Container current) && ReferenceEquals(current, chest)) containers.Remove(id);
            DiagnosticsPatch.Forget(id);
        }

        internal static void Reset()
        {
            views.Clear(); ids.Clear(); containers.Clear(); inventories.Clear(); hashes.Clear();
            ChestCraftPatch.Reset(); DiagnosticsPatch.Reset();
        }
    }
}
