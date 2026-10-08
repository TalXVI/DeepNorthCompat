using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthCompat
{
    internal static class VpoBurstPatch
    {
        private const string Owner = Plugin.Guid + ".VPO.Burst";
        private const string VpoHash = "614CD643343E2E2D182BA4B50AA8C96D8FEDB8E09A16C6AA0018BAD222D6EE70";
        private static Type? pending;
        private static int rewrites;

        internal static void Prepare(Assembly? assembly)
        {
            pending = null; rewrites = 0;
            if (assembly == null) { CompatibilityInstaller.Info("VPO.Burst: optional mod absent; inactive."); return; }
            if (Application.platform != RuntimePlatform.WindowsServer)
            { CompatibilityInstaller.Info("VPO.Burst: Windows dedicated-only patch; inactive."); return; }
            if (!Guard.KnownServerBuild())
                throw new NotSupportedException("Windows dedicated game build changed; re-audit required.");
            Guard.Build(assembly, VpoHash);
            string directory = Path.GetDirectoryName(assembly.Location)!;
            RequireFile(Path.Combine(directory, "VPOBurst_Managed.dll"), "05AF1CA87068AC676B3CF56A02E1AA797ED1F4A703E95497C60FC5FBFC7B383D");
            RequireFile(Path.Combine(directory, "VPOBurst_win_x86_64.dll"), "BC112B1A74FEF0D5028E7ABB3014026183175DF7724853DCCB44A188D57BDA46");
            Type loader = Guard.Type(assembly, "ValheimPerformanceOptimizations.BurstLoader");
            MethodInfo initialize = Guard.Method(loader, "Initialize", typeof(void));
            foreach (string property in new[] { "LibraryLoaded", "JobsAreBursted" })
                Guard.Method(loader, "get_" + property, typeof(bool));
            new Harmony(Owner).Patch(initialize,
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(VpoBurstPatch), nameof(Transpiler))));
            pending = loader;
        }

        internal static void Verify()
        {
            Type? loader = pending; pending = null;
            if (loader == null) return;
            try
            {
                if (rewrites != 4) throw new NotSupportedException("VPO platform selection rewrite did not apply.");
                // VPO initializes before this soft dependency. Retry after deferred patches apply.
                if (!(bool)AccessTools.Property(loader, "LibraryLoaded").GetValue(null))
                    AccessTools.Method(loader, "Initialize").Invoke(null, null);
                if (!(bool)AccessTools.Property(loader, "LibraryLoaded").GetValue(null) ||
                    !(bool)AccessTools.Property(loader, "JobsAreBursted").GetValue(null))
                    throw new InvalidOperationException("VPO Windows native library or Burst job probe failed.");
                CompatibilityInstaller.Info("VPO.Burst: APPLIED; Windows dedicated native library loaded and Burst probe passed.");
            }
            catch (Exception error)
            {
                try { new Harmony(Owner).UnpatchSelf(); }
                catch (Exception rollback) { CompatibilityInstaller.Error("VPO.Burst: rollback failed; inspect Harmony state before launch. " + rollback); }
                CompatibilityInstaller.Error("VPO.Burst: NOT APPLIED; required Windows dedicated launch gate failed. " + error);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            rewrites = 0;
            var source = instructions.ToList();
            MethodInfo platform = AccessTools.PropertyGetter(typeof(Application), "platform");
            if (source.Count(i => i.Calls(platform)) != 4) return source;
            var result = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in source)
            {
                result.Add(instruction);
                if (instruction.Calls(platform))
                {
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VpoBurstPatch), nameof(WindowsLibraryPlatform))));
                    rewrites++;
                }
            }
            return result;
        }

        // Normalize only inside VPO's library selector; Unity and other mods retain the server role.
        private static RuntimePlatform WindowsLibraryPlatform(RuntimePlatform platform) =>
            platform == RuntimePlatform.WindowsServer ? RuntimePlatform.WindowsPlayer : platform;

        private static void RequireFile(string path, string expected)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") != expected)
                    throw new NotSupportedException(Path.GetFileName(path) + " build changed; re-audit required.");
        }
    }
}
