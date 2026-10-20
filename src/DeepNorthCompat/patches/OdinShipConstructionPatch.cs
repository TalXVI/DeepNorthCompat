using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeepNorthCompat
{
    // OdinShip 0.8.7's decorative ShipConstruction prefab carries four persistent world-location TerrainModifiers.
    // The game applies them before saved terrain edits, so raised ground is counted twice and an underground
    // placement lowers the surface above a cave. Its hull MeshCollider also has no mesh and its supports have
    // no colliders, so nothing collides with it and the hammer cannot remove it. The repair edits the loaded
    // prefab once, before any world instance or placement ghost exists, on every peer.
    internal static class OdinShipConstructionPatch
    {
        internal const string Prefab = "ShipConstruction";
        private const int Supports = 6;
        private static bool pending, enabled;
        private static IList? registered;
        private static FieldInfo prefabField = null!;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("OdinShip.Construction: optional mod absent; inactive."); return; }
            OdinShipPatch.CheckBuild(assembly);
            // BuildPiece's static initializers only create collections. PiecePrefabManager's would install
            // PieceManager's Harmony hooks if this read ran first.
            Type piece = Guard.Type(assembly, "PieceManager.BuildPiece");
            FieldInfo? list = AccessTools.DeclaredField(piece, "registeredPieces");
            prefabField = AccessTools.DeclaredField(piece, "Prefab");
            if (list == null || !list.IsStatic || list.FieldType != typeof(List<>).MakeGenericType(piece)) throw new MissingFieldException(piece.FullName, "registeredPieces");
            if (prefabField == null || prefabField.IsStatic || prefabField.FieldType != typeof(GameObject)) throw new MissingFieldException(piece.FullName, "Prefab");
            registered = (IList)list.GetValue(null);
            if (TryRepair()) return;
            pending = true;
            CompatibilityInstaller.Info("OdinShip.Construction: 0.8.7 build verified; awaiting prefab registration.");
        }

        // StartupAccelerator or a later vendor Awake can register the prefab after this plugin's Awake.
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            try
            {
                if (TryRepair()) return;
                CompatibilityInstaller.Error("OdinShip.Construction: NOT APPLIED; ShipConstruction prefab was not registered; vendor prefab retained.");
            }
            catch (Exception exception)
            {
                CompatibilityInstaller.Error($"OdinShip.Construction: NOT APPLIED; audited prefab changed; vendor prefab retained. {exception}");
            }
            registered = null;
        }

        private static bool TryRepair()
        {
            GameObject[] matches = registered!.Cast<object>().Select(p => (GameObject)prefabField.GetValue(p))
                .Where(p => p != null && p.name == Prefab).ToArray();
            if (matches.Length == 0) return false;
            if (matches.Length != 1) throw new InvalidOperationException($"Expected one {Prefab} prefab, found {matches.Length}.");
            Repair(matches[0]);
            enabled = true; registered = null;
            CompatibilityInstaller.Info("OdinShip.Construction: APPLIED; decorative ship construction no longer reshapes terrain, and its hull and supports collide.");
            return true;
        }

        // Validates the whole audited hierarchy before changing anything, so an unexpected prefab is left intact.
        internal static void Repair(GameObject prefab)
        {
            Transform root = prefab.transform;
            if (prefab.GetComponent<Piece>() == null || prefab.GetComponent<ZNetView>() == null)
                throw new InvalidOperationException($"{Prefab} lacks its Piece or ZNetView.");

            Transform level = Child(root, "level");
            TerrainModifier[] modifiers = prefab.GetComponentsInChildren<TerrainModifier>(true);
            if (level.childCount != 4 || modifiers.Length != 4 || modifiers.Any(m => m.transform.parent != level)
                || level.GetComponents<Component>().Length != 1
                || modifiers.Any(m => m.GetComponents<Component>().Length != 2 || m.m_useTerrainCompiler || m.m_playerModifiction || !m.m_level || !m.m_smooth))
                throw new InvalidOperationException($"{Prefab} terrain modifiers differ from the audited prefab.");

            Transform hidden = Child(root, "ToHide");
            Transform hull = Child(hidden, "hull");
            MeshCollider hullCollider = hull.GetComponent<MeshCollider>();
            MeshFilter hullFilter = hull.GetComponent<MeshFilter>();
            if (hullCollider == null || hullCollider.sharedMesh != null || hullCollider.isTrigger || hullCollider.convex
                || hullFilter == null || hullFilter.sharedMesh == null || !hullFilter.sharedMesh.isReadable)
                throw new InvalidOperationException($"{Prefab} hull collider differs from the audited prefab.");

            Transform volume = Child(hidden, "GameObject");
            if (volume.gameObject.activeSelf || volume.GetComponent<BoxCollider>() == null)
                throw new InvalidOperationException($"{Prefab} inactive volume differs from the audited prefab.");

            var supports = new List<MeshFilter>();
            for (int i = 1; i <= Supports; i++)
            {
                MeshFilter filter = Child(hidden, $"wood_pole ({i})").GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || filter.GetComponent<Collider>() != null)
                    throw new InvalidOperationException($"{Prefab} support {i} differs from the audited prefab.");
                supports.Add(filter);
            }
            if (prefab.GetComponentsInChildren<Collider>(true).Length != 2)
                throw new InvalidOperationException($"{Prefab} collider set differs from the audited prefab.");

            // The large box stays inactive: it would fill the open space inside and beneath the hull.
            Object.DestroyImmediate(level.gameObject, true);
            hullCollider.sharedMesh = hullFilter.sharedMesh;
            foreach (MeshFilter filter in supports)
            {
                Bounds bounds = filter.sharedMesh.bounds;
                BoxCollider box = filter.gameObject.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = bounds.size;
            }
        }

        private static Transform Child(Transform parent, string name)
        {
            Transform[] found = Enumerable.Range(0, parent.childCount).Select(parent.GetChild).Where(c => c.name == name).ToArray();
            if (found.Length != 1) throw new InvalidOperationException($"{Prefab} expected one child '{name}' under '{parent.name}', found {found.Length}.");
            return found[0];
        }
    }
}
