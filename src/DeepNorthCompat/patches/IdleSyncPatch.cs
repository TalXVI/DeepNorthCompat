using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    // Skip only after vanilla has converged. Revision and owner checks catch new data or handoffs;
    // exact targets and cached pose catch writes that do not bump the revision; sleeping bodies
    // without gravity cannot move before the next check. Other sync modes keep vanilla's extra writes.
    internal static class IdleSyncPatch
    {
        internal const string Owner = Plugin.Guid + ".Performance.IdleSync";
        private static readonly Dictionary<ZSyncTransform, Rest> rest = new Dictionary<ZSyncTransform, Rest>(new SyncComparer());
        private static AccessTools.FieldRef<ZSyncTransform, ZNetView> netView = null!;
        private static AccessTools.FieldRef<ZSyncTransform, Rigidbody> body = null!;
        private static AccessTools.FieldRef<ZSyncTransform, bool> isKinematicBody = null!;
        private static AccessTools.FieldRef<ZSyncTransform, float> targetPosTimer = null!;
        private static MethodInfo clientSync = null!, onDisable = null!, skip = null!, settle = null!, forget = null!;
        private static bool pending, enabled;

        internal static long Checked, Skipped;
        internal static bool Active => enabled;
        internal static int Settled => rest.Count;

        internal static void Prepare()
        {
            pending = enabled = false;
            new Harmony(Owner).UnpatchSelf();
            Clear();

            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info("Performance.IdleSync: client-only patch; inactive on dedicated server.");
                return;
            }

            Guard.Build(typeof(ZSyncTransform).Assembly, ExpectedBuilds.Valheim1020Client);
            clientSync = Guard.Method(typeof(ZSyncTransform), "ClientSync", typeof(void), typeof(float));
            onDisable = Guard.Method(typeof(ZSyncTransform), "OnDisable", typeof(void));
            netView = AccessTools.FieldRefAccess<ZSyncTransform, ZNetView>(Field("m_nview", typeof(ZNetView)));
            body = AccessTools.FieldRefAccess<ZSyncTransform, Rigidbody>(Field("m_body", typeof(Rigidbody)));
            isKinematicBody = AccessTools.FieldRefAccess<ZSyncTransform, bool>(Field("m_isKinematicBody", typeof(bool)));
            targetPosTimer = AccessTools.FieldRefAccess<ZSyncTransform, float>(Field("m_targetPosTimer", typeof(float)));
            skip = AccessTools.DeclaredMethod(typeof(IdleSyncPatch), nameof(Skip));
            settle = AccessTools.DeclaredMethod(typeof(IdleSyncPatch), nameof(Settle));
            forget = AccessTools.DeclaredMethod(typeof(IdleSyncPatch), nameof(Forget));

            var harmony = new Harmony(Owner);
            harmony.Patch(clientSync, prefix: Guard.Hook(typeof(IdleSyncPatch), nameof(Skip)),
                postfix: Guard.Hook(typeof(IdleSyncPatch), nameof(Settle)));
            harmony.Patch(onDisable, postfix: Guard.Hook(typeof(IdleSyncPatch), nameof(Forget)));
            Guard.Registered(clientSync);
            Guard.Registered(onDisable);
            pending = true;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;

            string? failure = null;
            if (!Exactly(Harmony.GetPatchInfo(clientSync)?.Prefixes, skip))
                failure = "ClientSync prefix missing or changed";
            else if (!Exactly(Harmony.GetPatchInfo(clientSync)?.Postfixes, settle))
                failure = "ClientSync postfix missing or changed";
            else if (!Exactly(Harmony.GetPatchInfo(onDisable)?.Postfixes, forget))
                failure = "OnDisable postfix missing or changed";

            if (failure != null)
            {
                new Harmony(Owner).UnpatchSelf();
                Clear();
                CompatibilityInstaller.Error("Performance.IdleSync: NOT APPLIED; " + failure + "; vanilla sync retained.");
                return;
            }

            enabled = true;
            CompatibilityInstaller.Info("Performance.IdleSync: APPLIED; remote physics objects at rest skip their unchanged sync step; new network data, local movement or a wake runs the vanilla sync.");
        }

        internal static bool IsSettled(ZSyncTransform sync) => rest.ContainsKey(sync);

        // True when vanilla's next ClientSync pass would write nothing: thresholds match SyncPosition and ClientSync.
        internal static bool Converged(bool syncPosition, Vector3 position, Vector3 target, Vector3 velocity,
            float timer, bool syncRotation, Quaternion rotation, Quaternion targetRotation)
        {
            if (syncPosition)
            {
                if (!velocity.Equals(Vector3.zero) && timer < 2f) return false;
                if (Vector3.Distance(position, target + velocity * Mathf.Min(timer, 2f)) > 0.001f) return false;
            }
            return !syncRotation || Quaternion.Angle(rotation, targetRotation) <= 0.001f;
        }

        private static bool Skip(ZSyncTransform __instance, out bool __state)
        {
            __state = false;
            if (!enabled) return true;
            Checked++;
            if (!rest.TryGetValue(__instance, out Rest? state)) return true;

            ZNetView view = netView(__instance);
            if (!view)
            {
                rest.Remove(__instance);
                return true;
            }

            ZDO? zdo = view.GetZDO();
            if (zdo == null || !ReferenceEquals(zdo, state.Zdo) || zdo.IsOwner()
                || zdo.DataRevision != state.Revision || zdo.HasOwner() != state.HadOwner
                || !zdo.GetPosition().Equals(state.Target))
            {
                rest.Remove(__instance);
                return true;
            }

            Rigidbody rigidbody = body(__instance);
            if (rigidbody && (!rigidbody.IsSleeping() || rigidbody.useGravity))
            {
                rest.Remove(__instance);
                return true;
            }

            if (!state.Transform.position.Equals(state.Position) || !state.Transform.rotation.Equals(state.Rotation))
            {
                rest.Remove(__instance);
                return true;
            }

            Skipped++;
            __state = true;
            return false;
        }

        private static void Settle(ZSyncTransform __instance, bool __state)
        {
            if (!enabled || __state) return;

            if (isKinematicBody(__instance) || __instance.m_characterParentSync || __instance.m_syncScale
                || __instance.m_syncBodyVelocity)
            {
                rest.Remove(__instance);
                return;
            }

            ZNetView view = netView(__instance);
            if (!view)
            {
                rest.Remove(__instance);
                return;
            }

            ZDO? zdo = view.GetZDO();
            if (zdo == null || zdo.IsOwner())
            {
                rest.Remove(__instance);
                return;
            }

            Rigidbody rigidbody = body(__instance);
            if (rigidbody && (!rigidbody.IsSleeping() || rigidbody.useGravity))
            {
                rest.Remove(__instance);
                return;
            }

            bool hasOwner = zdo.HasOwner();
            Vector3 velocity = hasOwner ? zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero) : Vector3.zero;
            Transform transform = __instance.transform;
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            Vector3 target = zdo.GetPosition();
            Quaternion targetRotation = zdo.GetRotation();
            float timer = targetPosTimer(__instance);

            if (!Converged(__instance.m_syncPosition, position, target, velocity, timer,
                __instance.m_syncRotation, rotation, targetRotation))
            {
                rest.Remove(__instance);
                return;
            }

            if (!rest.TryGetValue(__instance, out Rest? state))
            {
                state = new Rest();
                rest.Add(__instance, state);
            }
            state.Zdo = zdo;
            state.Revision = zdo.DataRevision;
            state.HadOwner = hasOwner;
            state.Target = target;
            state.Position = position;
            state.Rotation = rotation;
            state.Transform = transform;
        }

        private static void Forget(ZSyncTransform __instance) => rest.Remove(__instance);

        private static FieldInfo Field(string name, Type fieldType)
        {
            FieldInfo? field = AccessTools.DeclaredField(typeof(ZSyncTransform), name);
            if (field == null || field.IsStatic || field.FieldType != fieldType)
                throw new MissingFieldException(typeof(ZSyncTransform).FullName, name);
            return field;
        }

        private static bool Exactly(IEnumerable<Patch>? patches, MethodInfo expected)
        {
            if (patches == null) return false;
            int ownerCount = 0, expectedCount = 0;
            foreach (Patch patch in patches)
            {
                if (patch.owner != Owner) continue;
                ownerCount++;
                if (patch.PatchMethod == expected) expectedCount++;
            }
            return ownerCount == 1 && expectedCount == 1;
        }

        private static void Clear()
        {
            rest.Clear();
            Checked = Skipped = 0;
        }

        private sealed class Rest
        {
            internal ZDO Zdo = null!;
            internal uint Revision;
            internal bool HadOwner;
            internal Vector3 Target;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Transform Transform = null!;
        }

        private sealed class SyncComparer : IEqualityComparer<ZSyncTransform>
        {
            public bool Equals(ZSyncTransform? left, ZSyncTransform? right) => ReferenceEquals(left, right);
            public int GetHashCode(ZSyncTransform sync) => RuntimeHelpers.GetHashCode(sync);
        }
    }
}
