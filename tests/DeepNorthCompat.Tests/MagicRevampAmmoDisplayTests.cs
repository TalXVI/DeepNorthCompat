using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;

internal static class MagicRevampAmmoDisplayTests
{
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevamp.AmmoDisplay";
    private const string Later = Fixture + ".Later";
    private static readonly List<string> Errors = new List<string>();
    private static MethodInfo target = null!, currentAmmo = null!;
    private static Inventory? book;
    private static ItemDrop.ItemData weapon = null!;
    private static int observed, selectedCalls, countCalls, labelCalls;

    internal static void Run(string profile, Action<string, Action> test)
    {
        Reset();
        test("MagicRevamp ammo display stays inactive without its mods", () =>
        {
            MagicRevampAmmoDisplayPatch.Prepare(null, null, null);
            MagicRevampAmmoDisplayPatch.Verify();
            Check(!Harmony.HasAnyPatches(MagicRevampAmmoDisplayPatch.Owner), "absent dependencies installed a patch");
        });

        string? magicPath = Find(profile, "MagicRevamp.dll");
        string? backpacksPath = Find(profile, "Backpacks.dll");
        string? uiPath = Find(profile, "MyLittleUI.dll");
        if (magicPath == null || backpacksPath == null || uiPath == null) return;
        Assembly magic = Assembly.LoadFrom(magicPath), backpacks = Assembly.LoadFrom(backpacksPath), ui = Assembly.LoadFrom(uiPath);
        test("MagicRevamp ammo display stays inactive when any dependency is absent", () =>
        {
            foreach (int missing in new[] { 0, 1, 2 })
            {
                MagicRevampAmmoDisplayPatch.Prepare(missing == 0 ? null : magic, missing == 1 ? null : backpacks, missing == 2 ? null : ui);
                MagicRevampAmmoDisplayPatch.Verify();
                Check(!Harmony.HasAnyPatches(MagicRevampAmmoDisplayPatch.Owner), "missing dependency installed a patch");
            }
        });
        test("MagicRevamp ammo display rejects unsupported builds", () =>
        {
            foreach (int wrong in new[] { 0, 1, 2 })
            {
                bool rejected = false;
                try
                {
                    MagicRevampAmmoDisplayPatch.Prepare(wrong == 0 ? typeof(Plugin).Assembly : magic,
                        wrong == 1 ? typeof(Plugin).Assembly : backpacks, wrong == 2 ? typeof(Plugin).Assembly : ui);
                }
                catch (NotSupportedException) { rejected = true; }
                Check(rejected && !Harmony.HasAnyPatches(MagicRevampAmmoDisplayPatch.Owner), "unsupported build accepted");
            }
        });

        Action<string> error = CompatibilityInstaller.Error;
        CompatibilityInstaller.Error = Errors.Add;
        try { Cases(profile, magic, backpacks, ui, test); }
        finally
        {
            Reset();
            CompatibilityInstaller.Error = error;
            Player.m_localPlayer = null;
            book = null;
            Errors.Clear();
        }
    }

    internal static void RunServer(string profile, Action<string, Action> test)
    {
        test("MagicRevamp ammo display stays inactive without its mods on a dedicated server", () =>
        {
            MagicRevampAmmoDisplayPatch.Reset();
            MagicRevampAmmoDisplayPatch.Prepare(null, null, null);
            MagicRevampAmmoDisplayPatch.Verify();
            Check(!Harmony.HasAnyPatches(MagicRevampAmmoDisplayPatch.Owner), "absent dependencies installed a server patch");
        });
        string? magic = Find(profile, "MagicRevamp.dll");
        string? backpacks = Find(profile, "Backpacks.dll");
        string? ui = Find(profile, "MyLittleUI.dll");
        if (magic == null || backpacks == null || ui == null) return;
        test("MagicRevamp ammo display stays inactive on a dedicated server", () =>
        {
            MagicRevampAmmoDisplayPatch.Reset();
            MagicRevampAmmoDisplayPatch.Prepare(Assembly.LoadFrom(magic), Assembly.LoadFrom(backpacks), Assembly.LoadFrom(ui));
            MagicRevampAmmoDisplayPatch.Verify();
            Check(!Harmony.HasAnyPatches(MagicRevampAmmoDisplayPatch.Owner), "server installed a client display patch");
        });
    }

    private static void Cases(string profile, Assembly magic, Assembly backpacks, Assembly ui, Action<string, Action> test)
    {
        Type cellType = ui.GetType("MyLittleUI.AmmoCountIcon+ElementExtraData", true)!;
        target = AccessTools.Method(cellType, "UpdateState");
        currentAmmo = AccessTools.Method(cellType, "GetCurrentAmmo");
        Type cycle = magic.GetType("MagicRevamp.Functions.CycleSpells", true)!;
        Type vendorPatch = magic.GetType("MagicRevamp.Patches.InventoryPatch", true)!;
        var environment = new Harmony(Fixture);
        var vendor = new Harmony(MagicRevampInventoryPatch.Guid);
        environment.Patch(AccessTools.Method(cycle, "SpellBookContainer"), prefix: Hook(nameof(Book)));
        environment.Patch(AccessTools.Method(typeof(Humanoid), "GetCurrentWeapon"), prefix: Hook(nameof(Weapon)));
        environment.Patch(AccessTools.Method(typeof(ZLog), "Log", new[] { typeof(object) }), prefix: Hook(nameof(Skip)));
        vendor.Patch(AccessTools.Method(typeof(Inventory), "ContainsItem"), postfix: new HarmonyMethod(AccessTools.Method(vendorPatch, "ContainsItem_Postfix")));
        vendor.Patch(AccessTools.Method(typeof(Inventory), "GetAmmoItem"), postfix: new HarmonyMethod(AccessTools.Method(vendorPatch, "GetAmmoItem_Postfix")));
        vendor.Patch(AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(ItemDrop.ItemData), typeof(int) }),
            postfix: new HarmonyMethod(AccessTools.Method(vendorPatch, "RemoveItem_Postfix")));

        test("MagicRevamp ammo display requires verified inventory hooks", () =>
        {
            Errors.Clear();
            MagicRevampAmmoDisplayPatch.Prepare(magic, backpacks, ui);
            MagicRevampAmmoDisplayPatch.Verify();
            Check(!Harmony.HasAnyPatches(MagicRevampAmmoDisplayPatch.Owner) && Errors.Any(e => e.Contains("NOT APPLIED")),
                "unverified inventory dependency was accepted");
        });
        MagicRevampInventoryPatch.Prepare(magic, backpacks);
        MagicRevampInventoryPatch.Verify();
        Check(MagicRevampInventoryPatch.Ready, "inventory fixture did not verify");
        InitializeUi(profile);
        test("MagicRevamp ammo display rejects ambiguous or unsafe count sites", () =>
        {
            MagicRevampAmmoDisplayPatch.Prepare(magic, backpacks, ui);
            MethodInfo count = AccessTools.Method(typeof(Inventory), "CountItems");
            for (int variant = 0; variant < 3; variant++)
            {
                List<CodeInstruction> source = PatchProcessor.GetOriginalInstructions(target);
                int call = source.FindIndex(i => i.Calls(count));
                Check(call >= 0, "vendor count missing");
                if (variant == 0) source[call].labels.Add(default);
                else if (variant == 1) source.Insert(call, new CodeInstruction(source[call]));
                else source[call - 1] = new CodeInstruction(OpCodes.Nop);
                List<CodeInstruction> result = MagicRevampAmmoDisplayPatch.Rewrite(source).ToList();
                Check(source.Count == result.Count && source.Zip(result, (a, b) => a.opcode == b.opcode
                    && Equals(a.operand, b.operand) && a.labels.SequenceEqual(b.labels) && a.blocks.SequenceEqual(b.blocks)).All(v => v),
                    "unsafe or ambiguous input was modified for variant " + variant);
            }
            MagicRevampAmmoDisplayPatch.Reset();
        });

        IntPtr id = Marshal.AllocHGlobal(sizeof(long)), remoteId = Marshal.AllocHGlobal(sizeof(long));
        int previousWorld = Game.m_worldLevel;
        try
        {
            Marshal.WriteInt32(id, 1);
            Marshal.WriteInt32(remoteId, 2);
            var player = Fake<Player>(id);
            Player.m_localPlayer = player;
            Inventory inventory = new Inventory("player", null, 8, 1);
            book = new Inventory("book", null, 6, 1);
            AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory);
            weapon = Item("staff", 1, "$ammo_spells", ItemDrop.ItemData.ItemType.OneHandedWeapon);
            object cell = FormatterServices.GetUninitializedObject(cellType);
            AccessTools.Field(cellType, "m_item").SetValue(cell, weapon);
            ItemDrop.ItemData spell = Item("$bmr_scroll_test", 7, "$ammo_spells");
            ItemDrop.ItemData another = Item("$bmr_scroll_test", 3, "$ammo_spells");
            ItemDrop.ItemData oldWorld = Item("$bmr_scroll_test", 9, "$ammo_spells");
            spell.m_worldLevel = another.m_worldLevel = 2;
            oldWorld.m_worldLevel = 1;
            spell.m_quality = 1; another.m_quality = 2;
            book.GetAllItems().AddRange(new[] { spell, another, oldWorld });
            AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(player, spell);
            Game.m_worldLevel = 2;

            // Run the vendor selection and count instructions in their original order. The
            // fixture cuts only scene rendering and records the computed count after stloc.2.
            environment.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampAmmoDisplayTests), nameof(ObserveCount)))
            { priority = Priority.Last, after = new[] { MyLittleUiAmmoPatch.Owner, MagicRevampAmmoDisplayPatch.Owner } });
            test("MyLittleUI counts zero for a selected book-only spell before repair", () =>
            {
                Update(cell, player);
                Check(observed == 0 && countCalls == 2 && selectedCalls == 0, "vendor did not reproduce the hidden book-only count");
            });

            test("MagicRevamp ammo display follows the selected inventory", () =>
            {
                Errors.Clear();
                MyLittleUiAmmoPatch.Prepare(ui);
                MyLittleUiAmmoPatch.Verify();
                MagicRevampAmmoDisplayPatch.Prepare(magic, backpacks, ui);
                MagicRevampAmmoDisplayPatch.Verify();
                Check(Errors.Count == 0, string.Join(" | ", Errors));
                Update(cell, player);
                Check(observed == 10 && selectedCalls == 1 && countCalls == 1 && labelCalls == 1,
                    "book count did not preserve world-level filter or compose with the label rewrite");
                ItemDrop.ItemData playerSpell = Item("$bmr_scroll_test", 5, "$ammo_spells");
                playerSpell.m_worldLevel = 2;
                inventory.GetAllItems().Add(playerSpell);
                Update(cell, player);
                Check(observed == 10, "book and player counts were combined");
                AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(player, playerSpell);
                Update(cell, player);
                Check(observed == 5, "player-owned spell used the book count");
                book = null;
                Update(cell, player);
                Check(observed == 5, "missing book changed the player count");
                book = new Inventory("book", null, 6, 1);
                book.GetAllItems().AddRange(new[] { spell, another, oldWorld });
                AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(player, null);
                Update(cell, player);
                Check(observed == 10, "automatic spell selection did not use the book count");
                Player remote = Fake<Player>(remoteId);
                Inventory remoteInventory = new Inventory("remote", null, 8, 1);
                ItemDrop.ItemData remoteSpell = Item("$bmr_scroll_test", 4, "$ammo_spells");
                remoteSpell.m_worldLevel = 2;
                remoteInventory.GetAllItems().Add(remoteSpell);
                AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(remote, remoteInventory);
                AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(remote, remoteSpell);
                Update(cell, remote);
                Check(observed == 4, "remote player borrowed the local spellbook count");
                ItemDrop.ItemData arrow = Item("arrow", 11, "$ammo_arrows");
                arrow.m_worldLevel = 2;
                inventory.GetAllItems().Add(arrow);
                AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(player, arrow);
                AccessTools.Field(cellType, "m_item").SetValue(cell, Item("bow", 1, "$ammo_arrows", ItemDrop.ItemData.ItemType.Bow));
                Update(cell, player);
                Check(observed == 11, "ordinary arrow count changed");
                AccessTools.Field(cellType, "m_item").SetValue(cell, weapon);
                AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(player, spell);
            });

            test("MagicRevamp ammo display composes when label optimization installs later", () =>
            {
                ResetUi();
                MagicRevampAmmoDisplayPatch.Reset();
                MagicRevampAmmoDisplayPatch.Prepare(magic, backpacks, ui);
                MagicRevampAmmoDisplayPatch.Verify();
                MyLittleUiAmmoPatch.Prepare(ui);
                MyLittleUiAmmoPatch.Verify();
                Update(cell, player);
                Check(observed == 10 && selectedCalls == 1 && countCalls == 1 && labelCalls == 1,
                    "opposite installation order changed the count or label rewrite");
                var later = new Harmony(Later);
                later.Patch(target, prefix: Hook(nameof(Noop)));
                Update(cell, player);
                Check(observed == 10 && selectedCalls == 1 && countCalls == 1 && labelCalls == 1, "later patch lost a rewrite");
                later.UnpatchSelf();
                Update(cell, player);
                Check(observed == 10 && selectedCalls == 1 && countCalls == 1 && labelCalls == 1, "later unpatch lost a rewrite");
            });

            test("MagicRevamp ammo display disables when its inventory dependency is lost", () =>
            {
                MagicRevampInventoryPatch.Reset();
                Errors.Clear();
                Update(cell, player);
                Check(observed == 5 && selectedCalls == 1 && Errors.Count(e => e.Contains("NO LONGER APPLIED")) == 1,
                    "lost inventory dependency did not retain the original player count");
                MagicRevampInventoryPatch.Prepare(magic, backpacks);
                MagicRevampInventoryPatch.Verify();
                Update(cell, player);
                Check(observed == 5 && Errors.Count(e => e.Contains("NO LONGER APPLIED")) == 1,
                    "display silently reactivated after the dependency returned");
                MagicRevampAmmoDisplayPatch.Reset();
                MagicRevampAmmoDisplayPatch.Prepare(magic, backpacks, ui);
                MagicRevampAmmoDisplayPatch.Verify();
                Update(cell, player);
                Check(observed == 10, "display did not return after explicit reinstallation");
            });

            test("MagicRevamp ammo display rejects later changed IL", () =>
            {
                Errors.Clear();
                var later = new Harmony(Later);
                later.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampAmmoDisplayTests), nameof(ChangeBranch)))
                { priority = Priority.First, before = new[] { MyLittleUiAmmoPatch.Owner, MagicRevampAmmoDisplayPatch.Owner } });
                Update(cell, player);
                Check(observed == 5 && selectedCalls == 0 && Errors.Count(e => e.Contains("NO LONGER APPLIED")) == 1,
                    $"changed IL did not disable the repair and retain MyLittleUI's count: {observed}, {selectedCalls}, {string.Join(" | ", Errors)}");
                later.UnpatchSelf();
                Update(cell, player);
                Check(observed == 5 && selectedCalls == 0 && Errors.Count(e => e.Contains("NO LONGER APPLIED")) == 1,
                    "lost rewrite silently reactivated");
            });
        }
        finally
        {
            Game.m_worldLevel = previousWorld;
            Player.m_localPlayer = null;
            Marshal.FreeHGlobal(id);
            Marshal.FreeHGlobal(remoteId);
        }
    }

    private static void InitializeUi(string profile)
    {
        AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
        {
            typeof(MagicRevampAmmoDisplayTests).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
            Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_MANAGED_PATH")!, new[] { Path.Combine(profile, "BepInEx/core") }
        });
        Type ccs = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ConditionalConfigSync.ConditionalConfigSync"))
            .FirstOrDefault(t => t != null)
            ?? Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/shudnal-ConditionalConfigSync/ConditionalConfigSync.dll"))
                .GetType("ConditionalConfigSync.ConditionalConfigSync", true)!;
        AccessTools.Field(ccs, "runtimeInitialized").SetValue(null, true);
        mono_add_internal_call("Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility::CreateMarker_Injected",
            Marshal.GetFunctionPointerForDelegate(markerStub));
        mono_add_internal_call("UnityEngine.LayerMask::NameToLayer_Injected", Marshal.GetFunctionPointerForDelegate(layerStub));
    }

    private static IEnumerable<CodeInstruction> ObserveCount(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.Select(i => new CodeInstruction(i)).ToList();
        MethodInfo selected = ((Func<Inventory, string, int, bool, Player, ItemDrop.ItemData, int>)MagicRevampAmmoDisplayPatch.SelectedCount).Method;
        MethodInfo count = AccessTools.Method(typeof(Inventory), "CountItems");
        selectedCalls = code.Count(i => i.Calls(selected));
        countCalls = code.Count(i => i.Calls(count));
        labelCalls = code.Count(i => i.operand is MethodInfo method && method.DeclaringType == typeof(MyLittleUiAmmoPatch));
        int call = code.FindIndex(i => i.Calls(currentAmmo));
        int store = code.FindIndex(call, i => i.opcode == OpCodes.Stloc_2);
        if (call < 5 || store < call) throw new InvalidOperationException("MyLittleUI count fixture shape changed");
        List<CodeInstruction> slice = code.GetRange(call - 5, store - call + 6);
        slice.Add(new CodeInstruction(OpCodes.Ldloc_2));
        slice.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MagicRevampAmmoDisplayTests), nameof(Record))));
        slice.Add(new CodeInstruction(OpCodes.Ret));
        return slice;
    }

    private static IEnumerable<CodeInstruction> ChangeBranch(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.Select(i => new CodeInstruction(i)).ToList();
        int call = code.FindIndex(i => i.Calls(currentAmmo));
        if (call < 0 || code[call + 1].opcode != OpCodes.Stloc_1) throw new InvalidOperationException("branch fixture changed");
        code.Insert(call + 2, new CodeInstruction(OpCodes.Nop));
        return code;
    }

    private static void Update(object cell, Player player) => target.Invoke(cell, new object?[] { null, player });
    private static string? Find(string profile, string name) =>
        Directory.EnumerateFiles(Path.Combine(profile, "BepInEx/plugins"), name, SearchOption.AllDirectories).SingleOrDefault();
    private static void Record(int count) => observed = count;
    private static bool Book(ref Inventory? __result) { __result = book; return false; }
    private static bool Weapon(ref ItemDrop.ItemData __result) { __result = weapon; return false; }
    private static bool Skip() => false;
    private static void Noop() { }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampAmmoDisplayTests), name));
    private static ItemDrop.ItemData Item(string name, int count, string ammo, ItemDrop.ItemData.ItemType type = ItemDrop.ItemData.ItemType.Ammo) =>
        new ItemDrop.ItemData { m_stack = count, m_shared = new ItemDrop.ItemData.SharedData { m_name = name, m_ammoType = ammo, m_itemType = type } };

    private static T Fake<T>(IntPtr id) where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, id);
        return value;
    }

    private static void ResetUi() => MyLittleUiAmmoPatch.Reset();

    private static void Reset()
    {
        new Harmony(Later).UnpatchSelf();
        new Harmony(Fixture).UnpatchSelf();
        MagicRevampAmmoDisplayPatch.Reset();
        ResetUi();
        MagicRevampInventoryPatch.Reset();
        new Harmony(MagicRevampInventoryPatch.Guid).UnpatchSelf();
    }

    private static readonly NativeStub markerStub = Marker;
    private static readonly LayerStub layerStub = Layer;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NativeStub(IntPtr name, ushort category, int flags, int metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LayerStub(IntPtr name);
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);
    private static IntPtr Marker(IntPtr name, ushort category, int flags, int metadata) => IntPtr.Zero;
    private static int Layer(IntPtr name) => 5;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
