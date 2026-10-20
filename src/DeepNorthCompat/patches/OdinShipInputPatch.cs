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
        private static readonly KeyCode[] customizationKeys = { KeyCode.G, KeyCode.H, KeyCode.J, KeyCode.L, KeyCode.K }, turretKeys = { KeyCode.T };
        private static readonly MethodInfo getKeyDown = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) });
        private static readonly MethodInfo pressed = AccessTools.DeclaredMethod(typeof(OdinShipInputPatch), nameof(Pressed));
        private static readonly Dictionary<string, bool> rewrites = new Dictionary<string, bool>();
        private static readonly List<(MethodInfo Target, MethodInfo Gate)> targets = new List<(MethodInfo, MethodInfo)>();
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
            foreach ((string type, string gate) in new[] { ("ShipCustomizationPatch", nameof(GateCustomizationKeys)), ("TurretModePatch", nameof(GateTurretKeys)) })
            {
                MethodInfo target = Guard.Method(Guard.Type(assembly, "OdinShip." + type), "Player_Update_Postfix", typeof(void), typeof(Player));
                HarmonyMethod hook = Guard.Hook(typeof(OdinShipInputPatch), gate);
                targets.Add((target, hook.method));
                harmony.Patch(target, transpiler: hook);
            }
            pending = true;
            CompatibilityInstaller.Info("OdinShip.Input: 0.8.7 build verified; awaiting input gate verification.");
        }
        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (rewrites.Count != 2 || rewrites.Values.Any(v => !v) || targets.Any(pair =>
                Harmony.GetPatchInfo(pair.Target)?.Transpilers.Count(p => p.owner == Owner && p.PatchMethod == pair.Gate) != 1))
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error("OdinShip.Input: NOT APPLIED; input gate IL or hooks did not match; vendor controls retained.");
                return;
            }
            enabled = true;
            CompatibilityInstaller.Info("OdinShip.Input: APPLIED; original vendor keys respect gameplay/text/map/radial input.");
        }
        private static IEnumerable<CodeInstruction> GateCustomizationKeys(IEnumerable<CodeInstruction> instructions) =>
            GateKeys(instructions, "ShipCustomizationPatch", customizationKeys);
        private static IEnumerable<CodeInstruction> GateTurretKeys(IEnumerable<CodeInstruction> instructions) =>
            GateKeys(instructions, "TurretModePatch", turretKeys);
        // Each Input.GetKeyDown call must follow its audited key constant, in order.
        private static IEnumerable<CodeInstruction> GateKeys(IEnumerable<CodeInstruction> instructions, string target, KeyCode[] expected)
        {
            List<CodeInstruction> code = instructions.ToList();
            int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(getKeyDown)).ToArray();
            bool match = rewrites[target] = calls.Length == expected.Length && calls.Select(i => i > 0 ? Constant(code[i - 1]) : null)
                .SequenceEqual(expected.Select(k => (int?)k));
            if (!match)
            {
                ILMatch.Lost(ref enabled, "OdinShip.Input", $"{target}.Player_Update_Postfix no longer reads its audited keys");
                return code;
            }
            return code.Select(c => c.Calls(getKeyDown) ? new CodeInstruction(c) { opcode = OpCodes.Call, operand = pressed } : c).ToArray();
        }
        private static int? Constant(CodeInstruction c) => c.opcode == OpCodes.Ldc_I4 || c.opcode == OpCodes.Ldc_I4_S ? Convert.ToInt32(c.operand) : (int?)null;
        internal static bool Available()
        {
            Player player = Player.m_localPlayer;
            return player != null && takeInput(player) && !TextInput.IsVisible() && !Minimap.IsOpen() && !Hud.InRadial()
                && (TextViewer.instance == null || !TextViewer.instance.IsVisible());
        }
        // A late rewrite failure in one handler clears enabled while the other keeps calling Pressed.
        private static bool Pressed(KeyCode key) => (!enabled || Available()) && Input.GetKeyDown(key);
    }
}
