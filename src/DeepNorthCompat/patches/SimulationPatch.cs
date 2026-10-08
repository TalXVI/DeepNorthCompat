using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    // Removal is delayed until Start: StartupAccelerator defers vendor registrations.
    internal static class SimulationPatch
    {
        internal const string ForkGuid = "MVP.Valheim_Serverside_Simulations";
        internal const string CoreOwner = ForkGuid + ".Core";
        private const string CompatOwner = ForkGuid + ".Compat_ValheimCommunityPatch";
        private const string ConsoleOwner = ForkGuid + ".ServerConsole";
        private const string VpoGuid = "dev.ontrigger.vpo";
        private const string VcpGuid = "MidnightsFX.ValheimCommunityPatch";
        internal const string ForkHash = "804AE557AD93D44C508EFCB472BE23DCFB5DE80FBFA13F5EC264D2DDB009221A";
        // Windows and Linux 1.0.16 and 1.0.17 dedicated builds; each pair differs only in platform reporting.
        internal static readonly string[] ServerHashes =
        {
            "7CAB9B49D31EC064591CA80402DD35C566E03B7297CFB7BF4696C38DA4E24D8B",
            "50035055F9B158A025CACD25E038B603943F7C2A465DA3021707B5F1E44E39FD",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA"
        };
        private const string VpoHash = "614CD643343E2E2D182BA4B50AA8C96D8FEDB8E09A16C6AA0018BAD222D6EE70";
        private const string VcpHash = "E48804F2280878B1BB30393C57C354EFDAA68F106A0B2C77F150C113A25EAA68";
        private static Assembly? fork, vpo, vcp;
        private static bool pending;
        internal static bool Active { get; private set; }

        internal static void Prepare(Func<string, Assembly?> resolve)
        {
            Active = false;
            fork = resolve(ForkGuid); vpo = resolve(VpoGuid); vcp = resolve(VcpGuid);
            pending = fork != null;
            // The fork hash is checked in Verify, where a mismatch can still disable Core.
            if (fork == null) CompatibilityInstaller.Info("Simulation: fork absent; inactive.");
        }

        internal static bool CoreActive(Assembly assembly)
        {
            foreach ((Type type, string target, string patch) in new[]
            {
                (typeof(ZNetScene), "CreateDestroyObjects", "CreateDestroyObjects_Patch"),
                (typeof(ZDOMan), "ReleaseNearbyZDOS", "ZDOMan_ReleaseNearbyZDOS_Patch"),
                (typeof(ZoneSystem), "Update", "ZoneSystem_Update_Patch")
            })
            {
                Type hookType = Guard.Type(assembly, "Valheim_Serverside.Features.Core+" + patch);
                MethodInfo hook = AccessTools.DeclaredMethod(hookType, "Prefix");
                Patches? info = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(type, target));
                if (info == null || !info.Prefixes.Any(p => p.owner == CoreOwner && p.PatchMethod == hook)) return false;
            }
            return true;
        }

        internal static void Verify()
        {
            if (!pending || fork == null) return;
            pending = false;
            var removed = new List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)>();
            (MethodInfo Target, MethodInfo Hook)? addedCompat = null;
            bool core = false;
            try
            {
                if (!CoreActive(fork))
                {
                    CompatibilityInstaller.Info("Simulation: Core not active on this process; object-management patches retained.");
                    return;
                }
                core = true;
                Guard.Build(fork, ForkHash);
                Guard.Build(typeof(ZNet).Assembly, ServerHashes);
                var hooks = new List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)>();
                (MethodInfo Target, MethodInfo Hook)? missingCompat = null;
                if (vpo != null)
                {
                    Guard.Build(vpo, VpoHash);
                    Type group = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.ObjectManagement.ZNetSceneObjectManagementPatch");
                    Type release = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.ObjectManagement.ZDOManReleaseNearbyPatch");
                    foreach ((Type targetType, string target, string method, HarmonyPatchType kind) in new[]
                    {
                        (typeof(ZNetScene), "CreateDestroyObjects", "ZNetScene_CreateDestroyObjects_Prefix", HarmonyPatchType.Prefix),
                        (typeof(ZDOMan), "AddToSector", "ZDOMan_AddToSector_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZDO), "InvalidateSector", "ZDO_InvalidateSector_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZDOMan), "HandleDestroyedZDO", "ZDOMan_HandleDestroyedZDO_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZDO), "Deserialize", "ZDO_Deserialize_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZNetScene), "AddInstance", "ZNetScene_AddInstance_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZNetScene), "Destroy", "ZNetScene_Destroy_Prefix", HarmonyPatchType.Prefix),
                        (typeof(ZNetScene), "Shutdown", "ZNetScene_Shutdown_Prefix", HarmonyPatchType.Prefix)
                    }) hooks.Add(ExactHook(targetType, target, AccessTools.DeclaredMethod(group, method), VpoGuid, kind));
                    hooks.Add(ExactHook(typeof(ZDOMan), "ReleaseNearbyZDOS", AccessTools.DeclaredMethod(release, "Prefix"), VpoGuid, HarmonyPatchType.Prefix));
                }
                if (vcp != null)
                {
                    Guard.Build(vcp, VcpHash);
                    Type group = Guard.Type(vcp, "ValheimCommunityPatch.Patches.Performance.SceneIdleSkipPatch");
                    hooks.Add(ExactHook(typeof(ZNetScene), "CreateDestroyObjects", AccessTools.DeclaredMethod(group, "CreateDestroyObjectsPrefix"), VcpGuid, HarmonyPatchType.Prefix));
                    hooks.Add(ExactHook(typeof(ZNetScene), "CreateDestroyObjects", AccessTools.DeclaredMethod(group, "CreateDestroyObjectsPostfix"), VcpGuid, HarmonyPatchType.Postfix));
                    Type compat = Guard.Type(fork, "Valheim_Serverside.Features.Compat_ValheimCommunityPatch+ZNetScene_Awake_Patch");
                    MethodInfo target = Guard.Method(typeof(ZNetScene), "Awake", typeof(void));
                    MethodInfo hook = Guard.Method(compat, "Postfix", typeof(void));
                    if (!All(target).Any(p => p.PatchMethod == hook))
                    {
                        if ((bool)AccessTools.Field(compat, "s_done").GetValue(null))
                            throw new NotSupportedException("Fork VCP takeover already ran but its registration is missing.");
                        missingCompat = (target, hook);
                    }
                    else ExactHook(typeof(ZNetScene), "Awake", hook, CompatOwner, HarmonyPatchType.Postfix);
                }
                foreach (var hook in hooks)
                {
                    removed.Add(hook);
                    new Harmony(Plugin.Guid + ".Simulation").Unpatch(hook.Target, hook.Hook.PatchMethod);
                }
                if (missingCompat.HasValue)
                {
                    // The fork checks PluginInfos in Awake, before later plugins are added.
                    // Register its existing implementation now; do not duplicate its takeover.
                    addedCompat = missingCompat;
                    new Harmony(CompatOwner).Patch(addedCompat.Value.Target, postfix: new HarmonyMethod(addedCompat.Value.Hook));
                    ExactHook(typeof(ZNetScene), "Awake", addedCompat.Value.Hook, CompatOwner, HarmonyPatchType.Postfix);
                    CompatibilityInstaller.Info("Simulation: registered the fork's existing VCP takeover after chainloading.");
                }
                if (!CoreActive(fork)) throw new InvalidOperationException("Core changed during integration.");
                Active = true;
                CompatibilityInstaller.Info($"Simulation: APPLIED; removed {removed.Count} exact server-side VPO/VCP hooks; Core retained.");
            }
            catch (Exception error)
            {
                if (addedCompat.HasValue)
                {
                    try { new Harmony(CompatOwner).Unpatch(addedCompat.Value.Target, addedCompat.Value.Hook); }
                    catch (Exception rollback) { CompatibilityInstaller.Error("Simulation: fork VCP registration rollback failed. " + rollback); }
                }
                foreach (var hook in removed)
                {
                    try { Restore(hook); }
                    catch (Exception rollback) { CompatibilityInstaller.Error("Simulation: rollback failed; inspect Harmony state before launch. " + rollback); }
                }
                CompatibilityInstaller.Error("Simulation: REJECTED BUILD/REGISTRATIONS; required launch gate failed. " + error);
                // Core next to restored VPO/VCP object management is unsafe; fall back to vanilla, as the fork does when Core fails.
                if (core) DisableFork();
            }
        }

        private static bool IsFork(string owner) => (owner == ForkGuid || owner.StartsWith(ForkGuid + ".")) && owner != ConsoleOwner;

        private static void DisableFork()
        {
            try
            {
                string[] owners = Harmony.GetAllPatchedMethods().SelectMany(All).Select(p => p.owner).Where(IsFork).Distinct().ToArray();
                foreach (string owner in owners) new Harmony(owner).UnpatchSelf();
                if (Harmony.GetAllPatchedMethods().SelectMany(All).Any(p => IsFork(p.owner)))
                    throw new InvalidOperationException("Fork hooks remain installed.");
                CompatibilityInstaller.Error("Simulation: FORK DISABLED; this server runs vanilla object management and ownership until the build mismatch is resolved.");
            }
            catch (Exception error)
            {
                CompatibilityInstaller.Error("Simulation: could not disable the fork; stop the server before players join. " + error);
            }
        }

        internal static IEnumerable<Patch> All(MethodBase target)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            return info == null ? Enumerable.Empty<Patch>() : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers)
                .Concat(info.Finalizers).Concat(info.ILManipulators);
        }

        internal static (MethodBase Target, Patch Hook, HarmonyPatchType Kind) ExactHook(Type type, string name,
            MethodInfo method, string owner, HarmonyPatchType kind)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, name);
            Patches? info = Harmony.GetPatchInfo(target);
            Patch[] all = All(target).Where(p => p.PatchMethod == method).ToArray();
            IEnumerable<Patch> expected = info == null ? Enumerable.Empty<Patch>()
                : kind == HarmonyPatchType.Prefix ? info.Prefixes : kind == HarmonyPatchType.Postfix ? info.Postfixes : info.Transpilers;
            if (all.Length != 1 || all[0].owner != owner || !expected.Contains(all[0]))
                throw new NotSupportedException($"Expected one {owner} {kind}: {Guard.Name(method)} on {Guard.Name(target)}.");
            return (target, all[0], kind);
        }

        internal static void Restore((MethodBase Target, Patch Hook, HarmonyPatchType Kind) hook)
        {
            if (All(hook.Target).Any(p => p.PatchMethod == hook.Hook.PatchMethod)) return;
            var method = new HarmonyMethod(hook.Hook.PatchMethod, hook.Hook.priority, hook.Hook.before, hook.Hook.after, hook.Hook.debug);
            var owner = new Harmony(hook.Hook.owner);
            if (hook.Kind == HarmonyPatchType.Prefix) owner.Patch(hook.Target, prefix: method);
            else if (hook.Kind == HarmonyPatchType.Postfix) owner.Patch(hook.Target, postfix: method);
            else owner.Patch(hook.Target, transpiler: method);
        }
    }
}
