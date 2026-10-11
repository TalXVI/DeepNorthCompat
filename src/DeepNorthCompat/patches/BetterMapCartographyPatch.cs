using System;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class BetterMapCartographyPatch
    {
        internal const string Owner = Plugin.Guid + ".Map.Cartography";
        private static BetterMapHooks? hooks;
        private static MethodInfo vendor = null!;
        private static FieldInfo config = null!;
        private static MethodInfo enabledGetter = null!, baseGetter = null!, stepGetter = null!;
        private static object? bound;
        private static Func<bool> skillEnabled = null!;
        private static Func<float> baseRadius = null!, radiusStep = null!;
        private static float seenFactor, seenBase, seenStep, seenRadius;
        private static Action calculate = null!;
        private static bool pending, enabled;

        internal static void Prepare(Assembly? betterMap, Assembly? cartography)
        {
            if (pending || enabled) return;
            if (cartography == null) { CompatibilityInstaller.Info("Map.Cartography: CartographySkill absent; inactive."); return; }
            BetterMapAccess? api = BetterMapAccess.Prepare(betterMap, "Map.Cartography", true);
            if (api == null) return;
            Guard.Build(cartography, ExpectedBuilds.Cartography);
            Type type = Guard.Type(cartography, "Advize_CartographySkill.CartographySkill");
            config = BetterMapAccess.RequiredField(type, "config", true);
            enabledGetter = Guard.Method(config.FieldType, "get_EnableSkill", typeof(bool));
            baseGetter = Guard.Method(config.FieldType, "get_BaseExploreRadius", typeof(float));
            stepGetter = Guard.Method(config.FieldType, "get_ExploreRadiusIncrease", typeof(float));
            bound = null; seenRadius = float.NaN;
            calculate = (Action)Delegate.CreateDelegate(typeof(Action), Guard.Method(type, "UpdateExploreRadius", typeof(void)));
            vendor = api.Method("Map.MapPatches", "Minimap_UpdateExplore_Prefix", typeof(void), typeof(Minimap));
            hooks = new BetterMapHooks(Owner);
            hooks.Add(vendor, typeof(BetterMapCartographyPatch), nameof(Explore), HarmonyPatchType.Prefix);
            pending = true;
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            enabled = hooks!.Complete("Map.Cartography", BetterMapHooks.Vendor(
                Guard.Method(typeof(Minimap), "UpdateExplore", typeof(void), typeof(float), typeof(Player)), vendor, HarmonyPatchType.Prefix),
                "current CartographySkill radius replaces the fixed radius; tile XP and vanilla exploration retained.");
        }
        private static bool Explore()
        {
            Player player = Player.m_localPlayer; Minimap map = Minimap.instance;
            if (!enabled || player == null || map == null) return true;
            object settings = config.GetValue(null);
            if (settings == null) return true;
            if (!ReferenceEquals(settings, bound))
            {
                skillEnabled = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), settings, enabledGetter);
                baseRadius = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), settings, baseGetter);
                radiusStep = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), settings, stepGetter);
                bound = settings; seenRadius = float.NaN;
            }
            if (!skillEnabled()) return true;
            // CartographySkill formats a debug string on every calculation, so calculate only when an
            // input changed or something else overwrote the radius. NaN never compares equal.
            float factor = player.GetSkillFactor((Skills.SkillType)1337), radius = baseRadius(), step = radiusStep();
            if (factor != seenFactor || radius != seenBase || step != seenStep || map.m_exploreRadius != seenRadius)
            {
                calculate();
                seenFactor = factor; seenBase = radius; seenStep = step; seenRadius = map.m_exploreRadius;
            }
            return false;
        }
    }
}
