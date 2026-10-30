using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;
using Object = UnityEngine.Object;

namespace DeepNorthCompat
{
    // RuneMagic's UnlockManager.isUnlockable(piece) is
    //     if (piece == null) return false;
    //     foreach (RunemagicPieceConfig config in ValheimMod.runemagicPieceConfigs)
    //         if (config.pieceName.Equals(piece.gameObject.name)) return true;
    //     return false;
    // Object.name allocates a new string on every read, so a lookup that misses the 25-entry list makes 25 strings
    // and 50 native calls. The patch reads the name once, right after the null check, into a new local. Membership
    // isn't cached, because the vendor list gives no notice when it changes.
    internal static class RuneRequirementPatch
    {
        internal static readonly TranspilerGroup Group = new TranspilerGroup("RuneMagic.RequirementLookup",
            AccessTools.DeclaredMethod(typeof(RuneRequirementPatch), nameof(Rewrite)),
            "rune membership reads the piece name once per lookup without caching membership.");
        // Positions in the prologue, which Prologue matches at index 0.
        private const int NullBranch = 3, ListLoad = 6;
        private static readonly MethodInfo gameObjectGetter = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
        private static readonly MethodInfo nameGetter = AccessTools.PropertyGetter(typeof(Object), nameof(Object.name));
        private static readonly MethodInfo objectEquality = AccessTools.Method(typeof(Object), "op_Equality");
        private static readonly MethodInfo stringEquals = AccessTools.Method(typeof(string), nameof(string.Equals), new[] { typeof(string) });
        private static MethodInfo getEnumerator = null!, pieceNameGetter = null!;
        private static FieldInfo pieceConfigsField = null!;
        private static Type configType = null!;

        internal static void Prepare(Assembly? rune)
        {
            if (Group.Installed) return;

            if (rune == null)
            {
                CompatibilityInstaller.Info($"{Group.Name}: optional mod absent; inactive.");
                return;
            }

            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info($"{Group.Name}: client-only; inactive on dedicated server.");
                return;
            }

            Guard.Build(rune, HearthTerrainPatch.RuneBuild);
            Guard.Build(typeof(Player).Assembly, ExpectedBuilds.Valheim1021Client);
            MethodInfo isUnlockable = Guard.Method(Guard.Type(rune, "ValheimMod.UnlockManager"), "isUnlockable", typeof(bool), typeof(Piece));
            configType = Guard.Type(rune, "ValheimMod.RunemagicPieceConfig");
            Type list = typeof(List<>).MakeGenericType(configType);
            pieceConfigsField = AccessTools.DeclaredField(Guard.Type(rune, "ValheimMod.ValheimMod"), "runemagicPieceConfigs");
            if (!isUnlockable.IsStatic || pieceConfigsField == null || !pieceConfigsField.IsStatic || pieceConfigsField.FieldType != list)
                throw new NotSupportedException("RuneMagic's membership signature or rune list changed.");

            getEnumerator = Guard.Method(list, "GetEnumerator", typeof(List<>.Enumerator).MakeGenericType(configType));
            pieceNameGetter = Guard.Method(configType, "get_pieceName", typeof(string));
            Group.Patch(isUnlockable);
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) =>
            Group.Transpile(instructions, "isUnlockable no longer has the audited null check and name lookup loop", code =>
            {
                // Reject rather than throw. Every index the edit uses below comes from a match that passed here.
                int nameSite = NameReadSite(code);
                if (nameSite < 0 || !Prologue(code)
                    || !Locals(__originalMethod, 3, (0, getEnumerator.ReturnType), (1, configType), (2, typeof(bool))))
                    return false;

                CacheName(code, nameSite, generator.DeclareLocal(typeof(string)));
                return true;
            });

        private static void CacheName(List<CodeInstruction> code, int nameSite, LocalBuilder name)
        {
            // The loop compares config.pieceName with the local instead of piece.gameObject.name.
            code.RemoveRange(nameSite - 2, 3);
            code.Insert(nameSite - 2, new CodeInstruction(OpCodes.Ldloc, name));

            // Read the name immediately after the null check. Move its branch label to the new first instruction.
            // Editing the loop first keeps nameSite valid.
            CodeInstruction[] read =
            {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Callvirt, gameObjectGetter),
                new CodeInstruction(OpCodes.Callvirt, nameGetter), new CodeInstruction(OpCodes.Stloc, name)
            };
            read[0].MoveLabelsFrom(code[ListLoad]);
            code.InsertRange(ListLoad, read);
        }

        // if (piece == null) return false; then the rune list's GetEnumerator. The null check branches to the list
        // load, and the name read goes in front of it, so no exception block may start there.
        private static bool Prologue(List<CodeInstruction> code) =>
            Shape(code, 0, Is(OpCodes.Ldarg_0), Is(OpCodes.Ldnull), Calls(objectEquality), BranchIfFalse,
                Is(OpCodes.Ldc_I4_0), Is(OpCodes.Ret), Loads(pieceConfigsField), Calls(getEnumerator), IsStore(0))
            && code[NullBranch].operand is Label notNull
            && code[ListLoad].labels.Contains(notNull)
            && code[ListLoad].blocks.Count == 0;

        // The loop's config.pieceName.Equals(piece.gameObject.name), the method's only name read. Returns the index
        // of get_name, or -1. The edit removes the three getter instructions, so none may carry a label or block.
        private static int NameReadSite(List<CodeInstruction> code)
        {
            int site = Site(code, i => code[i].Calls(nameGetter));
            bool matches = Shape(code, site - 4, IsLoad(1),
                    Calls(pieceNameGetter), Is(OpCodes.Ldarg_0), Calls(gameObjectGetter),
                    Calls(nameGetter), Calls(stringEquals))
                && code.Skip(site - 2).Take(3).All(Plain);
            return matches ? site : -1;
        }

        private static bool BranchIfFalse(CodeInstruction instruction) =>
            instruction.opcode == OpCodes.Brfalse || instruction.opcode == OpCodes.Brfalse_S;
    }
}
