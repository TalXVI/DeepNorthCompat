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
        private static FieldInfo previewPlayer = null!, characterAnimator = null!;

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
            CompatibilityInstaller.Info("Preview.AzuEPI: 2.6.3 build verified; awaiting vendor Harmony registrations.");
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
                previewPlayer = AccessTools.Field(Guard.Type(assembly, Namespace + "AzuEPICharacterPanel"), "playerPreviewComp");
                characterAnimator = AccessTools.Field(typeof(Character), "m_animator");
                if (previewPlayer?.FieldType != typeof(Player) || characterAnimator?.FieldType != typeof(Animator))
                    throw new MissingFieldException("AzuEPI preview stance fields changed.");

                // SetPreviewPose also copied weapon stance. Keep that part, without its pose reset or render.
                harmony.Patch(refresh, postfix: Guard.Hook(typeof(AzuEpiPreviewPatch), nameof(SyncStance), Priority.Last));
                foreach ((MethodInfo target, Patch patch) in hooks) harmony.Unpatch(target, patch.PatchMethod);
                CompatibilityInstaller.Info("Preview.AzuEPI: APPLIED; removed the two broad 2.6.3 equipment preview postfixes; weapon stance syncs through the local preview update.");
            }
            catch (Exception exception)
            {
                try
                {
                    foreach ((MethodInfo target, Patch patch) in hooks)
                        if (!AllPatches(target).Any(p => p.PatchMethod == patch.PatchMethod))
                            new Harmony(Owner).Patch(target, postfix: new HarmonyMethod(patch.PatchMethod,
                                patch.priority, patch.before, patch.after, patch.debug));
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

        private static void SyncStance(Humanoid __0)
        {
            if (!__0 || __0 != Player.m_localPlayer) return;
            var clone = previewPlayer.GetValue(null) as Player;
            if (!clone) return;
            var source = characterAnimator.GetValue(__0) as Animator;
            var preview = characterAnimator.GetValue(clone) as Animator;
            if (!source || !preview) return;

            int state = source.GetInteger("statei");
            float blend = source.GetFloat("statef");
            if (preview.GetInteger("statei") != state) preview.SetInteger("statei", state);
            if (preview.GetFloat("statef") != blend) preview.SetFloat("statef", blend);
        }

        // HarmonyX unpatches a method from every patch kind and owner, so the expected
        // postfix must be the only registration of that method on the target.
        private static Patch RequirePostfix(MethodInfo target, MethodInfo expected)
        {
            Patch[] matches = AllPatches(target).Where(p => p.PatchMethod == expected).ToArray();
            if (matches.Length != 1 || matches[0].owner != Owner || !Harmony.GetPatchInfo(target)!.Postfixes.Contains(matches[0]))
                throw new NotSupportedException($"Expected one {Owner} postfix {Guard.Name(expected)} on {Guard.Name(target)}.");
            return matches[0];
        }

        private static IEnumerable<Patch> AllPatches(MethodBase target)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            return info == null ? Enumerable.Empty<Patch>() : info.Prefixes.Concat(info.Postfixes)
                .Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators);
        }
    }
}
