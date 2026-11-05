using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class UsefulPathsPatch
    {
        internal const string Guid = "RustyMods.UsefulPaths";
        private const string Impact = "MidnightsFX.ImpactfulSkills";
        private const string VendorHash = "BFBA84D4AEA9860F48265321C9BE07B2602F8C735CAAA57EB60B662A511EFCE3";
        private const string ImpactHash = "19B9E4268C68AF645A2812574797EA9579DFBF5E1B3D8AD9FCA3A23F07DF8164";
        private const string CartOwner = Plugin.Guid + ".UsefulPaths.Cart";
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(UsefulPathsPatch), nameof(RewriteEffects));
        private static readonly MethodInfo notDue = AccessTools.DeclaredMethod(typeof(UsefulPathsPatch), nameof(NotDue));
        private static readonly MethodInfo order = AccessTools.DeclaredMethod(typeof(UsefulPathsPatch), nameof(OrderCart));
        private static readonly TranspilerGroup effects = new TranspilerGroup("UsefulPaths.Effects", rewrite,
            "effect updates have independent per-character intervals.");
        private static readonly ConditionalWeakTable<Character, Clock> clocks = new ConditionalWeakTable<Character, Clock>();
        private static readonly ConditionalWeakTable<Character, Clock>.CreateValueCallback makeClock = _ => new Clock();
        private static readonly string[] gameBuilds =
        {
            ExpectedBuilds.Valheim1021Client, ExpectedBuilds.Valheim1021WindowsServer, ExpectedBuilds.Valheim1021LinuxServer
        };
        private static FieldInfo timer = null!;
        private static MethodInfo tick = null!, vendorTick = null!, interval = null!, enabled = null!;
        private static Func<float> updateInterval = null!;
        private static MethodInfo cart = null!, vendorCart = null!, hauling = null!;
        private static bool cartPending;

        private sealed class Clock { internal float Elapsed; }

        internal static void PrepareEffects(Assembly? vendor)
        {
            if (effects.Installed) return;
            if (vendor == null) { CompatibilityInstaller.Info("UsefulPaths.Effects: optional mod absent; inactive."); return; }
            Guard.Build(vendor, VendorHash);
            Guard.Build(typeof(Character).Assembly, gameBuilds);
            Type patch = Guard.Type(vendor, "UsefulPaths.Managers.UsefulPaths+Character_CustomFixedUpdate_Patch");
            Type plugin = Guard.Type(vendor, "UsefulPaths.UsefulPathsPlugin");
            tick = Guard.Method(typeof(Character), "CustomFixedUpdate", typeof(void), typeof(float));
            vendorTick = Guard.Method(patch, "Postfix", typeof(void), typeof(Character), typeof(float));
            timer = AccessTools.DeclaredField(patch, "m_timer");
            if (timer == null || !timer.IsStatic || timer.FieldType != typeof(float)) throw new MissingFieldException(patch.FullName, "m_timer");
            interval = Guard.Method(plugin, "get_UpdateInterval", typeof(float));
            enabled = Guard.Method(plugin, "get_Enabled", typeof(bool));
            updateInterval = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), interval);
            effects.Patch(vendorTick);
        }

        internal static void VerifyEffects()
        {
            if (!effects.Installed) return;
            try { Guard.Sole(tick, vendorTick, Guid, HarmonyPatchType.Postfix); }
            catch (Exception exception)
            {
                new Harmony(effects.Owner).UnpatchSelf();
                CompatibilityInstaller.Error($"UsefulPaths.Effects: NOT APPLIED; vendor behavior retained. {exception}");
                return;
            }
            effects.Verify();
        }

        internal static IEnumerable<CodeInstruction> RewriteEffects(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            effects.Transpile(instructions, "the effect timer no longer matches", code =>
            {
                bool StoreTimer(CodeInstruction instruction) => instruction.opcode == OpCodes.Stsfld && Equals(instruction.operand, timer);
                bool Zero(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == 0f;
                int site = Unique(code, i => Shape(code, i,
                    Loads(timer), Is(OpCodes.Ldarg_1), Is(OpCodes.Add), StoreTimer, Loads(timer), Calls(interval),
                    Is(OpCodes.Clt), IsStore(2), IsLoad(2), Is(OpCodes.Brfalse), Is(OpCodes.Br), Zero, StoreTimer, Calls(enabled)));
                if (site < 0 || !Locals(__originalMethod, 7, (2, typeof(bool)))
                    || code.Count(i => i.LoadsField(timer)) != 2 || code.Count(StoreTimer) != 2
                    || code.Skip(site + 1).Take(10).Any(i => !Plain(i)) || !Plain(code[site + 12])
                    || code[site].blocks.Count != 0 || code[site + 11].blocks.Count != 0
                    || !(code[site + 9].operand is Label due) || !code[site + 11].labels.Contains(due)
                    || !(code[site + 10].operand is Label exit) || code.Count(i => i.labels.Contains(exit) && i.opcode == OpCodes.Ret) != 1)
                    return false;

                code[site].opcode = OpCodes.Ldarg_0; code[site].operand = null;
                code[site + 1].opcode = OpCodes.Ldarg_1; code[site + 1].operand = null;
                code[site + 2].opcode = OpCodes.Call; code[site + 2].operand = notDue;
                foreach (int index in new[] { site + 3, site + 4, site + 5, site + 6, site + 11, site + 12 })
                {
                    code[index].opcode = OpCodes.Nop;
                    code[index].operand = null;
                }
                return true;
            });

        private static bool NotDue(Character character, float dt)
        {
            Clock clock = clocks.GetValue(character, makeClock);
            clock.Elapsed += dt;
            if (clock.Elapsed < updateInterval()) return true;
            clock.Elapsed = 0f;
            return false;
        }

        internal static void PrepareCart(Assembly? vendor, Assembly? impact)
        {
            if (cartPending || Harmony.HasAnyPatches(CartOwner)) return;
            if (vendor == null || impact == null) { CompatibilityInstaller.Info("UsefulPaths.Cart: optional mod absent; inactive."); return; }
            Guard.Build(vendor, VendorHash);
            Guard.Build(impact, ImpactHash);
            Guard.Build(typeof(Vagon).Assembly, gameBuilds);
            cart = Guard.Method(typeof(Vagon), "SetMass", typeof(void), typeof(float));
            vendorCart = Guard.Method(Guard.Type(vendor, "UsefulPaths.Managers.UsefulPaths+Vagon_SetMass_Patch"), "Prefix",
                typeof(void), typeof(Vagon), typeof(float).MakeByRefType());
            hauling = Guard.Method(Guard.Type(impact, "ImpactfulSkills.patches.Hauling+VagonMassPatch"), "Prefix",
                typeof(void), typeof(float).MakeByRefType());
            cartPending = true;
        }

        internal static void VerifyCart()
        {
            if (!cartPending) return;
            cartPending = false;
            var harmony = new Harmony(CartOwner);
            try
            {
                Guard.Sole(cart, vendorCart, Guid, HarmonyPatchType.Prefix);
                Guard.Sole(cart, hauling, Impact, HarmonyPatchType.Prefix);
                harmony.Patch(cart, prefix: new HarmonyMethod(order) { after = new[] { Guid }, before = new[] { Impact } });
                Patches patches = Harmony.GetPatchInfo(cart);
                if (patches.Prefixes.Count(p => p.owner == CartOwner && p.PatchMethod == order) != 1)
                    throw new NotSupportedException("The cart ordering prefix is not registered exactly once.");
                List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(cart, patches.Prefixes.ToArray());
                if (sorted.IndexOf(vendorCart) < 0 || sorted.IndexOf(hauling) < 0 || sorted.IndexOf(vendorCart) >= sorted.IndexOf(hauling))
                    throw new NotSupportedException("Harmony did not place UsefulPaths before Hauling.");
                CompatibilityInstaller.Info("UsefulPaths.Cart: APPLIED; path mass is calculated before the Hauling reduction.");
            }
            catch (Exception exception)
            {
                harmony.UnpatchSelf();
                CompatibilityInstaller.Error($"UsefulPaths.Cart: NOT APPLIED; vendor behavior retained. {exception}");
            }
        }

        // This prefix only creates an ordering edge; the two vendors keep their own hooks and formulas.
        private static void OrderCart() { }
    }
}
