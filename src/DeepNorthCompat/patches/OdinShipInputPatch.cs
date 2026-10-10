using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class OdinShipInputPatch
    {
        internal const string Owner = Plugin.Guid + ".OdinShip.Input";
        private static readonly KeyCode[] keys = { KeyCode.G, KeyCode.H, KeyCode.J, KeyCode.L, KeyCode.K, KeyCode.T };
        private static readonly Dictionary<string, bool> rewrites = new Dictionary<string, bool>();
        private static readonly List<MethodInfo> targets = new List<MethodInfo>();
        private static Func<Player, bool> takeInput = null!;
        private static bool pending, enabled;

        internal static void Prepare(Assembly? assembly)
        {
            if (pending || enabled) return;
            if (assembly == null) { CompatibilityInstaller.Info("OdinShip.Input: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info("OdinShip.Input: client-only; inactive on dedicated server."); return; }
            OdinShipPatch.CheckBuild(assembly);
            takeInput = (Func<Player, bool>)Delegate.CreateDelegate(typeof(Func<Player, bool>),
                Guard.Method(typeof(Player), "TakeInput", typeof(bool)));
            rewrites.Clear();
            targets.Clear();
            var harmony = new Harmony(Owner);
            foreach (string type in new[] { "ShipCustomizationPatch", "TurretModePatch" })
            {
                Type vendor = Guard.Type(assembly, "OdinShip." + type);
                MethodInfo target = Guard.Method(vendor, "Player_Update_Postfix", typeof(void), typeof(Player));
                targets.Add(target);
                harmony.Patch(target, transpiler: Guard.Hook(typeof(OdinShipInputPatch), nameof(GateKeys)));
            }
            pending = true;
            CompatibilityInstaller.Info("OdinShip.Input: 0.8.7 build verified; awaiting input gate verification.");
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (rewrites.Count != 2 || rewrites.Values.Any(v => !v) || targets.Any(method =>
                Harmony.GetPatchInfo(method)?.Transpilers.Count(p => p.owner == Owner
                    && p.PatchMethod == AccessTools.Method(typeof(OdinShipInputPatch), nameof(GateKeys))) != 1))
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("OdinShip.Input: NOT APPLIED; input gate IL or hooks did not match; vendor controls retained.");
                return;
            }
            enabled = true;
            CompatibilityInstaller.Info("OdinShip.Input: APPLIED; original vendor keys respect gameplay/text/map/radial input.");
        }
        private static KeyCode[] Expected(MethodBase method) => method.DeclaringType!.Name == "ShipCustomizationPatch" ? keys.Take(5).ToArray() : new[] { KeyCode.T };
        private static IEnumerable<CodeInstruction> GateKeys(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo get = Guard.Method(typeof(Input), "GetKeyDown", typeof(bool), typeof(KeyCode));
            var calls = code.Select((c, i) => (c, i)).Where(p => p.c.Calls(get)).ToArray();
            KeyCode[] expected = Expected(__originalMethod);
            bool match = calls.Length == expected.Length && calls.Select(p => p.i > 0 ? Constant(code[p.i - 1]) : null)
                .SequenceEqual(expected.Select(k => (int?)k));
            rewrites[__originalMethod.DeclaringType!.Name] = match;
            if (!match) return code;
            return code.Select(c => c.Calls(get) ? new CodeInstruction(c) { opcode = OpCodes.Call,
                operand = AccessTools.Method(typeof(OdinShipInputPatch), nameof(Pressed)) } : c).ToArray();
        }
        private static int? Constant(CodeInstruction c) => c.opcode == OpCodes.Ldc_I4 || c.opcode == OpCodes.Ldc_I4_S ? Convert.ToInt32(c.operand) : (int?)null;
        internal static bool Available()
        {
            Player player = Player.m_localPlayer;
            return player != null && takeInput(player) && !TextInput.IsVisible() && !Minimap.IsOpen() && !Hud.InRadial()
                && (TextViewer.instance == null || !TextViewer.instance.IsVisible());
        }
        private static bool Pressed(KeyCode key) => enabled && Available() && Input.GetKeyDown(key);
    }
}
