using System;
using System.Collections;
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

// Executes the installed vendor's pagination/disposal block. Unity hierarchy access and
// deferred destruction are native fixtures; page selection and cleanup remain real IL.
internal static class AaaRecipeRowsTests
{
    private const string Owner = "DeepNorthCompat.UI.AAARecipeRows", Fixture = "DeepNorthCompat.Tests.AAARecipeRows";
    private static readonly Dictionary<Transform, GameObject> Objects = new Dictionary<Transform, GameObject>();
    private static readonly Dictionary<Transform, List<Transform>> Children = new Dictionary<Transform, List<Transform>>();
    private static readonly Dictionary<GameObject, bool> Active = new Dictionary<GameObject, bool>();
    private static readonly HashSet<GameObject> Pending = new HashSet<GameObject>();
    private static readonly MethodInfo SetActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
    private static readonly MethodInfo Destroy = AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object) });
    private static readonly Type Pair = AccessTools.Inner(typeof(InventoryGui), "RecipeDataPair"), PairList = typeof(List<>).MakeGenericType(Pair);
    private static readonly PropertyInfo[] PairProperties = new[] { "InterfaceElement", "Recipe", "ItemData", "CanCraft" }
        .Select(name => AccessTools.Property(Pair, name)).ToArray();
    private static int identity;

    internal static void Run(string profile, Action<string, Action> test)
    {
        // Existing suites remove every compatibility owner without resetting other groups' flags.
        // Start this fixture in the same fresh state as its standalone reproduction.
        CleanupPatch();
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server") { Server(profile, test); return; }
        Assembly vendor = Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/Azumatt-AAA_Crafting/AzuAntiArthriticCrafting.dll"));
        MethodInfo target = AccessTools.DeclaredMethod(vendor.GetType(
            "AzuAntiArthriticCrafting.Patches.PaginatorPatches+InventoryGuiUpdateRecipeListPatch", true), "Postfix");
        using var aaa = new AaaCraftingFixture(vendor);
        var native = new Harmony(Fixture);
        native.Patch(SetActive, transpiler: Hook(nameof(ActivationBody)));
        native.Patch(Destroy, prefix: Hook(nameof(QueueDestroy)));
        try
        {
            test("AAA recipe rows stay bounded across refreshes, filters and page changes", () =>
            {
                CompatibilityInstaller.Install(guid => guid == "Azumatt.AzuAntiArthriticCrafting" ? vendor : null,
                    _ => { }, _ => { }, error => throw new Exception(error));
                CompatibilityInstaller.Verify();
                var paginate = Pagination(target);
                RectTransform root = Fake<RectTransform>();
                Children[root] = new List<Transform>();
                for (int refresh = 0; refresh < 30; refresh++)
                {
                    object[] rows = Enumerable.Range(0, 7).Select(_ => Row(root)).ToArray();
                    // These inputs are the already sorted/filtered rows at this vendor boundary.
                    IList filtered = List(refresh % 3 == 1 ? new[] { rows[5], rows[1], rows[3] }
                        : refresh % 3 == 2 ? Array.Empty<object>() : rows);
                    object[] expected = refresh % 3 == 1 ? new[] { rows[1], rows[3] }
                        : refresh % 3 == 2 ? Array.Empty<object>() : new[] { rows[1], rows[2] };
                    IList available = List(rows);
                    IList result = paginate(root, available, filtered, 1, 2);
                    Check(ReferenceEquals(result, available) && result.Count == expected.Length,
                        "vendor keeps its list instance and the selected page");
                    for (int i = 0; i < expected.Length; i++)
                        Check(PairProperties.All(property => Equals(property.GetValue(result[i]), property.GetValue(expected[i]))),
                            "page order, item references and craftability remain intact");
                    foreach (object row in expected) Check(Active[Element(row)] && !Pending.Contains(Element(row)),
                        "selected rows remain active and are never scheduled for destruction");
                    EndFrame(root);
                    Check(new HashSet<GameObject>(Children[root].Select(child => Objects[child])).SetEquals(expected.Select(Element)),
                        "discarded rows survived pagination refresh " + refresh);
                }
            });
            GuardCases(vendor, target, test);
        }
        finally
        {
            native.UnpatchSelf();
            CleanupPatch();
            Objects.Clear(); Children.Clear(); Active.Clear(); Pending.Clear();
        }
    }

    private static void CleanupPatch()
    {
        new Harmony(Owner).UnpatchSelf();
        Type? patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.AaaRecipeRowsPatch");
        if (patch != null)
            foreach (string field in new[] { "pending", "enabled" }) AccessTools.Field(patch, field).SetValue(null, false);
    }

    private static void Server(string profile, Action<string, Action> test)
    {
        test("AAA recipe rows remain inactive on dedicated servers", () =>
        {
            Assembly vendor = Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/Azumatt-AAA_Crafting/AzuAntiArthriticCrafting.dll"));
            var messages = new List<string>();
            CompatibilityInstaller.Install(guid => guid == "Azumatt.AzuAntiArthriticCrafting" ? vendor : null,
                messages.Add, _ => { }, error => throw new Exception(error));
            CompatibilityInstaller.Verify();
            Check(!Harmony.HasAnyPatches(Owner) && messages.Any(message => message.StartsWith("UI.AAARecipeRows: client-only")),
                "dedicated startup must not install client UI cleanup");
        });
    }

    private static void GuardCases(Assembly vendor, MethodInfo target, Action<string, Action> test)
    {
        const string other = Fixture + ".Other";
        Type patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.AaaRecipeRowsPatch", true)!;
        FieldInfo infoSink = AccessTools.Field(typeof(CompatibilityInstaller), "Info"), errorSink = AccessTools.Field(typeof(CompatibilityInstaller), "Error");
        void Reset()
        {
            new Harmony(Owner).UnpatchSelf(); new Harmony(other).UnpatchSelf();
            foreach (string field in new[] { "pending", "enabled" }) AccessTools.Field(patch, field).SetValue(null, false);
        }
        void Case(string name, Action<List<string>, List<string>> action) => test("AAA recipe rows " + name, () =>
        {
            Reset();
            var messages = new List<string>(); var errors = new List<string>();
            object previousInfo = infoSink.GetValue(null), previousError = errorSink.GetValue(null);
            infoSink.SetValue(null, (Action<string>)messages.Add); errorSink.SetValue(null, (Action<string>)errors.Add);
            try { action(messages, errors); }
            finally { Reset(); infoSink.SetValue(null, previousInfo); errorSink.SetValue(null, previousError); }
        });
        void Prepare(Assembly? assembly) => AccessTools.Method(patch, "Prepare").Invoke(null, new object?[] { assembly });
        void Verify() => AccessTools.Method(patch, "Verify").Invoke(null, null);

        Case("stay inactive without AAA and reject an unaudited vendor", (messages, errors) =>
        {
            Prepare(null);
            Check(messages.Any(message => message.StartsWith("UI.AAARecipeRows: optional mod absent")) && !Harmony.HasAnyPatches(Owner),
                "absence leaves no vendor hook");
            try { Prepare(typeof(Plugin).Assembly); throw new Exception("unsupported vendor was accepted"); }
            catch (TargetInvocationException exception) { Check(exception.InnerException is NotSupportedException, "exact vendor hash must reject the build"); }
            Check(!Harmony.HasAnyPatches(Owner), "an unsupported build must leave no hook");
        });
        Case("reject a changed discard branch and roll back only their own hook", (messages, errors) =>
        {
            new Harmony(other).Patch(target, transpiler: Hook(nameof(ChangeDiscard)));
            Prepare(vendor); Verify();
            Check(errors.Count(message => message.StartsWith("UI.AAARecipeRows: NOT APPLIED")) == 1 && !Harmony.HasAnyPatches(Owner),
                "changed pagination must fail verification and remove its cleanup hook");
            Check(Harmony.GetPatchInfo(target).Owners.Contains(other), "foreign edit must survive rollback");
        });
        Case("reject a removed registration before reporting application", (messages, errors) =>
        {
            Prepare(vendor);
            new Harmony(Owner).UnpatchSelf();
            Verify();
            Check(!messages.Any(message => message.StartsWith("UI.AAARecipeRows: APPLIED"))
                && errors.Any(message => message.StartsWith("UI.AAARecipeRows: NOT APPLIED")), "verification requires the installed registration");
        });
        Case("disable cleanup after a later incompatible Harmony rewrite", (messages, errors) =>
        {
            Prepare(vendor); Verify();
            Check(messages.Count(message => message.StartsWith("UI.AAARecipeRows: APPLIED")) == 1, "cleanup applies once");
            Verify();
            Check(messages.Count(message => message.StartsWith("UI.AAARecipeRows: APPLIED")) == 1, "verification is idempotent");
            var previousBody = Pagination(target);
            new Harmony(other).Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(AaaRecipeRowsTests), nameof(ChangeDiscard)))
                { priority = Priority.First });
            Check(errors.Count(message => message.StartsWith("UI.AAARecipeRows: NO LONGER APPLIED")) == 1, "lost rewrite must be reported");
            Pending.Clear();
            RectTransform root = Fake<RectTransform>(); Children[root] = new List<Transform>();
            object[] rows = Enumerable.Range(0, 3).Select(_ => Row(root)).ToArray();
            IList available = List(rows);
            previousBody(root, available, List(rows), 0, 1);
            Check(Pending.Count == 0 && Children[root].Count == 3 && Active[Element(rows[0])]
                && !Active[Element(rows[1])] && !Active[Element(rows[2])], "cached cleanup calls must retain vendor visibility without deleting rows after loss");
        });
    }

    // A valid foreign edit separates the Boolean argument from its SetActive call.
    private static IEnumerable<CodeInstruction> ChangeDiscard(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int contains = code.FindIndex(i => i.operand is MethodInfo method && method.DeclaringType == typeof(HashSet<GameObject>) && method.Name == "Contains");
        Check(contains >= 0, "vendor's discarded-page membership check exists");
        code.Insert(contains + 4, new CodeInstruction(OpCodes.Nop));
        return code;
    }

    private static object Row(Transform root)
    {
        GameObject row = Fake<GameObject>();
        Transform transform = Fake<RectTransform>();
        Objects[transform] = row;
        Children[root].Add(transform);
        Active[row] = true;
        object pair = Activator.CreateInstance(Pair)!;
        object[] values = { row, Fake<Recipe>(), new ItemDrop.ItemData(), identity % 2 == 0 };
        for (int i = 0; i < values.Length; i++) PairProperties[i].SetValue(pair, values[i]);
        return pair;
    }

    private static GameObject Element(object pair) => (GameObject)PairProperties[0].GetValue(pair);
    private static IList List(IEnumerable<object> pairs)
    {
        var list = (IList)Activator.CreateInstance(PairList)!;
        foreach (object pair in pairs) list.Add(pair);
        return list;
    }

    private static T Fake<T>() where T : Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, new IntPtr(++identity));
        return value;
    }

    private static void EndFrame(Transform root)
    {
        Children[root].RemoveAll(child => Pending.Contains(Objects[child]));
        Pending.Clear();
    }

    private static void Activation(GameObject row, bool active) => Active[row] = active;
    private static bool QueueDestroy(Object __0)
    {
        var row = (GameObject)__0;
        Check(!Active[row], "discarded rows must be hidden before deferred destruction");
        Pending.Add(row);
        return false;
    }

    private static IEnumerable<CodeInstruction> ActivationBody() => new[]
    {
        new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AaaRecipeRowsTests), nameof(Activation))),
        new CodeInstruction(OpCodes.Ret)
    };

    private static int Count(Transform root) => Children[root].Count;
    private static Transform Child(Transform root, int index) => Children[root][index];
    private static GameObject GameObjectOf(Component component) => Objects[(Transform)component];

    private static Func<RectTransform, IList, IList, int, int, IList> Pagination(MethodInfo target)
    {
        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(target);
        int start = code.FindIndex(i => i.operand is MethodInfo m && m.Name == "Skip" && m.GetGenericArguments().SequenceEqual(new[] { Pair })) - 2;
        Check(start >= 0, "installed vendor page selection exists");
        int end = code.FindIndex(start, i => i.operand is MethodInfo m && m.DeclaringType == PairList && m.Name == "AddRange");
        Check(end > start, "installed vendor pagination block exists");
        var method = new DynamicMethod("AaaPagination", typeof(IList),
            new[] { typeof(RectTransform), typeof(IList), typeof(IList), typeof(int), typeof(int) }, typeof(AaaRecipeRowsTests), true);
        ILGenerator il = method.GetILGenerator();
        LocalBuilder[] locals = target.GetMethodBody()!.LocalVariables.Select(local => il.DeclareLocal(local.LocalType)).ToArray();
        foreach (var argument in new (int Argument, int Local)[] { (0, 0), (1, 1), (2, 3), (3, 8), (4, 6) })
        {
            il.Emit(OpCodes.Ldarg, (short)argument.Argument);
            if (argument.Argument == 1 || argument.Argument == 2) il.Emit(OpCodes.Castclass, PairList);
            il.Emit(OpCodes.Stloc, locals[argument.Local]);
        }
        CodeInstruction[] block = code.Skip(start).Take(end - start + 1).ToArray();
        var labels = block.SelectMany(i => i.labels).Distinct().ToDictionary(label => label, _ => il.DefineLabel());
        var natives = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.childCount))] = AccessTools.Method(typeof(AaaRecipeRowsTests), nameof(Count)),
            [AccessTools.Method(typeof(Transform), nameof(Transform.GetChild))] = AccessTools.Method(typeof(AaaRecipeRowsTests), nameof(Child)),
            [AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject))] = AccessTools.Method(typeof(AaaRecipeRowsTests), nameof(GameObjectOf))
        };
        foreach (CodeInstruction instruction in block)
        {
            foreach (Label label in instruction.labels) il.MarkLabel(labels[label]);
            OpCode op = instruction.opcode;
            switch (instruction.operand)
            {
                case null: il.Emit(op); break;
                case LocalBuilder local: il.Emit(op, locals[local.LocalIndex]); break;
                case Label label: il.Emit(op, labels[label]); break;
                case MethodInfo call:
                    if (natives.TryGetValue(call, out MethodInfo replacement)) il.Emit(OpCodes.Call, replacement);
                    else il.Emit(op, call);
                    break;
                case ConstructorInfo constructor: il.Emit(op, constructor); break;
                case FieldInfo field: il.Emit(op, field); break;
                case int number: il.Emit(op, number); break;
                case byte number: il.Emit(op, number); break;
                default: throw new NotSupportedException("Unexpected pagination instruction: " + instruction);
            }
        }
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ret);
        return (Func<RectTransform, IList, IList, int, int, IList>)method.CreateDelegate(
            typeof(Func<RectTransform, IList, IList, int, int, IList>));
    }

    private static HarmonyMethod Hook(string method) => new HarmonyMethod(AccessTools.Method(typeof(AaaRecipeRowsTests), method));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
