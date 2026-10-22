using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class AzuEpiPreviewPatch
    {
        internal const string Owner = "Azumatt.AzuExtendedPlayerInventory";
        private const string Namespace = "AzuEPI.Game.PlayerPreview.";
        private const string PatchOwner = Plugin.Guid + ".Preview.AzuEPI";
        private static Assembly? pending;
        private static AccessTools.FieldRef<Player> previewPlayer = null!;
        private static AccessTools.FieldRef<Character, Animator> characterAnimator = null!;

        internal static void Prepare(Assembly? assembly)
        {
            pending = null;
            if (assembly == null)
            {
                CompatibilityInstaller.Info("Preview.AzuEPI: optional mod absent; inactive.");
                return;
            }

            Guard.Build(assembly, ExpectedBuilds.AzuEpi);
            Guard.Build(typeof(Humanoid).Assembly, ExpectedBuilds.Valheim);
            // StartupAccelerator can defer AzuEPI's Harmony registrations until chainloading
            // finishes. Inspect and remove them in Start, after that batch has completed.
            pending = assembly;
            CompatibilityInstaller.Info("Preview.AzuEPI: audited build verified; awaiting vendor Harmony registrations.");
        }

        internal static void Verify()
        {
            Assembly? assembly = pending;
            pending = null;
            if (assembly == null) return;
            var harmony = new Harmony(PatchOwner);
            var hooks = new List<(MethodInfo Target, Patch Patch)>();
            try
            {
                foreach ((string name, Type result) in new[] { ("EquipItem", typeof(bool)), ("UnequipItem", typeof(void)) })
                {
                    MethodInfo target = Guard.Method(typeof(Humanoid), name, result, typeof(ItemDrop.ItemData), typeof(bool));
                    MethodInfo postfix = Guard.Method(Guard.Type(assembly, Namespace + "PreviewInstantRefresh_" + name),
                        "Postfix", typeof(void));
                    hooks.Add((target, RequirePostfix(target, postfix)));
                }

                // These existing local-player paths keep the preview current once the broad hooks are gone.
                RequirePostfix(Guard.Method(typeof(VisEquipment), "UpdateEquipmentVisuals", typeof(void)),
                    Guard.Method(Guard.Type(assembly, Namespace + "PreviewInstantRefresh_VisEquipmentPatch"),
                        "Postfix", typeof(void), typeof(VisEquipment)));
                RequirePostfix(Guard.Method(typeof(Humanoid), "SetupAnimationState", typeof(void)),
                    Guard.Method(Guard.Type(assembly, Namespace + "UpdatePlayerPreviewVisuals"),
                        "Postfix", typeof(void), typeof(Humanoid)));
                MethodInfo refresh = Guard.Method(Guard.Type(assembly, Namespace + "PlayerPreviewManager"),
                    "UpdatePlayerPreview", typeof(void), typeof(Humanoid));
                FieldInfo preview = AccessTools.Field(Guard.Type(assembly, Namespace + "AzuEPICharacterPanel"), "playerPreviewComp");
                FieldInfo animator = AccessTools.Field(typeof(Character), "m_animator");
                if (preview?.FieldType != typeof(Player) || animator?.FieldType != typeof(Animator))
                    throw new MissingFieldException("AzuEPI preview stance fields changed.");
                previewPlayer = AccessTools.StaticFieldRefAccess<Player>(preview);
                characterAnimator = AccessTools.FieldRefAccess<Character, Animator>(animator);

                // SetPreviewPose also copied weapon stance. Keep that part, without its pose reset or render.
                harmony.Patch(refresh, postfix: Guard.Hook(typeof(AzuEpiPreviewPatch), nameof(SyncStance), Priority.Last));
                foreach ((MethodInfo target, Patch patch) in hooks) harmony.Unpatch(target, patch.PatchMethod);
                CompatibilityInstaller.Info("Preview.AzuEPI: APPLIED; removed the two broad equipment preview postfixes; weapon stance syncs through the local preview update.");
            }
            catch (Exception exception)
            {
                try
                {
                    foreach ((MethodInfo target, Patch patch) in hooks) Guard.Restore(target, patch, HarmonyPatchType.Postfix);
                    harmony.UnpatchSelf();
                }
                catch (Exception rollback)
                {
                    CompatibilityInstaller.Error($"Preview.AzuEPI: application and rollback failed; inspect Harmony state. {exception} {rollback}");
                    return;
                }
                CompatibilityInstaller.Error($"Preview.AzuEPI: NOT APPLIED; vendor hooks retained. {exception}");
            }
        }

        private static Patch RequirePostfix(MethodInfo target, MethodInfo expected) =>
            Guard.Sole(target, expected, Owner, HarmonyPatchType.Postfix);

        private static void SyncStance(Humanoid __0)
        {
            if (!__0 || __0 != Player.m_localPlayer) return;
            Player clone = previewPlayer();
            if (!clone) return;
            Animator source = characterAnimator(__0);
            Animator preview = characterAnimator(clone);
            if (!source || !preview) return;

            int state = source.GetInteger("statei");
            float blend = source.GetFloat("statef");
            if (preview.GetInteger("statei") != state) preview.SetInteger("statei", state);
            if (preview.GetFloat("statef") != blend) preview.SetFloat("statef", blend);
        }
    }
}
