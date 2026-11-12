using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class MagicRevampPortalPatch
    {
        internal static readonly TranspilerGroup Group = new TranspilerGroup("MagicRevamp.Portal",
            AccessTools.DeclaredMethod(typeof(MagicRevampPortalPatch), nameof(Rewrite)),
            "a rejected local teleport no longer marks its portal for removal.");

        private static readonly MethodInfo teleport = AccessTools.Method(typeof(Character), nameof(Character.TeleportTo),
            new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
        private static readonly MethodInfo getTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo getRotation = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.rotation));
        private static readonly MethodInfo mark = AccessTools.DeclaredMethod(typeof(MagicRevampPortalPatch), nameof(MarkPortal));
        private static FieldInfo teleported = null!;
        private static bool pending, enabled;

        internal static void Prepare(Assembly? assembly)
        {
            if (Group.Installed) return;
            pending = false;
            enabled = false;
            if (assembly == null) { CompatibilityInstaller.Info($"{Group.Name}: optional mod absent; inactive."); return; }
            if (Guard.KnownServerBuild()) { CompatibilityInstaller.Info($"{Group.Name}: client-only; inactive on dedicated server."); return; }

            Guard.Build(assembly, ExpectedBuilds.MagicRevamp151);
            Guard.Build(typeof(Player).Assembly, ExpectedBuilds.Valheim1021Client);
            Type portal = Guard.Type(assembly, "MagicRevamp.Functions.TeleportMagic");
            MethodInfo target = Guard.Method(portal, "OnTriggerEnter", typeof(void), typeof(Collider));
            teleported = AccessTools.DeclaredField(portal, "m_teleported");
            if (target.IsStatic || teleported == null || teleported.IsStatic || teleported.FieldType != typeof(bool))
                throw new NotSupportedException("MagicRevamp's portal trigger or teleport flag changed.");

            Group.Patch(target);
            pending = true;
            CompatibilityInstaller.Info($"{Group.Name}: audited builds verified; awaiting teleport result verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            Group.Verify();
            enabled = Group.Installed;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) =>
            Group.Transpile(instructions, "the portal teleport result no longer matches", code =>
            {
                // The target currently discards TeleportTo's result and unconditionally sets the flag.
                int site = Site(code, i => Shape(code, i, IsLoad(0), IsLoad(1), IsLoad(0), Calls(getTransform),
                    Calls(getRotation), Is(OpCodes.Ldc_I4_1), Calls(teleport), Is(OpCodes.Pop),
                    Is(OpCodes.Ldarg_0), Is(OpCodes.Ldc_I4_1), c => c.StoresField(teleported), Is(OpCodes.Ret))
                    && code.Skip(i).Take(11).All(Plain) && code[i + 11].blocks.Count == 0);
                if (site < 0 || !Locals(__originalMethod, 2, (0, typeof(Player)), (1, typeof(Vector3)))
                    || code.Count(c => c.Calls(teleport)) != 1 || code.Count(c => c.StoresField(teleported)) != 1
                    || code.Any(c => c.blocks.Count != 0))
                {
                    enabled = false;
                    return false;
                }

                Label done = generator.DefineLabel();
                code[site + 11].labels.Add(done);
                code[site + 7].opcode = OpCodes.Ldloc_0;
                code.InsertRange(site + 8, new[] { new CodeInstruction(OpCodes.Call, mark), new CodeInstruction(OpCodes.Brfalse, done) });
                return true;
            });

        // A non-owner's false result can mean the request was forwarded, so keep the vendor's exit behavior in that case.
        private static bool MarkPortal(bool accepted, Player player) => !enabled || accepted || !player.IsOwner();
    }
}
