using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

// The game invokes the real vendor hooks. Unity scene components, status-effect storage,
// and skill level are fixtures; physics, transport, and scene updates are not exercised.
internal static class UsefulPathsTests
{
    private const string Fixture = "DeepNorthCompat.Tests.UsefulPaths";
    private const string Vendor = "RustyMods.UsefulPaths";
    private const string Impact = "MidnightsFX.ImpactfulSkills";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly HashSet<SEMan> attached = new HashSet<SEMan>();
    private static readonly HashSet<Character> players = new HashSet<Character>();
    private static readonly HashSet<Character> tames = new HashSet<Character>();
    private static StatusEffect effect = null!;
    private static Character cartOwner = null!;
    private static float captured;
    private static int id;

    internal static void Run(string profile, Action<string, Action> test)
    {
        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "UsefulPaths.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (path == null) return;

        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(UsefulPathsTests).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH")!, new[] { Path.Combine(profile, "BepInEx/core") }
        });

        Assembly vendor = Assembly.LoadFrom(path);
        Assembly impact = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "ImpactfulSkills.dll", SearchOption.AllDirectories).Single());
        Type plugin = vendor.GetType("UsefulPaths.UsefulPathsPlugin", true)!;
        Type toggle = vendor.GetType("UsefulPaths.Toggle", true)!;
        Type manager = vendor.GetType("UsefulPaths.Managers.UsefulPaths", true)!;
        Type timerPatch = vendor.GetType("UsefulPaths.Managers.UsefulPaths+Character_CustomFixedUpdate_Patch", true)!;
        Type cartPatch = vendor.GetType("UsefulPaths.Managers.UsefulPaths+Vagon_SetMass_Patch", true)!;
        Type haulingPatch = impact.GetType("ImpactfulSkills.patches.Hauling+VagonMassPatch", true)!;
        Type settings = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        Type effectType = vendor.GetType("UsefulPaths.Managers.AirJordan", true)!;
        Type groundType = vendor.GetType("UsefulPaths.Managers.GroundTypes", true)!;
        InitializePlugin(plugin);
        MethodInfo tick = AccessTools.Method(typeof(Character), "CustomFixedUpdate", new[] { typeof(float) });
        MethodInfo setMass = AccessTools.Method(typeof(Vagon), "SetMass", new[] { typeof(float) });
        MethodInfo vendorTick = AccessTools.Method(timerPatch, "Postfix");
        MethodInfo vendorCart = AccessTools.Method(cartPatch, "Prefix");
        MethodInfo hauling = AccessTools.Method(haulingPatch, "Prefix");
        var fixture = new Harmony(Fixture);
        var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/usefulpaths-unsaved.cfg"), false)
            { SaveOnConfigSet = false };
        object on = Enum.Parse(toggle, "On"), off = Enum.Parse(toggle, "Off");
        object enabled = Bind(config, toggle, "enabled", on);
        object allowTames = Bind(config, toggle, "tames", off);
        AccessTools.Field(plugin, "m_enabled").SetValue(null, enabled);
        AccessTools.Field(plugin, "m_applyToCreatures").SetValue(null, allowTames);
        AccessTools.Field(plugin, "m_update").SetValue(null, config.Bind("fixture", "interval", 1f));
        AccessTools.Field(manager, "hash").SetValue(null, 78630);
        AccessTools.Field(settings, "EnableHauling").SetValue(null, config.Bind("fixture", "hauling", true));
        AccessTools.Field(settings, "EnableHaulingCartMassReduction").SetValue(null, config.Bind("fixture", "cart", true));
        AccessTools.Field(settings, "HaulingCartMassReduction").SetValue(null, config.Bind("fixture", "reduction", 0.75f));
        AccessTools.Field(settings, "HaulingBonusesPastLevel100").SetValue(null, config.Bind("fixture", "beyond100", false));

        Player previousPlayer = Player.m_localPlayer;
        try
        {
            fixture.Patch(tick, prefix: Hook(nameof(Skip)));
            fixture.Patch(AccessTools.Method(typeof(Character), "IsPlayer"), prefix: Hook(nameof(IsPlayer)));
            fixture.Patch(AccessTools.Method(typeof(Character), "IsTamed", Type.EmptyTypes), prefix: Hook(nameof(IsTamed)));
            fixture.Patch(AccessTools.Method(typeof(SEMan), "HaveStatusEffect", new[] { typeof(int) }), prefix: Hook(nameof(HaveEffect)));
            fixture.Patch(AccessTools.Method(typeof(SEMan), "AddStatusEffect", new[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) }), prefix: Hook(nameof(AddEffect)));
            fixture.Patch(AccessTools.Method(typeof(SEMan), "RemoveStatusEffect", new[] { typeof(int), typeof(bool) }), prefix: Hook(nameof(RemoveEffect)));
            fixture.Patch(AccessTools.Method(typeof(SEMan), "GetStatusEffect", new[] { typeof(int) }), prefix: Hook(nameof(GetEffect)));
            fixture.Patch(AccessTools.Method(typeof(Character), "GetSkillFactor", new[] { typeof(Skills.SkillType) }), prefix: Hook(nameof(SkillFactor)));
            fixture.Patch(AccessTools.Method(typeof(Player), "GetSkillFactor", new[] { typeof(Skills.SkillType) }), prefix: Hook(nameof(SkillFactor)));
            MethodInfo tryComponent = typeof(GameObject).GetMethods().Single(m => m.Name == "TryGetComponent" && m.IsGenericMethodDefinition);
            fixture.Patch(tryComponent.MakeGenericMethod(typeof(Character)), prefix: Hook(nameof(TryCharacter)));
            fixture.Patch(setMass, prefix: new HarmonyMethod(AccessTools.Method(typeof(UsefulPathsTests), nameof(CaptureMass))) { priority = Priority.Last });
            effect = (StatusEffect)Fake(effectType);
            cartOwner = Fake<Character>();
            Player.m_localPlayer = Fake<Player>();

            var values = (System.Collections.IDictionary)AccessTools.Field(plugin, "m_vagonMass").GetValue(null);
            object paved = Enum.Parse(groundType, "Paved");
            values[paved] = config.Bind("fixture", "path mass", 1f);
            AccessTools.Field(effectType, "m_terrain").SetValue(effect, paved);

            void Install(Assembly? other)
            {
                CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : guid == Impact ? other : null,
                    _ => { }, _ => { }, _ => { });
                CompatibilityInstaller.Verify();
            }

            test("UsefulPaths rejects a missing vendor update hook", () =>
            {
                var errors = new List<string>();
                CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : null, _ => { }, _ => { }, errors.Add);
                CompatibilityInstaller.Verify();

                Check(errors.Any(message => message.Contains("UsefulPaths.Effects: NOT APPLIED")),
                    "a missing vendor registration must be reported");
                Check(!Harmony.HasAnyPatches("DeepNorthCompat.UsefulPaths.Effects"),
                    "a missing vendor registration must roll back the timer rewrite");
            });

            test("UsefulPaths rejects an unaudited vendor build", () =>
            {
                var errors = new List<string>();
                new Harmony(Vendor).Patch(tick, postfix: new HarmonyMethod(vendorTick));
                try
                {
                    CompatibilityInstaller.Install(guid => guid == Vendor ? typeof(UsefulPathsTests).Assembly : null,
                        _ => { }, _ => { }, errors.Add);
                    CompatibilityInstaller.Verify();

                    Check(errors.Any(message => message.Contains("UsefulPaths.Effects: NOT APPLIED") && message.Contains("build changed")),
                        "an unaudited vendor build must be rejected by the build guard");
                    Check(!Harmony.HasAnyPatches("DeepNorthCompat.UsefulPaths.Effects"),
                        "an unaudited vendor build must not install the timer rewrite");
                    Check(Harmony.GetPatchInfo(tick).Postfixes.Any(patch => patch.owner == Vendor && patch.PatchMethod == vendorTick),
                        "rejecting the build must leave the vendor hook registered");
                }
                finally { new Harmony(Vendor).Unpatch(tick, vendorTick); }
            });

            test("UsefulPaths effects attach and clear independently for each character", () =>
            {
                AccessTools.Field(timerPatch, "m_timer").SetValue(null, 0f);
                new Harmony(Vendor).Patch(tick, postfix: new HarmonyMethod(vendorTick));
                Install(null);
                Character player = Actor(), enemy = Actor(), tame = Actor();
                players.Add(player); tames.Add(tame);

                for (int i = 0; i < 4; i++) { Tick(tick, player); Tick(tick, enemy); }
                Check(attached.Contains(player.GetSEMan()), "player effect must attach even when an enemy ticks between player updates");
                Check(!attached.Contains(enemy.GetSEMan()), "an untamed creature must not gain the effect");

                SetValue(allowTames, on);
                for (int i = 0; i < 4; i++) { Tick(tick, tame); Tick(tick, enemy); }
                Check(attached.Contains(tame.GetSEMan()), "enabled tame must gain its own effect");

                SetValue(enabled, off);
                for (int i = 0; i < 4; i++) { Tick(tick, player); Tick(tick, enemy); Tick(tick, tame); }
                Check(!attached.Contains(player.GetSEMan()) && !attached.Contains(tame.GetSEMan()),
                    "disabling the mod must clear each affected character's effect");
            });

            test("UsefulPaths cart retains Hauling after either vendor registration order", () =>
            {
                var cart = Fake<Vagon>();
                AccessTools.Field(typeof(Vagon), "m_attachedObject").SetValue(cart, Fake<GameObject>());
                AccessTools.Field(typeof(Vagon), "m_baseMass").SetValue(cart, 20f);
                AccessTools.Field(typeof(Character), "m_seman").SetValue(cartOwner, FormatterServices.GetUninitializedObject(typeof(SEMan)));
                attached.Add(cartOwner.GetSEMan());
                foreach (bool impactFirst in new[] { true, false })
                {
                    new Harmony(Vendor).Unpatch(setMass, vendorCart);
                    new Harmony(Impact).Unpatch(setMass, hauling);
                    if (impactFirst) new Harmony(Impact).Patch(setMass, prefix: new HarmonyMethod(hauling));
                    new Harmony(Vendor).Patch(setMass, prefix: new HarmonyMethod(vendorCart));
                    if (!impactFirst) new Harmony(Impact).Patch(setMass, prefix: new HarmonyMethod(hauling));
                    Install(impact);

                    values[paved] = config.Bind("fixture", "neutral " + impactFirst, 1f);
                    setMass.Invoke(cart, new object[] { 20f });
                    Near(captured, 5f, "neutral path and 75% Hauling on an empty cart");
                    values[paved] = config.Bind("fixture", "heavy " + impactFirst, 2f);
                    setMass.Invoke(cart, new object[] { 40f });
                    Near(captured, 20f, "path multiplier applies before Hauling on a loaded cart");
                }
            });

            test("UsefulPaths returns to vendor behavior when another transpiler changes the timer", () =>
            {
                var errors = new List<string>();
                CompatibilityInstaller.Install(guid => guid == Vendor ? vendor : guid == Impact ? impact : null,
                    _ => { }, _ => { }, errors.Add);
                CompatibilityInstaller.Verify();
                SetValue(enabled, on);

                var other = new Harmony(Fixture + ".ChangedTimer");
                try
                {
                    other.Patch(vendorTick, transpiler: new HarmonyMethod(AccessTools.Method(typeof(UsefulPathsTests), nameof(InsertTimerNop)))
                        { priority = Priority.First });
                    Check(errors.Any(message => message.Contains("UsefulPaths.Effects: NO LONGER APPLIED")),
                        "a changed timer must report the lost rewrite");

                    AccessTools.Field(timerPatch, "m_timer").SetValue(null, 0f);
                    Character player = Actor(), enemy = Actor();
                    players.Add(player);
                    for (int i = 0; i < 4; i++) { Tick(tick, player); Tick(tick, enemy); }
                    Check(!attached.Contains(player.GetSEMan()), "the unchanged vendor timer must remain in control after rejection");
                }
                finally { other.UnpatchSelf(); }

                AccessTools.Field(timerPatch, "m_timer").SetValue(null, 0f);
                Character restored = Actor(), otherEnemy = Actor();
                players.Add(restored);
                for (int i = 0; i < 4; i++) { Tick(tick, restored); Tick(tick, otherEnemy); }
                Check(attached.Contains(restored.GetSEMan()), "the timer rewrite must work after the competing patch is removed");
            });
        }
        finally
        {
            new Harmony(Fixture).UnpatchSelf();
            new Harmony(Vendor).Unpatch(tick, vendorTick);
            new Harmony(Vendor).Unpatch(setMass, vendorCart);
            new Harmony(Impact).Unpatch(setMass, hauling);
            foreach (string owner in new[] { "DeepNorthCompat.UsefulPaths.Effects", "DeepNorthCompat.UsefulPaths.Cart" })
                new Harmony(owner).UnpatchSelf();
            Player.m_localPlayer = previousPlayer;
            attached.Clear(); players.Clear(); tames.Clear();
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            pointers.Clear();
        }
    }

    private static Character Actor()
    {
        Character character = Fake<Character>();
        AccessTools.Field(typeof(Character), "m_seman").SetValue(character, FormatterServices.GetUninitializedObject(typeof(SEMan)));
        return character;
    }

    private static void Tick(MethodInfo method, Character character) => method.Invoke(character, new object[] { 0.5f });
    private static bool Skip() => false;
    private static bool IsPlayer(Character __instance, ref bool __result) { __result = players.Contains(__instance); return false; }
    private static bool IsTamed(Character __instance, ref bool __result) { __result = tames.Contains(__instance); return false; }
    private static bool HaveEffect(SEMan __instance, ref bool __result) { __result = attached.Contains(__instance); return false; }
    private static bool AddEffect(SEMan __instance, ref StatusEffect __result) { attached.Add(__instance); __result = effect; return false; }
    private static bool RemoveEffect(SEMan __instance, ref bool __result) { __result = attached.Remove(__instance); return false; }
    private static bool GetEffect(SEMan __instance, ref StatusEffect __result) { __result = attached.Contains(__instance) ? effect : null!; return false; }
    private static bool SkillFactor(ref float __result) { __result = 1f; return false; }
    private static bool TryCharacter(ref Character component, ref bool __result) { component = cartOwner; __result = true; return false; }
    private static bool CaptureMass(float mass) { captured = mass; return false; }

    private static IEnumerable<CodeInstruction> InsertTimerNop(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int[] sites = code.Select((instruction, index) => new { instruction, index })
            .Where(item => item.instruction.opcode == OpCodes.Ldsfld && item.instruction.operand is FieldInfo field
                && field.Name == "m_timer").Select(item => item.index).ToArray();
        Check(sites.Length == 2, "the competing transpiler needs the audited vendor timer");
        code.Insert(sites[0] + 1, new CodeInstruction(OpCodes.Nop));
        return code;
    }

    private static object Bind(ConfigFile config, Type type, string key, object value)
    {
        MethodInfo method = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 4 && m.GetParameters()[3].ParameterType == typeof(string));
        return method.MakeGenericMethod(type).Invoke(config, new[] { "fixture", key, value, "" });
    }

    private static void InitializePlugin(Type plugin)
    {
        // ServerSync schedules Unity work during initialization; the embedded host has no main loop.
        FieldInfo instance = AccessTools.Field(typeof(ThreadingHelper), "<Instance>k__BackingField");
        object previous = instance.GetValue(null);
        var harmony = new Harmony(Fixture + ".Initialization");
        harmony.Patch(AccessTools.Method(typeof(ThreadingHelper), "StartSyncInvoke"), prefix: Hook(nameof(Skip)));
        try
        {
            instance.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ThreadingHelper)));
            RuntimeHelpers.RunClassConstructor(plugin.TypeHandle);
        }
        finally
        {
            harmony.UnpatchSelf();
            instance.SetValue(null, previous);
        }
    }

    private static void SetValue(object entry, object value) => entry.GetType().GetProperty("Value")!.SetValue(entry, value);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(UsefulPathsTests), name));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(float value, float expected, string message) => Check(Math.Abs(value - expected) < 0.0001f, $"{message}: {value} != {expected}");
    private static T Fake<T>() where T : Object => (T)Fake(typeof(T));
    private static Object Fake(Type type)
    {
        var value = (Object)FormatterServices.GetUninitializedObject(type);
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
        pointers.Add(pointer); Marshal.WriteInt64(pointer, ++id);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer);
        return value;
    }
}
