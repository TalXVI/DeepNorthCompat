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

internal static class MagicRevampInventoryTests
{
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevamp.Inventory";
    private const string Other = Fixture + ".Other";
    private static readonly MethodInfo Contains = AccessTools.Method(typeof(Inventory), "ContainsItem");
    private static readonly MethodInfo Ammo = AccessTools.Method(typeof(Inventory), "GetAmmoItem");
    private static readonly MethodInfo Remove = AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(ItemDrop.ItemData), typeof(int) });
    private static readonly List<string> Errors = new List<string>();
    private static Inventory? book;
    private static ItemDrop.ItemData weapon = null!;
    private static bool throwOnRepatch;

    internal static void Run(string profile, Action<string, Action> test)
    {
        test("MagicRevamp inventory is inactive when optional mods are absent", () =>
        {
            MagicRevampInventoryPatch.Reset();
            MagicRevampInventoryPatch.Prepare(null, null);
            MagicRevampInventoryPatch.Verify();
            Check(!MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner), "absent mods installed hooks");
        });

        string plugins = Path.Combine(profile, "BepInEx/plugins");
        string[] magicFiles = Directory.GetFiles(plugins, "MagicRevamp.dll", SearchOption.AllDirectories);
        string[] backpackFiles = Directory.GetFiles(plugins, "Backpacks.dll", SearchOption.AllDirectories);
        if (magicFiles.Length == 0 || backpackFiles.Length == 0) return;
        Assembly magic = Assembly.LoadFrom(magicFiles.Single()), backpacks = Assembly.LoadFrom(backpackFiles.Single());

        test("MagicRevamp inventory is inactive when either dependency is absent", () =>
        {
            MagicRevampInventoryPatch.Prepare(magic, null);
            MagicRevampInventoryPatch.Verify();
            MagicRevampInventoryPatch.Prepare(null, backpacks);
            MagicRevampInventoryPatch.Verify();
            Check(!MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner), "missing dependency installed hooks");
        });

        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("MagicRevamp inventory stays inactive on a dedicated server", () =>
            {
                MagicRevampInventoryPatch.Prepare(magic, backpacks);
                MagicRevampInventoryPatch.Verify();
                Check(!MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner), "server installed client inventory hooks");
            });
            return;
        }

        Action<string> error = CompatibilityInstaller.Error;
        CompatibilityInstaller.Error = Errors.Add;
        try { Cases(magic, backpacks, test); }
        finally
        {
            MagicRevampInventoryPatch.Reset();
            new Harmony(Other).UnpatchSelf();
            new Harmony(Fixture).UnpatchSelf();
            new Harmony(MagicRevampInventoryPatch.Guid).UnpatchSelf();
            CompatibilityInstaller.Error = error;
            Player.m_localPlayer = null;
            book = null;
            Errors.Clear();
        }
    }

    private static void Cases(Assembly magic, Assembly backpacks, Action<string, Action> test)
    {
        Type inventoryPatch = magic.GetType("MagicRevamp.Patches.InventoryPatch", true)!;
        MethodInfo vendorContains = AccessTools.Method(inventoryPatch, "ContainsItem_Postfix");
        MethodInfo vendorAmmo = AccessTools.Method(inventoryPatch, "GetAmmoItem_Postfix");
        MethodInfo vendorRemove = AccessTools.Method(inventoryPatch, "RemoveItem_Postfix");
        MethodInfo spellBook = AccessTools.Method(magic.GetType("MagicRevamp.Functions.CycleSpells", true)!, "SpellBookContainer");
        var environment = new Harmony(Fixture);
        var vendor = new Harmony(MagicRevampInventoryPatch.Guid);
        environment.Patch(spellBook, prefix: Hook(nameof(Book)));
        environment.Patch(AccessTools.Method(typeof(Humanoid), "GetCurrentWeapon"), prefix: Hook(nameof(Weapon)));
        environment.Patch(AccessTools.PropertyGetter(typeof(UnityEngine.Object), "name"), prefix: Hook(nameof(Name)));
        environment.Patch(AccessTools.Method(typeof(ZLog), "Log", new[] { typeof(object) }), prefix: Hook(nameof(Skip)));

        test("MagicRevamp inventory rejects unsupported vendor builds", () =>
        {
            foreach (bool badMagic in new[] { true, false })
            {
                bool rejected = false;
                try { MagicRevampInventoryPatch.Prepare(badMagic ? typeof(Plugin).Assembly : magic, badMagic ? backpacks : typeof(Plugin).Assembly); }
                catch (NotSupportedException) { rejected = true; }
                Check(rejected && !MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner), "unsupported vendor was accepted");
            }
        });

        test("MagicRevamp inventory rejects missing vendor registrations", () =>
        {
            Errors.Clear();
            MagicRevampInventoryPatch.Prepare(magic, backpacks);
            MagicRevampInventoryPatch.Verify();
            Check(!MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner)
                && Errors.Any(e => e.Contains("NOT APPLIED")), "missing vendor hooks were accepted");
        });

        vendor.Patch(Contains, postfix: new HarmonyMethod(vendorContains));
        vendor.Patch(Ammo, postfix: new HarmonyMethod(vendorAmmo));
        vendor.Patch(Remove, postfix: new HarmonyMethod(vendorRemove));

        test("MagicRevamp inventory rejects duplicate vendor hooks", () =>
        {
            var other = new Harmony(Other);
            Patch original = Harmony.GetPatchInfo(Contains)!.Postfixes.Single(p => p.owner == MagicRevampInventoryPatch.Guid && p.PatchMethod == vendorContains);
            other.Patch(Contains, postfix: new HarmonyMethod(vendorContains));
            Errors.Clear();
            MagicRevampInventoryPatch.Prepare(magic, backpacks);
            MagicRevampInventoryPatch.Verify();
            Check(!MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner)
                && Errors.Any(e => e.Contains("NOT APPLIED")), "duplicate vendor hook was accepted");
            other.UnpatchSelf();
            Guard.Restore(Contains, original, HarmonyPatchType.Postfix);
        });

        test("MagicRevamp inventory rolls back a partially installed group", () =>
        {
            environment.Patch(vendorAmmo, transpiler: Hook(nameof(RejectOnce)));
            Errors.Clear();
            MagicRevampInventoryPatch.Prepare(magic, backpacks);
            throwOnRepatch = true;
            MagicRevampInventoryPatch.Verify();
            Check(!throwOnRepatch && !MagicRevampInventoryPatch.Ready && !Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner)
                && Errors.Any(e => e.Contains("NOT APPLIED")), $"rollback: pending throw={throwOnRepatch}, ready={MagicRevampInventoryPatch.Ready}, "
                    + $"owner patched={Harmony.HasAnyPatches(MagicRevampInventoryPatch.Owner)}, errors={string.Join(" | ", Errors)}");
            environment.Unpatch(vendorAmmo, AccessTools.Method(typeof(MagicRevampInventoryTests), nameof(RejectOnce)));
        });

        test("MagicRevamp inventory verifies its exact vendor hooks", () =>
        {
            Errors.Clear();
            MagicRevampInventoryPatch.Prepare(magic, backpacks);
            Check(!MagicRevampInventoryPatch.Ready, "group enabled before verification");
            MagicRevampInventoryPatch.Verify();
            Check(MagicRevampInventoryPatch.Ready && Errors.Count == 0, "exact vendor hooks did not verify");
            MagicRevampInventoryPatch.Prepare(magic, backpacks);
            MagicRevampInventoryPatch.Verify();
            foreach (MethodInfo target in new[] { vendorContains, vendorAmmo, vendorRemove })
                Check(Harmony.GetPatchInfo(target)!.Prefixes.Count(p => p.owner == MagicRevampInventoryPatch.Owner) == 1, "duplicate replacement hook");
        });

        IntPtr nativeId = Marshal.AllocHGlobal(sizeof(long));
        IntPtr prefabId = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt32(nativeId, 1);
            Marshal.WriteInt32(prefabId, 2);
            var player = Fake<Player>(nativeId);
            var prefab = Fake<GameObject>(prefabId);
            Player.m_localPlayer = player;
            Inventory playerInventory = NewInventory("player", 8), chest = NewInventory("chest", 4);
            AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, playerInventory);
            weapon = Item("weapon", 1, "$ammo_spells");
            book = NewInventory("book", 6);
            int changes = 0;
            book.m_onChanged += () => changes++;

            test("MagicRevamp inventory removes direct and redirected book stacks once", () =>
            {
                ItemDrop.ItemData scroll = Item("scroll", 10, "$ammo_spells"); book!.GetAllItems().Add(scroll);
                Check(book.RemoveItem(scroll, 2) && scroll.m_stack == 8 && changes == 1, "direct book removal was not exact");
                scroll.m_stack = 10; changes = 0;
                Check(playerInventory.RemoveItem(scroll, 2) && scroll.m_stack == 8 && changes == 1, "redirected removal or change event failed");
                book.GetAllItems().Clear(); changes = 0;
            });

            test("MagicRevamp inventory leaves ordinary and failed removals native", () =>
            {
                ItemDrop.ItemData stone = Item("stone", 2, ""), other = Item("stone", 2, ""), scroll = Item("scroll", 10, "$ammo_spells");
                playerInventory.GetAllItems().Add(stone); chest.GetAllItems().Add(other); book!.GetAllItems().Add(scroll);
                Check(playerInventory.RemoveItem(stone, 1) && stone.m_stack == 1 && playerInventory.GetAllItems().Contains(stone), "player item lost");
                Check(chest.RemoveItem(other, 1) && other.m_stack == 1 && chest.GetAllItems().Contains(other), "chest item lost");
                Check(!chest.RemoveItem(scroll, 2) && scroll.m_stack == 10 && changes == 0, "foreign removal touched the book");
                playerInventory.GetAllItems().Clear(); chest.GetAllItems().Clear(); book.GetAllItems().Clear();
            });

            test("MagicRevamp inventory preserves native removal boundaries", () =>
            {
                ItemDrop.ItemData scroll = Item("scroll", 5, "$ammo_spells"); book!.GetAllItems().Add(scroll);
                Check(!playerInventory.RemoveItem(scroll, 0) && !playerInventory.RemoveItem(scroll, -1)
                    && scroll.m_stack == 5 && changes == 0, "invalid redirected amount mutated the book");
                Check(book.RemoveItem(scroll, 0) && scroll.m_stack == 5 && changes == 1, "direct zero changed native semantics");
                Check(playerInventory.RemoveItem(scroll, 100) && !book.GetAllItems().Contains(scroll) && changes == 2, "overamount failed to remove the full stack");
                Check(!playerInventory.RemoveItem(scroll, 1) && changes == 2, "absent item reported removal");
                scroll = Item("scroll", 1, "$ammo_spells"); book.GetAllItems().Add(scroll); changes = 0;
                Check(playerInventory.RemoveItem(scroll, 1) && !book.GetAllItems().Contains(scroll) && changes == 1, "exact full-stack removal failed");
                changes = 0;
            });

            test("MagicRevamp inventory scopes composite queries to spell ammo", () =>
            {
                ItemDrop.ItemData scroll = Item("scroll", 10, "$ammo_spells"); scroll.m_dropPrefab = prefab;
                ItemDrop.ItemData later = Item("later", 10, "$ammo_spells"); later.m_gridPos = new Vector2i(3, 0); later.m_dropPrefab = prefab;
                ItemDrop.ItemData arrow = Item("arrow", 5, "$ammo_arrows");
                book!.GetAllItems().Add(later); book.GetAllItems().Add(scroll); playerInventory.GetAllItems().Add(arrow);
                Check(!chest.ContainsItem(scroll) && chest.GetAmmoItem("$ammo_spells") == null, "book leaked into a foreign inventory");
                Check(playerInventory.ContainsItem(scroll) && !playerInventory.GetAllItems().Contains(scroll), "player composite query lost book ammo");
                Check(ReferenceEquals(playerInventory.GetAmmoItem("$ammo_spells", "BMR_TestScroll"), scroll)
                    && playerInventory.GetAmmoItem("$ammo_spells", "wrong") == null, "spell prefab matching changed");
                Check(ReferenceEquals(playerInventory.GetAmmoItem("$ammo_spells"), scroll), "spell selection did not preserve book grid order");
                Check(ReferenceEquals(playerInventory.GetAmmoItem("$ammo_arrows"), arrow), "ordinary ammo query was overwritten");
                book.GetAllItems().Clear(); playerInventory.GetAllItems().Clear();
            });

            test("MagicRevamp inventory exposes only the local player's equipped book", () =>
            {
                Player remote = Fake<Player>(prefabId);
                Check(ReferenceEquals(MagicRevampInventoryPatch.EquippedBook(player), book), "local equipped book missing");
                Check(MagicRevampInventoryPatch.EquippedBook(remote) == null, "remote player inherited the local book");
                Inventory saved = book!;
                book = null;
                Check(MagicRevampInventoryPatch.EquippedBook(player) == null, "absent book returned an inventory");
                book = saved;
            });

            test("MagicRevamp inventory leaves no-book and non-spell weapon behavior native", () =>
            {
                ItemDrop.ItemData scroll = Item("scroll", 2, "$ammo_spells"), arrow = Item("arrow", 2, "$ammo_arrows");
                book!.GetAllItems().Add(scroll); playerInventory.GetAllItems().Add(arrow);
                Inventory saved = book!;
                book = null;
                Check(!playerInventory.ContainsItem(scroll) && playerInventory.GetAmmoItem("$ammo_spells") == null, "no-book query changed");
                book = saved;
                weapon.m_shared.m_ammoType = "$ammo_arrows";
                Check(!playerInventory.ContainsItem(scroll) && ReferenceEquals(playerInventory.GetAmmoItem("$ammo_arrows"), arrow), "non-spell weapon query changed");
                Check(!playerInventory.RemoveItem(scroll, 1) && scroll.m_stack == 2, "non-spell weapon removed book ammo");
                weapon.m_shared.m_ammoType = "$ammo_spells";
                book.GetAllItems().Clear(); playerInventory.GetAllItems().Clear();
            });

            test("MagicRevamp inventory leaves queries and removals native without a local player", () =>
            {
                ItemDrop.ItemData scroll = Item("scroll", 2, "$ammo_spells"), arrow = Item("arrow", 2, "$ammo_arrows");
                book!.GetAllItems().Add(scroll); playerInventory.GetAllItems().Add(arrow);
                Player.m_localPlayer = null;
                Check(!playerInventory.ContainsItem(scroll) && playerInventory.GetAmmoItem("$ammo_spells") == null,
                    "book ammo leaked without a local player");
                Check(ReferenceEquals(playerInventory.GetAmmoItem("$ammo_arrows"), arrow)
                    && !playerInventory.RemoveItem(scroll, 1) && scroll.m_stack == 2, "no-player behavior changed");
                Player.m_localPlayer = player;
                book.GetAllItems().Clear(); playerInventory.GetAllItems().Clear();
            });

            test("MagicRevamp inventory survives a later target re-patch", () =>
            {
                var other = new Harmony(Other);
                other.Patch(Remove, postfix: Hook(nameof(Noop)));
                ItemDrop.ItemData scroll = Item("scroll", 2, "$ammo_spells"); book!.GetAllItems().Add(scroll);
                Check(MagicRevampInventoryPatch.Ready && playerInventory.RemoveItem(scroll, 1) && scroll.m_stack == 1, "re-patch lost removal");
                other.UnpatchSelf();
                Check(MagicRevampInventoryPatch.Ready && playerInventory.RemoveItem(scroll, 1) && !book.GetAllItems().Contains(scroll), "unpatch lost removal");
            });

            test("MagicRevamp inventory reset restores vendor behavior", () =>
            {
                MagicRevampInventoryPatch.Reset();
                ItemDrop.ItemData stone = Item("stone", 2, ""); chest.GetAllItems().Add(stone);
                Check(!MagicRevampInventoryPatch.Ready && MagicRevampInventoryPatch.EquippedBook(player) == null
                    && chest.RemoveItem(stone, 1) && !chest.GetAllItems().Contains(stone), "vendor postfix was not restored");
            });
        }
        finally
        {
            Player.m_localPlayer = null;
            Marshal.FreeHGlobal(nativeId);
            Marshal.FreeHGlobal(prefabId);
        }
    }

    private static Inventory NewInventory(string name, int width) => new Inventory(name, null, width, 1);
    private static ItemDrop.ItemData Item(string name, int stack, string ammo) => new ItemDrop.ItemData
    {
        m_stack = stack,
        m_shared = new ItemDrop.ItemData.SharedData { m_name = name, m_ammoType = ammo, m_maxStackSize = 100, m_itemType = ItemDrop.ItemData.ItemType.Ammo }
    };

    private static T Fake<T>(IntPtr id) where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, id);
        return value;
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampInventoryTests), name));
    private static bool Book(ref Inventory? __result) { __result = book; return false; }
    private static bool Weapon(ref ItemDrop.ItemData __result) { __result = weapon; return false; }
    private static bool Name(ref string __result) { __result = "BMR_TestScroll"; return false; }
    private static bool Skip() => false;
    private static void Noop() { }

    private static IEnumerable<CodeInstruction> RejectOnce(IEnumerable<CodeInstruction> instructions)
    {
        if (throwOnRepatch) { throwOnRepatch = false; throw new InvalidOperationException("fixture rejects the second replacement"); }
        return instructions;
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
