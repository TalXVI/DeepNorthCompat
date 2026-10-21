using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

// Runs RuneMagic's SpriteFadeInOut against fake Unity objects. The fake material getter clones the shared
// material once per renderer, as Unity's does, and Destroy runs OnDestroy on the destroyed hierarchy.
internal static class RuneSpriteMaterialTests
{
    private const string Owner = "DeepNorthCompat.RuneMagic.SpriteMaterials";
    private const string Fixture = "Tests.RuneSpriteMaterials";
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.RuneSpritePatch", true)!;
    private static readonly FieldInfo pointer = AccessTools.Field(typeof(Object), "m_CachedPtr");
    private static readonly Type textMesh = Type.GetType("UnityEngine.TextMesh, UnityEngine.TextRenderingModule", true)!;
    private static readonly Dictionary<Component, GameObject> objects = new Dictionary<Component, GameObject>();
    private static readonly Dictionary<GameObject, List<Component>> attached = new Dictionary<GameObject, List<Component>>();
    private static readonly Dictionary<GameObject, GameObject> parents = new Dictionary<GameObject, GameObject>();
    private static readonly Dictionary<Renderer, Material> assigned = new Dictionary<Renderer, Material>();
    private static readonly HashSet<Renderer> owning = new HashSet<Renderer>();
    private static readonly Dictionary<Material, Dictionary<string, Color>> colors = new Dictionary<Material, Dictionary<string, Color>>();
    private static readonly Dictionary<Material, Dictionary<string, float>> floats = new Dictionary<Material, Dictionary<string, float>>();
    private static readonly Dictionary<Component, Color> textColors = new Dictionary<Component, Color>();
    private static readonly Dictionary<string, float> config = new Dictionary<string, float>
    {
        ["SpriteFadeInOutMaxOpacity"] = 0.8f,
        ["SpriteFadeInOutMinIntensity"] = -3f
    };
    private static readonly List<Object> destroyed = new List<Object>();
    private static readonly List<(Object Target, string Name, Color Value)> writes = new List<(Object, string, Color)>();
    private static readonly List<(GameObject Target, Type Type)> added = new List<(GameObject, Type)>();
    private static int getters, clones, teardownReads, id = 70000;
    private static bool tearingDown;

    private sealed class Rune
    {
        internal GameObject Root = null!;
        internal MonoBehaviour Fade = null!;
        internal Renderer? Renderer;
        internal Material? Prefab;
        internal Component? Text;
    }

    internal static void Run(string profile, Action<string, Action> test)
    {
        void Case(string name, Action action) => test("rune sprite materials " + name, () =>
        {
            try { action(); }
            finally { Cleanup(); }
        });

        Case("stay inactive without RuneMagic and reject an unsupported build", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Install(null, messages, errors);
            Check(errors.Count == 0 && messages.Contains("RuneMagic.SpriteMaterials: optional mod absent; inactive.")
                && !Harmony.HasAnyPatches(Owner), "absent RuneMagic: " + string.Join("\n", errors));
            Install(typeof(RuneSpriteMaterialTests).Assembly, messages, errors);
            Check(errors.Count(e => e.StartsWith("RuneMagic.SpriteMaterials: NOT APPLIED") && e.Contains("Re-audit required.")) == 1,
                "unknown RuneMagic build was not rejected: " + string.Join("\n", errors));
            Check(!Harmony.HasAnyPatches(Owner), "unsupported build installed a sprite material hook");
        });

        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "RuneMagic.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (path == null) return;
        Assembly runeMagic = Assembly.LoadFrom(path);
        Type fade = runeMagic.GetType("ValheimMod.Monobehaviours.SpriteFadeInOut", true)!;
        MethodInfo awake = AccessTools.DeclaredMethod(fade, "Awake"), update = AccessTools.DeclaredMethod(fade, "Update");
        MethodInfo getMaterial = AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.material));
        MethodInfo hook = AccessTools.DeclaredMethod(Patch, "Instance");
        FieldInfo spriteField = AccessTools.DeclaredField(fade, "sprite"), textField = AccessTools.DeclaredField(fade, "text");
        List<CodeInstruction> Rewrite(List<CodeInstruction> code) =>
            ((IEnumerable<CodeInstruction>)AccessTools.DeclaredMethod(Patch, "Rewrite").Invoke(null, new object[] { code })).ToList();

        Case("apply once on each supported role", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Install(runeMagic, messages, errors);
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(Harmony.GetPatchInfo(awake)?.Transpilers.Count(p => p.owner == Owner) == 1, "sprite material transpiler missing");
            Check(messages.Count(m => m.StartsWith("RuneMagic.SpriteMaterials: APPLIED")) == 1, "sprite material verification missing");
            CompatibilityInstaller.Verify();
            Check(messages.Count(m => m.StartsWith("RuneMagic.SpriteMaterials: APPLIED")) == 1, "verification repeated the sprite material result");
        });
        Case("rewrite only passes the sprite's owner to its material read", () =>
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(awake);
            List<CodeInstruction> result = Rewrite(original);
            int site = original.FindIndex(i => i.Calls(getMaterial));
            Check(site > 0 && result.Count == original.Count + 1, "exactly one instruction added");
            Check(result[site].opcode == OpCodes.Ldarg_0 && result[site].labels.Count == 0 && result[site].blocks.Count == 0
                && result[site + 1].opcode == OpCodes.Call && Equals(result[site + 1].operand, hook), "material read redirected with its owner");
            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction before = original[i], after = result[i < site ? i : i + 1];
                Check(before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks), "branch and exception targets retained");
                if (i != site) Check(before.opcode == after.opcode && Equals(before.operand, after.operand), "instruction " + i + " unchanged");
            }
            Check(original[site].opcode == OpCodes.Callvirt && Equals(original[site].operand, getMaterial), "incoming IL left unmodified");
        });
        Case("rewrite rejects a changed Awake and reports a lost rewrite once", () =>
        {
            var messages = new List<string>();
            var errors = new List<string>();
            Install(runeMagic, messages, errors);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(awake);
            int site = original.FindIndex(i => i.Calls(getMaterial));
            var twice = original.ToList();
            twice.InsertRange(site + 1, new[] { new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, spriteField), new CodeInstruction(OpCodes.Callvirt, getMaterial) });
            var receiver = original.Select(i => new CodeInstruction(i)).ToList();
            receiver[site - 1].operand = textField;
            foreach (List<CodeInstruction> changed in new[] { twice, receiver })
            {
                List<CodeInstruction> result = Rewrite(changed);
                Check(result.Count == changed.Count && result.Zip(changed, (a, b) => a.opcode == b.opcode && Equals(a.operand, b.operand)).All(x => x),
                    "a changed Awake keeps the incoming IL");
            }
            Check(errors.Count(e => e.StartsWith("RuneMagic.SpriteMaterials: NO LONGER APPLIED")) == 1,
                "lost rewrite reported once: " + string.Join("\n", errors));
        });

        var fixture = new Harmony(Fixture);
        try
        {
            Hook(fixture, AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject)), nameof(GameObjectOf));
            Hook(fixture, AccessTools.Method(typeof(GameObject), "GetComponentsInternal"), nameof(Components));
            Hook(fixture, AccessTools.Method(typeof(GameObject), nameof(GameObject.AddComponent), new[] { typeof(Type) }), nameof(Add));
            Hook(fixture, getMaterial, nameof(MaterialOf));
            Hook(fixture, AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.sharedMaterial)), nameof(SharedOf));
            Hook(fixture, AccessTools.Method(typeof(Material), nameof(Material.GetColor), new[] { typeof(string) }), nameof(GetColor));
            Hook(fixture, AccessTools.Method(typeof(Material), nameof(Material.SetColor), new[] { typeof(string), typeof(Color) }), nameof(SetColor));
            Hook(fixture, AccessTools.Method(typeof(Material), nameof(Material.GetFloat), new[] { typeof(string) }), nameof(GetFloat));
            Hook(fixture, AccessTools.Method(fade.Assembly.GetType("ValheimMod.Setup.ConfigLoader", true), "getFloat", new[] { typeof(string), typeof(bool) }), nameof(Config));
            Hook(fixture, AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object) }), nameof(Destroy));
            Hook(fixture, AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object), typeof(float) }), nameof(Destroy));
            Hook(fixture, AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime)), nameof(Delta));
            Hook(fixture, AccessTools.PropertyGetter(textMesh, "color"), nameof(TextColor));
            Hook(fixture, AccessTools.PropertySetter(textMesh, "color"), nameof(SetTextColor));

            Rune Spawn(float mode, bool child = false, bool text = false)
            {
                var rune = new Rune { Root = Fake<GameObject>() };
                attached[rune.Root] = new List<Component>();
                if (text)
                {
                    rune.Text = (Component)Fake(textMesh);
                    Attach(rune.Text, rune.Root);
                    textColors[rune.Text] = new Color(0.2f, 0.4f, 0.6f, 1f);
                }
                else
                {
                    GameObject host = rune.Root;
                    if (child) { host = Fake<GameObject>(); attached[host] = new List<Component>(); parents[host] = rune.Root; }
                    var renderer = Fake<SpriteRenderer>();
                    Attach(renderer, host);
                    var prefab = Fake<Material>();
                    colors[prefab] = new Dictionary<string, Color> { ["_EmissionColor"] = new Color(0f, 0.5098f, 1f) * 4f, ["_Color"] = Color.black };
                    floats[prefab] = new Dictionary<string, float> { ["_Mode"] = mode };
                    assigned[renderer] = prefab;
                    rune.Renderer = renderer;
                    rune.Prefab = prefab;
                }
                return rune;
            }
            // HomingSpawner.emitParticle adds the component, which runs Awake, then sets its timing.
            void Emit(Rune rune)
            {
                rune.Fade = (MonoBehaviour)Fake(fade);
                Attach(rune.Fade, rune.Root);
                awake.Invoke(rune.Fade, null);
                foreach (var (field, value) in new[] { ("lifeTime", 2f), ("fadeInTime", 1f), ("fadeOutTime", 1f) })
                    AccessTools.DeclaredField(fade, field).SetValue(rune.Fade, value);
            }
            void Live(Rune rune)
            {
                for (int frame = 0; frame < 8 && Alive(rune.Root); frame++) update.Invoke(rune.Fade, null);
                Check(!Alive(rune.Root), "the vendor destroys the sprite after its lifetime");
            }
            (string, string, Color)[] Writes(Rune rune) =>
                writes.Select(w => (w.Target == rune.Prefab ? "prefab" : w.Target is Material ? "instance" : "text", w.Name, w.Value)).ToArray();
            ((string, string, Color)[] Writes, int Reads) Vendor(float mode, bool text = false)
            {
                Rune sprite = Spawn(mode, text: text);
                Emit(sprite);
                Live(sprite);
                var result = (Writes(sprite), getters);
                Reset();
                return result;
            }

            Case("unpatched sprites leak one material instance each", () =>
            {
                Reset();
                Rune sprite = Spawn(1f);
                Emit(sprite);
                Material instance = assigned[sprite.Renderer!];
                Live(sprite);
                Check(clones == 1 && instance != sprite.Prefab && Alive(instance) && !destroyed.Contains(instance),
                    "the vendor never destroys the instance its material read creates");
            });
            foreach (var (effect, mode) in new[] { ("Ocean Currents", 1f), ("Decumberance", 2f) })
                Case($"patched {effect} sprites destroy only their own instance with the sprite", () =>
                {
                    Reset();
                    var vendor = Vendor(mode);
                    Install(runeMagic, new List<string>(), new List<string>());
                    Reset();
                    Rune sprite = Spawn(mode, child: true);
                    Emit(sprite);
                    Material instance = assigned[sprite.Renderer!];
                    Check(added.Count == 1 && added[0].Target == sprite.Root && added[0].Type.FullName == "DeepNorthCompat.RuneSpriteMaterial",
                        "the companion lives on the object the vendor destroys, not the renderer's child");
                    Live(sprite);
                    Check(clones == 1 && getters == vendor.Reads, "material reads and instances match the vendor");
                    Check(destroyed.Count(o => o == instance) == 1 && !Alive(instance), "the sprite's instance is destroyed exactly once");
                    Check(!destroyed.Contains(sprite.Prefab!) && Alive(sprite.Prefab!), "the prefab material survives");
                    Check(teardownReads == 0, "teardown reads no renderer material");
                    Check(Writes(sprite).SequenceEqual(vendor.Writes), "colors, emission, and fading match the vendor's writes");
                });
            Case("released when a sprite is destroyed early", () =>
            {
                Install(runeMagic, new List<string>(), new List<string>());
                Reset();
                Rune sprite = Spawn(1f);
                Emit(sprite);
                Material instance = assigned[sprite.Renderer!];
                update.Invoke(sprite.Fade, null);
                Object.Destroy(sprite.Root);
                Check(destroyed.Count(o => o == instance) == 1 && Alive(sprite.Prefab!) && teardownReads == 0,
                    "early destruction releases the instance and keeps the prefab material");
            });
            Case("leave text runes and owned materials alone", () =>
            {
                Reset();
                var vendor = Vendor(0f, text: true);
                Install(runeMagic, new List<string>(), new List<string>());
                Reset();
                Rune label = Spawn(0f, text: true);
                Emit(label);
                Live(label);
                Check(added.Count == 0 && getters == 0 && !destroyed.OfType<Material>().Any(), "a text rune takes no material");
                Check(Writes(label).SequenceEqual(vendor.Writes) && vendor.Writes.Length > 0, "text fading matches the vendor");

                Reset();
                Rune owned = Spawn(1f);
                Material own = Fake<Material>();
                colors[own] = new Dictionary<string, Color>(colors[owned.Prefab!]);
                floats[own] = new Dictionary<string, float>(floats[owned.Prefab!]);
                assigned[owned.Renderer!] = own;
                owning.Add(owned.Renderer!);
                Emit(owned);
                Live(owned);
                Check(added.Count == 0 && clones == 0 && Alive(own) && !destroyed.Contains(own),
                    "a material the renderer already owned is not taken");
            });
        }
        finally
        {
            fixture.UnpatchSelf();
            Reset();
        }
    }

    private static void Install(Assembly? vendor, List<string> messages, List<string> errors)
    {
        CompatibilityInstaller.Install(g => g == "hyleanlegend.RuneMagic" ? vendor : null, messages.Add, _ => { }, errors.Add);
        CompatibilityInstaller.Verify();
    }

    private static void Cleanup()
    {
        foreach (string owner in Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
            .Where(o => o.StartsWith("DeepNorthCompat.", StringComparison.Ordinal)).Distinct().ToArray())
            new Harmony(owner).UnpatchSelf();
    }

    private static void Reset()
    {
        objects.Clear(); attached.Clear(); parents.Clear(); assigned.Clear(); owning.Clear(); colors.Clear(); floats.Clear();
        textColors.Clear(); destroyed.Clear(); writes.Clear(); added.Clear();
        getters = clones = teardownReads = 0;
        tearingDown = false;
    }

    private static object Fake(Type type)
    {
        object value = FormatterServices.GetUninitializedObject(type);
        pointer.SetValue(value, new IntPtr(++id));
        return value;
    }

    private static T Fake<T>() where T : Object => (T)Fake(typeof(T));
    private static bool Alive(Object value) => (IntPtr)pointer.GetValue(value) != IntPtr.Zero;
    private static void Attach(Component component, GameObject target) { objects[component] = target; attached[target].Add(component); }
    private static bool Under(GameObject candidate, GameObject root)
    {
        for (GameObject? current = candidate; current is object; current = parents.TryGetValue(current, out GameObject? parent) ? parent : null)
            if (ReferenceEquals(current, root)) return true;
        return false;
    }

    private static void Hook(Harmony harmony, MethodInfo target, string prefix) =>
        harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(RuneSpriteMaterialTests), prefix)));

    private static bool GameObjectOf(Component __instance, ref GameObject __result) { __result = objects[__instance]; return false; }

    private static bool Components(GameObject __instance, Type type, bool recursive, ref Array __result)
    {
        Component[] found = attached.Where(p => recursive ? Under(p.Key, __instance) : ReferenceEquals(p.Key, __instance))
            .SelectMany(p => p.Value).Where(type.IsInstanceOfType).ToArray();
        __result = Array.CreateInstance(type, found.Length);
        Array.Copy(found, __result, found.Length);
        return false;
    }

    private static bool Add(GameObject __instance, Type componentType, ref Component __result)
    {
        __result = (Component)Fake(componentType);
        Attach(__result, __instance);
        added.Add((__instance, componentType));
        return false;
    }

    // Unity's getter returns the renderer's own instance, or clones the shared material and assigns the clone.
    private static bool MaterialOf(Renderer __instance, ref Material __result)
    {
        getters++;
        if (tearingDown) teardownReads++;
        if (owning.Add(__instance))
        {
            Material shared = assigned[__instance], clone = Fake<Material>();
            colors[clone] = new Dictionary<string, Color>(colors[shared]);
            floats[clone] = new Dictionary<string, float>(floats[shared]);
            assigned[__instance] = clone;
            clones++;
        }
        __result = assigned[__instance];
        return false;
    }

    private static bool SharedOf(Renderer __instance, ref Material __result)
    {
        if (tearingDown) teardownReads++;
        __result = assigned[__instance];
        return false;
    }

    private static bool GetColor(Material __instance, string name, ref Color __result) { __result = colors[__instance][name]; return false; }
    private static bool SetColor(Material __instance, string name, Color value) { writes.Add((__instance, name, value)); colors[__instance][name] = value; return false; }
    private static bool GetFloat(Material __instance, string name, ref float __result) { __result = floats[__instance][name]; return false; }
    private static bool Config(string key, ref float __result) { __result = config[key]; return false; }
    private static bool Delta(ref float __result) { __result = 0.5f; return false; }
    private static bool TextColor(Component __instance, ref Color __result) { __result = textColors[__instance]; return false; }
    private static bool SetTextColor(Component __instance, Color value) { writes.Add((__instance, "color", value)); textColors[__instance] = value; return false; }

    // Destroying a GameObject runs OnDestroy across its hierarchy, then marks every object there destroyed.
    private static bool Destroy(Object obj)
    {
        destroyed.Add(obj);
        if (obj is GameObject root)
        {
            GameObject[] hierarchy = attached.Keys.Where(g => Under(g, root)).ToArray();
            bool outer = tearingDown;
            tearingDown = true;
            try
            {
                foreach (Component component in hierarchy.SelectMany(g => attached[g]).ToArray())
                    AccessTools.DeclaredMethod(component.GetType(), "OnDestroy")?.Invoke(component, null);
            }
            finally { tearingDown = outer; }
            foreach (GameObject target in hierarchy)
            {
                foreach (Component component in attached[target]) pointer.SetValue(component, IntPtr.Zero);
                pointer.SetValue(target, IntPtr.Zero);
            }
        }
        else pointer.SetValue(obj, IntPtr.Zero);
        return false;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
