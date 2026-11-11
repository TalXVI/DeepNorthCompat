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
        private const string Managed200 = "3310A393ED7C27F0F91B61A8EA114F2D080A6016EB0CDC895D02B8846C2BE4D1";
        private const string Native201 = "5275975C2B6BAD1CE67496D7076BA4EBCF4AB7134111BF255A51D69A0CDC8D88";
        private static readonly MethodInfo platform = AccessTools.PropertyGetter(typeof(Application), nameof(Application.platform));
        private static readonly MethodInfo libraryPlatform = AccessTools.DeclaredMethod(typeof(VpoBurstPatch), nameof(WindowsLibraryPlatform));

        // The managed and Windows native Burst libraries shipped with each upstream-fixed main DLL.
        private static readonly Dictionary<string, (string Managed, string Native)> BurstBuilds = new Dictionary<string, (string, string)>
        {
            [ExpectedBuilds.Vpo200] = (Managed200, "1CE566AE62822F19CF18D873BBB42D8E80DB0DC94CFAB194D5E01BE8B1510D47"),
            [ExpectedBuilds.Vpo201] = (Managed200, Native201),
            [ExpectedBuilds.Vpo210] = (Managed200, Native201),
            [ExpectedBuilds.Vpo220] = ("AC9EF31097D1A04D781B4D75BE7ACC6FA31ABA02794D13A8695E4EDABEB1B2E1", "EF66CE863D54F36477DFD0C83F6DA1EB9E7DB402902FF3E67DCF1B108207CC24")
        };

        private static Type? pending;
        private static bool pendingUpstream;
        private static int rewrites;

        internal static void Prepare(Assembly? assembly)
        {
            pending = null; pendingUpstream = false; rewrites = 0;
            if (assembly == null) { CompatibilityInstaller.Info("VPO.Burst: optional mod absent; inactive."); return; }
            if (Application.platform != RuntimePlatform.WindowsServer)
            { CompatibilityInstaller.Info("VPO.Burst: Windows dedicated-only patch; inactive."); return; }
            if (!Guard.KnownServerBuild())
                throw new NotSupportedException("Windows dedicated game build changed; re-audit required.");
            Guard.Build(assembly, ExpectedBuilds.Vpo);
            string directory = Path.GetDirectoryName(assembly.Location)!;
            string build = Guard.Sha256(assembly.Location);
            bool fixedUpstream = build != ExpectedBuilds.Vpo123;
            if (fixedUpstream)
            {
                var burst = BurstBuilds[build];
                RequireFile(Path.Combine(directory, "ValheimPerformanceOptimizations.Burst.dll"), burst.Managed);
                RequireFile(Path.Combine(directory, "ValheimPerformanceOptimizations.Burst_win_x86_64.dll"), burst.Native);
            }
            else
            {
                RequireFile(Path.Combine(directory, "VPOBurst_Managed.dll"), "05AF1CA87068AC676B3CF56A02E1AA797ED1F4A703E95497C60FC5FBFC7B383D");
                RequireFile(Path.Combine(directory, "VPOBurst_win_x86_64.dll"), "BC112B1A74FEF0D5028E7ABB3014026183175DF7724853DCCB44A188D57BDA46");
            }

            Type loader = Guard.Type(assembly, "ValheimPerformanceOptimizations.BurstLoader");
            MethodInfo initialize = Guard.Method(loader, "Initialize", typeof(void));
            foreach (string property in new[] { "LibraryLoaded", "JobsAreBursted" })
                Guard.Method(loader, "get_" + property, typeof(bool));

            if (!fixedUpstream)
                new Harmony(Owner).Patch(initialize,
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(VpoBurstPatch), nameof(Transpiler))));
            pending = loader; pendingUpstream = fixedUpstream;
        }

        internal static void Verify()
        {
            Type? loader = pending; pending = null;
            bool fixedUpstream = pendingUpstream; pendingUpstream = false;
            if (loader == null) return;
            try
            {
                if (!fixedUpstream && rewrites != 4) throw new NotSupportedException("VPO platform selection rewrite did not apply.");
                // VPO initializes before this soft dependency. Retry after deferred patches apply.
                if (!fixedUpstream && !(bool)AccessTools.Property(loader, "LibraryLoaded").GetValue(null))
                    AccessTools.Method(loader, "Initialize").Invoke(null, null);
                if (!(bool)AccessTools.Property(loader, "LibraryLoaded").GetValue(null) ||
                    !(bool)AccessTools.Property(loader, "JobsAreBursted").GetValue(null))
                    throw new InvalidOperationException("VPO Windows native library or Burst job probe failed.");
                CompatibilityInstaller.Info(fixedUpstream
                    ? "VPO.Burst: fixed upstream; Windows dedicated native library loaded and Burst probe passed; inactive."
                    : "VPO.Burst: APPLIED; Windows dedicated native library loaded and Burst probe passed.");
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
            if (source.Count(i => i.Calls(platform)) != 4) return source;
            var result = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in source)
            {
                result.Add(instruction);
                if (instruction.Calls(platform))
                {
                    result.Add(new CodeInstruction(OpCodes.Call, libraryPlatform));
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
