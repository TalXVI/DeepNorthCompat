using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class RuneIcePatch
    {
        internal const string Owner = Plugin.Guid + ".RuneMagic.Ice";
        private const float InitialScale = 0.001f;
        private static MethodInfo awake = null!;
        private static FieldInfo maxScale = null!;
        private static bool pending;

        internal static void Prepare(Assembly? rune)
        {
            pending = false;
            new Harmony(Owner).UnpatchSelf();
            if (rune == null) { CompatibilityInstaller.Info("RuneMagic.Ice: optional mod absent; inactive."); return; }
            Guard.Build(rune, HearthTerrainPatch.RuneBuild);
            Guard.Build(typeof(ZNet).Assembly,
                "94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A",
                "EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3",
                "C36B81C1733C48D00DD567E41010E6DC95F8DAC877E0701CDB6B8C213ADE7812");
            Type ice = Guard.Type(rune, "ValheimMod.Monobehaviours.IceTimedDestruction");
            awake = Guard.Method(ice, "Awake", typeof(void));
            maxScale = AccessTools.DeclaredField(ice, "maxScale");
            if (maxScale == null || maxScale.IsStatic || maxScale.FieldType != typeof(Vector3))
                throw new MissingFieldException(ice.FullName, "maxScale");
            new Harmony(Owner).Patch(awake, postfix: Guard.Hook(typeof(RuneIcePatch), nameof(Initialize)));
            Guard.Registered(awake);
            pending = true;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (Harmony.GetPatchInfo(awake)?.Postfixes.Count(p => p.owner == Owner
                && p.PatchMethod == AccessTools.DeclaredMethod(typeof(RuneIcePatch), nameof(Initialize))) != 1)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("RuneMagic.Ice: NOT APPLIED; ice initialization hook missing; vendor behavior retained.");
                return;
            }
            CompatibilityInstaller.Info("RuneMagic.Ice: APPLIED; ice starts at a nonzero physics scale; growth and decay remain vendor-owned.");
        }

        // Awake's zero scale leaves a dynamic convex collider with degenerate mass properties for
        // its first physics step. Start at 0.1% size; the vendor's zero growth factor is unchanged.
        private static void Initialize(MonoBehaviour __instance) =>
            __instance.transform.localScale = (Vector3)maxScale.GetValue(__instance) * InitialScale;
    }
}
