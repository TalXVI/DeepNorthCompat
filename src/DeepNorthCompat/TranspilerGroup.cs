using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DeepNorthCompat
{
    // A Harmony owner whose one transpiler rewrites one method.
    // Verify keeps the group only if the transpiler ran, its edit matched, and Harmony holds exactly one copy of it.
    // HarmonyX reruns the transpiler whenever any mod patches the method.
    // A rerun that no longer matches keeps the incoming IL and reports the loss once.
    internal sealed class TranspilerGroup
    {
        private enum State { Inactive, Pending, Applied, Lost }

        private readonly MethodInfo transpiler;
        private readonly string applied;
        private MethodBase target = null!;
        private State state;
        private bool matched;

        internal TranspilerGroup(string name, MethodInfo transpiler, string applied)
        {
            Name = name;
            Owner = Plugin.Guid + "." + name;
            this.transpiler = transpiler;
            this.applied = applied;
        }

        internal string Name { get; }
        internal string Owner { get; }

        // Harmony's registry answers this, so a rollback by InstallGroup or anyone else also resets the group.
        internal bool Installed => Harmony.HasAnyPatches(Owner);

        internal void Patch(MethodBase method)
        {
            target = method;
            matched = false;
            // The transpiler runs inside Patch, so the state must already be Pending.
            // Otherwise a miss here would read an earlier installation's Applied state and report a loss.
            state = State.Pending;
            try { new Harmony(Owner).Patch(method, transpiler: new HarmonyMethod(transpiler)); }
            catch { state = State.Inactive; throw; }
        }

        internal void Verify()
        {
            if (state != State.Pending) return;
            bool registered = Harmony.GetPatchInfo(target)?.Transpilers.Count(p => p.owner == Owner && p.PatchMethod == transpiler) == 1;
            if (!matched || !registered)
            {
                state = State.Inactive;
                new Harmony(Owner).UnpatchSelf();
                string reason = registered ? "the rewrite did not run or did not match the audited IL" : "the transpiler is not registered exactly once";
                CompatibilityInstaller.Error($"{Name}: NOT APPLIED; {reason}; vendor behavior retained.");
                return;
            }

            state = State.Applied;
            CompatibilityInstaller.Info($"{Name}: APPLIED; {applied}");
        }

        // The edit gets a deep copy and returns false to reject. It must never throw.
        // Transpiler runs inside Harmony's patch call, where an exception can abort another mod's deferred patch batch.
        internal IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, string lost, Func<List<CodeInstruction>, bool> edit)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            matched = edit(code);
            if (matched) return code;

            if (state == State.Applied)
            {
                state = State.Lost;
                CompatibilityInstaller.Error($"{Name}: NO LONGER APPLIED; {lost}; vendor behavior retained.");
            }
            return original;
        }
    }
}
