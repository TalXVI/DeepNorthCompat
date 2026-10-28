using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DeepNorthCompat.Preloader
{
    public static class Patcher
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("DNC.LegacyStats");

        public static IEnumerable<string> TargetDLLs
        {
            get
            {
                if (!HasConsumer())
                {
                    Log.LogInfo("LegacyStats: no installed mod references the removed API; inactive.");
                    return Array.Empty<string>();
                }

                using (var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(Paths.ManagedPath, "assembly_valheim.dll")))
                {
                    if (Profile(assembly).Methods.Any(Legacy))
                    {
                        Log.LogInfo("LegacyStats: game already provides the API; inactive.");
                        return Array.Empty<string>();
                    }

                    if (!Supported(assembly))
                    {
                        Log.LogError("LegacyStats: NOT APPLIED; unaudited game build with a missing stats API. Re-audit required.");
                        return Array.Empty<string>();
                    }
                }

                return new[] { "assembly_valheim.dll" };
            }
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            TypeDefinition profile = Profile(assembly);
            if (profile.Methods.Any(Legacy)) return;
            if (!Supported(assembly)) throw new NotSupportedException("LegacyStats: unaudited game build. Re-audit required.");

            MethodDefinition current = profile.Methods.Single(method => !method.IsStatic && method.FullName ==
                "System.Single PlayerProfile::GetStat(PlayerStatType,DifficultyRequirement)");
            MethodDefinition difficulty = assembly.MainModule.Types.Single(type => type.FullName == "Achievements")
                .Methods.Single(method => method.IsStatic && method.FullName == "System.Int32 Achievements::GetCurrentAchievementDifficultyIndex()");
            OpCode[] shape =
            {
                OpCodes.Ldc_I4_0, OpCodes.Call, OpCodes.Brfalse_S, OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldarg_2,
                OpCodes.Ldelem_Ref, OpCodes.Ldarg_1, OpCodes.Callvirt, OpCodes.Ret, OpCodes.Ldarg_0, OpCodes.Ldfld,
                OpCodes.Ldc_I4_0, OpCodes.Ldelem_Ref, OpCodes.Ldarg_1, OpCodes.Callvirt, OpCodes.Ret
            };

            if (current.Body.HasVariables || current.Body.HasExceptionHandlers
                || !current.Body.Instructions.Select(instruction => instruction.OpCode).SequenceEqual(shape))
                throw new NotSupportedException("LegacyStats: GetStat IL changed; assembly unchanged.");

            var bridge = new MethodDefinition("GetStat", MethodAttributes.Public | MethodAttributes.HideBySig, current.ReturnType);
            bridge.Parameters.Add(new ParameterDefinition("stat", ParameterAttributes.None, current.Parameters[0].ParameterType));
            bridge.Body.MaxStackSize = current.Body.MaxStackSize;
            bridge.Body.InitLocals = current.Body.InitLocals;
            var copies = current.Body.Instructions.ToDictionary(instruction => instruction, _ => Instruction.Create(OpCodes.Nop));

            foreach (Instruction instruction in current.Body.Instructions)
            {
                Instruction copy = copies[instruction];
                copy.OpCode = instruction.OpCode;
                copy.Operand = instruction.Operand is Instruction target ? copies[target] : instruction.Operand;

                if (copy.OpCode == OpCodes.Ldarg_2)
                {
                    copy.OpCode = OpCodes.Call;
                    copy.Operand = difficulty;
                }

                bridge.Body.Instructions.Add(copy);
            }

            profile.Methods.Add(bridge);
            Log.LogInfo("LegacyStats: APPLIED; restored PlayerProfile.GetStat(PlayerStatType) for installed legacy consumers.");
        }

        private static TypeDefinition Profile(AssemblyDefinition assembly) =>
            assembly.MainModule.Types.Single(type => type.FullName == "PlayerProfile");

        private static bool Legacy(MethodReference method) => method.HasThis &&
            method.FullName == "System.Single PlayerProfile::GetStat(PlayerStatType)";

        private static bool Supported(AssemblyDefinition assembly)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(assembly.MainModule.FileName))
                return GameBuilds.Valheim1021.Contains(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""));
        }

        private static bool HasConsumer()
        {
            foreach (string directory in new[] { Paths.PluginPath, Paths.PatcherPluginPath }.Where(Directory.Exists))
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                AssemblyDefinition assembly;

                try { assembly = AssemblyDefinition.ReadAssembly(path); }
                catch (BadImageFormatException) { continue; }

                using (assembly)
                    if (assembly.Modules.SelectMany(module => module.GetMemberReferences()).OfType<MethodReference>()
                        .Any(method => Legacy(method) && method.DeclaringType.Scope is AssemblyNameReference reference && reference.Name == "assembly_valheim"))
                        return true;
            }

            return false;
        }
    }
}
