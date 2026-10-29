using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using DeepNorthCompat;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Executes the game and vendor methods. Only Unity object access and text rendering are fixtures.
internal static class PerformanceLookupTests
{
    private const string Fixture = "DeepNorthCompat.Tests.PerformanceLookup";
    private static readonly TranspilerGroup[] groups = { VisibleLocalizationPatch.Group, RuneRequirementPatch.Group };
    private static readonly FieldInfo pointer = AccessTools.Field(typeof(Object), "m_CachedPtr");
    private static readonly Dictionary<Component, GameObject> objects = new Dictionary<Component, GameObject>();
    private static readonly Dictionary<GameObject, Component[]> descendants = new Dictionary<GameObject, Component[]>();
    private static readonly Dictionary<GameObject, bool> active = new Dictionary<GameObject, bool>();
    private static readonly Dictionary<Object, string> names = new Dictionary<Object, string>();
    private static readonly List<(Object Label, string Text)> writes = new List<(Object, string)>();
    private static readonly List<bool> searches = new List<bool>();
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static int nameReads, objectReads, returnedLabels;
    private static bool controller;
    private static readonly MarkerStub marker = (_, __, ___, ____) => IntPtr.Zero;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MarkerStub(IntPtr name, ushort category, int flags, int metadata);

    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);

    internal static void Run(string profile, Action<string, Action> test)
    {
        void Case(string name, Action action) => test(name, () =>
        {
            Cleanup();

            try { NativeBoundaries(); action(); }
            finally { Cleanup(); }
        });

        bool server = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server";
        Assembly rune = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"),
            "RuneMagic.dll", SearchOption.AllDirectories).Single());

        if (server)
        {
            Case("performance lookups stay inactive on a dedicated server", () =>
            {
                var messages = Install(rune);
                foreach (TranspilerGroup group in groups)
                {
                    Check(messages.Any(m => m.StartsWith(group.Name + ":") && m.Contains("inactive on dedicated server")), group.Name);
                    Check(!Harmony.HasAnyPatches(group.Owner), group.Name + " installed on a server");
                }
            });

            return;
        }

        Case("visible localization refreshes current labels without collecting inactive descendants", () =>
        {
            Install(null);
            Localization localization = Localizer();
            Transform root = Attach<Transform>(true);
            Text visible = Attach<Text>(true), hidden = Attach<Text>(false), uncached = Attach<Text>(true);
            TMP_Text hint = Attach<TextMeshProUGUI>(true), hiddenHint = Attach<TextMeshProUGUI>(false);
            descendants[objects[root]] = new Component[] { visible, hidden, uncached, hint, hiddenHint };
            Set(localization, "textStrings", new Dictionary<Text, string> { [visible] = "$hello", [hidden] = "$hello" });
            Set(localization, "textMeshStrings", new Dictionary<TMP_Text, string> { [hint] = "$KEY_Use", [hiddenHint] = "$hello" });

            localization.ReLocalizeVisible(root);
            Check(writes.SequenceEqual(new[] { ((Object)visible, "Hello"), ((Object)hint, "E") }), "visible text and input hint");
            Check(searches.SequenceEqual(new[] { false, false }) && returnedLabels == 3,
                $"visible traversal collected {returnedLabels} labels; includeInactive={string.Join(",", searches)}");

            active[objects[hidden]] = true;
            active[objects[hiddenHint]] = true;
            ((Dictionary<string, string>)Get(localization, "m_translations"))["hello"] = "Bonjour";
            controller = true;
            Evict(localization);
            writes.Clear();
            localization.ReLocalizeVisible(root);
            Check(writes.SequenceEqual(new[] { ((Object)visible, "Bonjour"), ((Object)hidden, "Bonjour"),
                ((Object)hint, "A"), ((Object)hiddenHint, "Bonjour") }), "same-call activation, language and input changes");

            // Unity includes the root even when inactive. The game's final active check must survive.
            active[objects[root]] = false;
            objects[visible] = objects[root];
            descendants[objects[root]] = new Component[] { visible };
            writes.Clear();
            localization.ReLocalizeVisible(root);
            Check(writes.Count == 0, "inactive root text was changed");

            writes.Clear();
            searches.Clear();
            localization.ReLocalizeAll(root);
            Check(writes.SequenceEqual(new[] { ((Object)visible, "Bonjour") }) && searches.All(v => v),
                "ReLocalizeAll must still refresh inactive text");
        });

        Case("rune membership reads the current piece name once per lookup", () =>
        {
            Install(rune);
            Type owner = rune.GetType("ValheimMod.ValheimMod", true)!;
            Type config = rune.GetType("ValheimMod.RunemagicPieceConfig", true)!;
            FieldInfo configs = AccessTools.Field(owner, "runemagicPieceConfigs");
            object saved = configs.GetValue(null);
            var list = (IList)Activator.CreateInstance(configs.FieldType);
            for (int i = 0; i < 25; i++)
            {
                object entry = FormatterServices.GetUninitializedObject(config);
                Set(entry, "<pieceName>k__BackingField", "rune_" + i);
                list.Add(entry);
            }

            var lookup = (Func<Piece, bool>)Delegate.CreateDelegate(typeof(Func<Piece, bool>),
                AccessTools.Method(rune.GetType("ValheimMod.UnlockManager", true), "isUnlockable"));
            Piece piece = Attach<Piece>(true);

            try
            {
                configs.SetValue(null, list);
                foreach (string name in new[] { "ordinary_piece", "rune_0", "rune_12", "rune_24", "rune_24(Clone)", "RUNE_0" })
                {
                    names[objects[piece]] = name;
                    nameReads = objectReads = 0;
                    Check(lookup(piece) == (name == "rune_0" || name == "rune_12" || name == "rune_24"), "membership: " + name);
                    Check(nameReads == 1 && objectReads == 1, $"{name}: {nameReads} name reads, {objectReads} object reads");
                }

                nameReads = objectReads = 0;
                Check(!lookup(null!), "null piece");
                pointer.SetValue(piece, IntPtr.Zero);
                Check(!lookup(piece) && nameReads == 0 && objectReads == 0, "destroyed piece read a native property");

                Piece renamed = Attach<Piece>(true);
                names[objects[renamed]] = "rune_24";
                Check(lookup(renamed), "original list");
                list.RemoveAt(24);
                Check(!lookup(renamed), "changed list");
                names[objects[renamed]] = "rune_0";
                Check(lookup(renamed), "renamed object");
                list.Clear();
                Check(!lookup(renamed), "empty list");
            }
            finally { configs.SetValue(null, saved); }
        });
    }

    private static List<string> Install(Assembly? rune)
    {
        var messages = new List<string>();
        var errors = new List<string>();
        CompatibilityInstaller.Install(guid => guid == "hyleanlegend.RuneMagic" ? rune : null,
            messages.Add, _ => { }, errors.Add);
        CompatibilityInstaller.Verify();
        Check(errors.Count == 0, string.Join("\n", errors));
        return messages;
    }

    private static void NativeBoundaries()
    {
        mono_add_internal_call("Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility::CreateMarker_Injected",
            Marshal.GetFunctionPointerForDelegate(marker));
        Hook(AccessTools.PropertyGetter(typeof(Component), "gameObject"), nameof(GameObjectOf));
        Hook(AccessTools.PropertyGetter(typeof(Object), "name"), nameof(Name));
        Hook(AccessTools.PropertyGetter(typeof(GameObject), "activeInHierarchy"), nameof(Active));
        Hook(AccessTools.Method(typeof(GameObject), "GetComponentsInternal"), nameof(Components));
        Hook(AccessTools.PropertySetter(typeof(Text), "text"), nameof(TextWrite));
        Hook(AccessTools.PropertySetter(typeof(TMP_Text), "text"), nameof(TextWrite));
        Hook(AccessTools.Method(typeof(ZInput), "IsGamepadActive"), nameof(Controller));
        Hook(AccessTools.Method(typeof(Localization), "GetBoundKeyString"), nameof(Binding));
    }

    private static Localization Localizer()
    {
        var result = (Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
        Set(result, "m_stringBuilder", new StringBuilder());
        Set(result, "m_endChars", " (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
        Set(result, "m_translations", new Dictionary<string, string> { ["hello"] = "Hello" });
        Set(result, "m_cache", Activator.CreateInstance(AccessTools.Field(typeof(Localization), "m_cache").FieldType, 100));
        return result;
    }

    private static void Evict(Localization localization)
    {
        object cache = Get(localization, "m_cache");
        AccessTools.Method(cache.GetType(), "EvictAll").Invoke(cache, null);
    }

    private static T Fake<T>() where T : Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr address = Marshal.AllocHGlobal(sizeof(long));
        Marshal.WriteInt32(address, pointers.Count + 1);
        pointers.Add(address);
        pointer.SetValue(value, address);
        return value;
    }

    private static T Attach<T>(bool visible) where T : Component
    {
        T component = Fake<T>();
        GameObject gameObject = Fake<GameObject>();
        objects[component] = gameObject;
        active[gameObject] = visible;
        return component;
    }

    private static bool GameObjectOf(Component __instance, ref GameObject __result)
    {
        objectReads++;
        __result = objects[__instance];
        return false;
    }

    private static bool Name(Object __instance, ref string __result)
    {
        nameReads++;
        __result = names[__instance];
        return false;
    }

    private static bool Active(GameObject __instance, ref bool __result) { __result = active[__instance]; return false; }

    // Assumes Unity's documented traversal: includeInactive: false skips inactive descendants but always
    // returns the root's own components. The offline suite can't confirm native behavior.
    private static bool Components(GameObject __instance, Type type, bool includeInactive, ref Array __result)
    {
        searches.Add(includeInactive);
        Component[] found = descendants[__instance].Where(type.IsInstanceOfType)
            .Where(c => includeInactive || active[objects[c]] || ReferenceEquals(objects[c], __instance)).ToArray();
        __result = Array.CreateInstance(type, found.Length);
        Array.Copy(found, __result, found.Length);
        returnedLabels += found.Length;
        return false;
    }

    private static bool TextWrite(Object __instance, string value) { writes.Add((__instance, value)); return false; }
    private static bool Controller(ref bool __result) { __result = controller; return false; }
    private static bool Binding(string bindingName, ref string __result) { __result = bindingName == "JoyUse" ? "A" : "E"; return false; }
    private static void Set(object value, string field, object data) => AccessTools.Field(value.GetType(), field).SetValue(value, data);
    private static object Get(object value, string field) => AccessTools.Field(value.GetType(), field).GetValue(value);
    private static void Hook(MethodInfo method, string prefix) => new Harmony(Fixture).Patch(method,
        prefix: new HarmonyMethod(AccessTools.Method(typeof(PerformanceLookupTests), prefix)));

    private static void Cleanup()
    {
        foreach (TranspilerGroup group in groups) new Harmony(group.Owner).UnpatchSelf();
        new Harmony(Fixture).UnpatchSelf();

        objects.Clear(); descendants.Clear(); active.Clear(); names.Clear(); writes.Clear(); searches.Clear();
        foreach (IntPtr address in pointers) Marshal.FreeHGlobal(address);
        pointers.Clear();
        nameReads = objectReads = returnedLabels = 0;
        controller = false;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
