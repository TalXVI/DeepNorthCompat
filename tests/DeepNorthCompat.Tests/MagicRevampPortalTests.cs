using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class MagicRevampPortalTests
{
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevampPortal";
    private const string Foreign = "DeepNorthCompat.Tests.MagicRevampPortal.Foreign";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly MethodInfo teleport = AccessTools.Method(typeof(Character), nameof(Character.TeleportTo),
        new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
    private static Type portalType = null!;
    private static MethodInfo enter = null!;
    private static Player player = null!;
    private static Transform transform = null!;
    private static bool owner, teleportResult;
    private static int calls, routed, claims, destroys;

    internal static void Run(string profile, Action<string, Action> test)
    {
        void Case(string name, Action action) => test("MagicRevamp portal " + name, () =>
        {
            try { action(); }
            finally
            {
                new Harmony(MagicRevampPortalPatch.Group.Owner).UnpatchSelf();
                new Harmony(Foreign).UnpatchSelf();
                new Harmony(Fixture).UnpatchSelf();
                foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
                pointers.Clear();
            }
        });

        Case("leaves an absent mod inactive", () =>
        {
            MagicRevampPortalPatch.Prepare(null);
            MagicRevampPortalPatch.Verify();
            Check(!MagicRevampPortalPatch.Group.Installed, "absent mod installed a hook");
        });

        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "MagicRevamp.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            Case("remains inactive on the dedicated server", () =>
            {
                MagicRevampPortalPatch.Prepare(path == null ? null : Assembly.LoadFrom(path));
                MagicRevampPortalPatch.Verify();
                Check(!MagicRevampPortalPatch.Group.Installed, "client portal hook installed on server");
            });
            return;
        }

        Case("rejects an unsupported vendor build", () =>
        {
            bool rejected = false;
            try { MagicRevampPortalPatch.Prepare(typeof(MagicRevampPortalTests).Assembly); }
            catch (NotSupportedException) { rejected = true; }
            Check(rejected && !MagicRevampPortalPatch.Group.Installed, "unknown build was accepted");
        });
        if (path == null) return;

        AccessTools.Method(typeof(Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(MagicRevampPortalTests).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH")!, new[] { Path.Combine(profile, "BepInEx/core") }
        });
        Assembly vendor = Assembly.LoadFrom(path);
        portalType = vendor.GetType("MagicRevamp.Functions.TeleportMagic", true)!;
        enter = AccessTools.Method(portalType, "OnTriggerEnter");

        Case("preserves accepted and forwarded calls while retaining a portal after a rejected owner call", () =>
        {
            using var scene = new Scene(vendor);

            scene.Enter(true, 1f, false);
            Check(calls == 1 && !teleportResult && scene.Marked, "vendor baseline did not mark a rejected owner call");
            scene.Exit();
            Check(claims == 1 && destroys == 1, "vendor baseline did not remove the portal");

            MagicRevampPortalPatch.Prepare(vendor);
            MagicRevampPortalPatch.Verify();
            Check(MagicRevampPortalPatch.Group.Installed, "audited trigger was not patched");

            scene.Enter(true, 1f, false);
            Check(calls == 1 && !teleportResult && !scene.Marked, "rejected owner call marked the portal");
            scene.Exit();
            Check(claims == 0 && destroys == 0, "rejected owner call removed the portal");

            scene.Enter(true, 3f, false, alreadyTeleporting: true);
            Check(calls == 1 && !teleportResult && !scene.Marked, "already-teleporting owner marked the portal");

            scene.Enter(true, 3f, false);
            Check(calls == 1 && teleportResult && scene.Marked, "accepted owner call was changed");
            scene.Exit();
            Check(claims == 1 && destroys == 1, "accepted teleport no longer removes the portal");

            scene.Enter(false, 1f, false);
            Check(calls == 1 && !teleportResult && routed == 1 && scene.Marked, "forwarded non-owner call was changed");
            scene.Exit();
            Check(claims == 1 && destroys == 1, "forwarded call changed vendor exit behavior");

            scene.Enter(true, 1f, true);
            Check(!teleportResult && scene.Marked, "a later rejected call cleared an earlier accepted flag");
        });

        Case("rejects an unexpected trigger shape without removing other hooks", () =>
        {
            var errors = new List<string>();
            Action<string> previous = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                new Harmony(Fixture).Patch(enter, prefix: Hook(nameof(Noop)));
                MagicRevampPortalPatch.Prepare(vendor);
                var source = PatchProcessor.GetOriginalInstructions(enter);
                int call = source.FindIndex(i => i.Calls(teleport));
                Check(call >= 0, "fixture could not find teleport call");
                source[call + 1] = new CodeInstruction(OpCodes.Nop);
                var before = source.Select(i => new CodeInstruction(i)).ToList();
                var generator = new DynamicMethod("portal_rejection", typeof(void), Type.EmptyTypes).GetILGenerator();
                var after = MagicRevampPortalPatch.Rewrite(source, generator, enter).ToList();
                Check(Same(before, after), "rejected rewrite changed incoming IL");
                MagicRevampPortalPatch.Verify();
                Check(!MagicRevampPortalPatch.Group.Installed && errors.Count == 1 && errors[0].Contains("NOT APPLIED"), "shape mismatch did not roll back");
                Check(Harmony.GetPatchInfo(enter)!.Prefixes.Any(p => p.owner == Fixture), "rollback removed another owner");
            }
            finally { CompatibilityInstaller.Error = previous; }
        });

        Case("stays inactive after a later conflicting transpiler is removed", () =>
        {
            var errors = new List<string>();
            Action<string> previous = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                using var scene = new Scene(vendor);
                MagicRevampPortalPatch.Prepare(vendor);
                MagicRevampPortalPatch.Verify();
                new Harmony(Foreign).Patch(enter, transpiler: Hook(nameof(InsertNop), Priority.First));
                Check(errors.Count == 1 && errors[0].Contains("NO LONGER APPLIED"), "later mismatch was not reported");
                scene.Enter(true, 1f, false);
                Check(scene.Marked, "lost rewrite did not retain vendor behavior");
                new Harmony(Foreign).UnpatchSelf();
                scene.Enter(true, 1f, false);
                Check(scene.Marked && errors.Count == 1, "recovered IL silently re-enabled the lost rewrite");
            }
            finally { CompatibilityInstaller.Error = previous; }
        });
    }

    // Unity object creation, local networking, config scheduling and scene transforms are fixtures;
    // the trigger, Player.TeleportTo, view ownership checks and portal flag are real methods.
    private sealed class Scene : IDisposable
    {
        private readonly Harmony hooks = new Harmony(Fixture);
        private readonly Player? previousPlayer = Player.m_localPlayer;
        private readonly object? previousGame = AccessTools.Field(typeof(Game), "<instance>k__BackingField").GetValue(null);
        private readonly object? previousHelper = AccessTools.Field(typeof(ThreadingHelper), "<Instance>k__BackingField").GetValue(null);
        private readonly object?[] previousSettings = new object?[2];
        private readonly FieldInfo[] settings;
        private readonly object portal;
        private readonly Collider collider;

        internal Scene(Assembly vendor)
        {
            Type plugin = vendor.GetType("MagicRevamp.Plugin", true)!;
            Type toggle = vendor.GetType("MagicRevamp.Functions.Toggle", true)!;
            AccessTools.Field(typeof(ThreadingHelper), "<Instance>k__BackingField").SetValue(null, Fake<ThreadingHelper>());
            Patch(typeof(ThreadingHelper), "StartSyncInvoke", nameof(Skip));

            var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/magic-portal.cfg"), false)
                { SaveOnConfigSet = false };
            MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(ConfigDefinition));
            settings = new[] { AccessTools.Field(plugin, "_enableWeightCheck"), AccessTools.Field(plugin, "_enableRestrictedItemCheck") };
            for (int i = 0; i < settings.Length; i++)
            {
                previousSettings[i] = settings[i].GetValue(null);
                settings[i].SetValue(null, bind.MakeGenericMethod(toggle).Invoke(config, new object[]
                    { new ConfigDefinition("test", settings[i].Name), Enum.ToObject(toggle, 0), new ConfigDescription("fixture") }));
            }

            MethodInfo getPlayer = typeof(Component).GetMethods().Single(m => m.Name == "GetComponent" &&
                m.IsGenericMethodDefinition && m.GetParameters().Length == 0).MakeGenericMethod(typeof(Player));
            hooks.Patch(getPlayer, prefix: Hook(nameof(GetPlayer)));
            Patch(typeof(Component), "get_transform", nameof(GetTransform));
            Patch(typeof(Transform), "get_rotation", nameof(GetRotation));
            Patch(typeof(Transform), "get_position", nameof(GetPosition));
            Patch(typeof(PlayerProfile), "GetCustomSpawnPoint", nameof(GetSpawn));
            Patch(typeof(ZNetView), "IsValid", nameof(IsValid));
            Patch(typeof(ZNetView), "IsOwner", nameof(IsOwner));
            Patch(typeof(ZNetView), "ClaimOwnership", nameof(Claim));
            Patch(typeof(ZNetView), "Destroy", nameof(Destroy));
            hooks.Patch(AccessTools.Method(typeof(ZNetView), "InvokeRPC", new[] { typeof(string), typeof(object[]) }), prefix: Hook(nameof(Route)));
            hooks.Patch(AccessTools.Method(typeof(Player), nameof(Player.TeleportTo)), postfix: Hook(nameof(Observe)));

            player = Fake<Player>();
            Player.m_localPlayer = player;
            AccessTools.Field(typeof(Character), "m_nview").SetValue(player, Fake<ZNetView>());
            transform = Fake<Transform>();
            var game = Fake<Game>();
            AccessTools.Field(typeof(Game), "<instance>k__BackingField").SetValue(null, game);
            AccessTools.Field(typeof(Game), "m_playerProfile").SetValue(game, FormatterServices.GetUninitializedObject(typeof(PlayerProfile)));
            portal = Fake(portalType);
            AccessTools.Field(portalType, "m_portalInstance").SetValue(portal, Fake<GameObject>());
            AccessTools.Field(portalType, "m_nview").SetValue(portal, Fake<ZNetView>());
            collider = Fake<Collider>();
        }

        internal bool Marked => (bool)AccessTools.Field(portalType, "m_teleported").GetValue(portal);
        internal void Enter(bool isOwner, float cooldown, bool marked, bool alreadyTeleporting = false)
        {
            owner = isOwner;
            calls = routed = claims = destroys = 0;
            teleportResult = false;
            AccessTools.Field(typeof(Player), "m_teleportCooldown").SetValue(player, cooldown);
            AccessTools.Field(typeof(Player), "m_teleporting").SetValue(player, alreadyTeleporting);
            AccessTools.Field(portalType, "m_teleported").SetValue(portal, marked);
            enter.Invoke(portal, new object[] { collider });
        }

        internal void Exit() => AccessTools.Method(portalType, "OnTriggerExit").Invoke(portal, new object[] { collider });
        private void Patch(Type type, string method, string prefix) => hooks.Patch(AccessTools.Method(type, method), prefix: Hook(prefix));

        public void Dispose()
        {
            hooks.UnpatchSelf();
            for (int i = 0; i < settings.Length; i++) settings[i].SetValue(null, previousSettings[i]);
            Player.m_localPlayer = previousPlayer!;
            AccessTools.Field(typeof(Game), "<instance>k__BackingField").SetValue(null, previousGame);
            AccessTools.Field(typeof(ThreadingHelper), "<Instance>k__BackingField").SetValue(null, previousHelper);
        }
    }

    private static T Fake<T>() where T : Object => (T)Fake(typeof(T));
    private static Object Fake(Type type)
    {
        var value = (Object)FormatterServices.GetUninitializedObject(type);
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
        pointers.Add(pointer);
        Marshal.WriteInt32(pointer, pointers.Count);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer);
        return value;
    }

    private static HarmonyMethod Hook(string name, int priority = Priority.Normal) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampPortalTests), name)) { priority = priority };
    private static bool Same(IList<CodeInstruction> a, IList<CodeInstruction> b) => a.Count == b.Count && a.Zip(b,
        (x, y) => x.opcode == y.opcode && Equals(x.operand, y.operand) && x.labels.SequenceEqual(y.labels) && x.blocks.SequenceEqual(y.blocks)).All(v => v);
    private static bool Skip() => false;
    private static bool GetPlayer(ref Player __result) { __result = player; return false; }
    private static bool GetTransform(ref Transform __result) { __result = transform; return false; }
    private static bool GetRotation(ref Quaternion __result) { __result = Quaternion.identity; return false; }
    private static bool GetPosition(ref Vector3 __result) { __result = Vector3.zero; return false; }
    private static bool GetSpawn(ref Vector3 __result) { __result = new Vector3(10, 0, 10); return false; }
    private static bool IsValid(ref bool __result) { __result = true; return false; }
    private static bool IsOwner(ref bool __result) { __result = owner; return false; }
    private static bool Claim() { claims++; return false; }
    private static bool Destroy() { destroys++; return false; }
    private static bool Route() { routed++; return false; }
    private static void Observe(bool __result) { calls++; teleportResult = __result; }
    private static void Noop() { }
    private static IEnumerable<CodeInstruction> InsertNop(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int call = code.FindIndex(i => i.Calls(teleport));
        if (call < 0) throw new InvalidOperationException("fixture teleport call missing");
        code.Insert(call + 1, new CodeInstruction(OpCodes.Nop));
        return code;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
