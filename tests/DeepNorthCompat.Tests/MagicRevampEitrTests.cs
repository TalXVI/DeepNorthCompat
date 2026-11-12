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

internal static class MagicRevampEitrTests
{
    private const string VendorOwner = "blacks7ar.MagicRevamp";
    private const string FixtureOwner = "DeepNorthCompat.Tests.MagicRevampEitr";
    private const string OtherOwner = "DeepNorthCompat.Tests.MagicRevampEitr.Other";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static Assembly magic = null!, backpacks = null!;
    private static MethodInfo target = null!, vendor = null!, vendorHelper = null!;
    private static Player local = null!, remote = null!;
    private static Inventory localInventory = null!, remoteInventory = null!;
    private static Inventory? book;
    private static ItemDrop.ItemData localWeapon = null!, remoteWeapon = null!;
    private static float skill, adjustment;

    internal static void Run(string profile, Action<string, Action> test)
    {
        test("magic revamp eitr remains inactive without the optional mod", () =>
        {
            MagicRevampEitrPatch.Reset();
            MagicRevampEitrPatch.Prepare(null);
            MagicRevampEitrPatch.Verify();
            Check(!Harmony.HasAnyPatches(MagicRevampEitrPatch.Owner), "absent mod installed an eitr hook");
        });

        test("magic revamp eitr rejects an unsupported vendor build", () =>
        {
            bool rejected = false;
            try { MagicRevampEitrPatch.Prepare(typeof(MagicRevampEitrTests).Assembly); }
            catch (NotSupportedException exception) { rejected = exception.Message.Contains("DeepNorthCompat.Tests build changed"); }
            Check(rejected && !Harmony.HasAnyPatches(MagicRevampEitrPatch.Owner), "unsupported vendor build installed an eitr hook");
        });

        string? magicPath = Find(profile, "MagicRevamp.dll");
        string? backpacksPath = Find(profile, "Backpacks.dll");
        if (magicPath == null || backpacksPath == null) return;
        magic = Assembly.LoadFrom(magicPath);
        backpacks = Assembly.LoadFrom(backpacksPath);
        Type patch = magic.GetType("MagicRevamp.Patches.AttackPatch", true)!;
        target = AccessTools.Method(typeof(Attack), "GetAttackEitr", new[] { typeof(Character), typeof(ItemDrop.ItemData) });
        vendor = AccessTools.Method(patch, "EitrTranspiler");
        vendorHelper = AccessTools.Method(patch, "GetAttackEitr");

        void Case(string name, Action action) => test("magic revamp eitr " + name, () =>
        {
            Cleanup();
            try { Setup(); action(); } finally { Cleanup(); }
        });

        Case("uses the selected scroll and keeps the game's skill calculation", () =>
        {
            Install();
            var staff = Item("$ammo_spells", 17, 0);
            var first = Item("$ammo_spells", 31, 0);
            var selected = Item("$ammo_spells", 73, 1);
            localWeapon = remoteWeapon = staff;
            localInventory.GetAllItems().Add(first);
            localInventory.GetAllItems().Add(selected);
            Equip(local, selected);
            Attack active = staff.m_shared.m_attack.Clone();
            AccessTools.Field(typeof(Attack), "m_character").SetValue(active, local);

            Cost(active, local, staff, 73, "equipped scroll, not first slot");
            Equip(local, null);
            Cost(active, local, staff, 31, "inventory scroll before auto-equip");
            first.m_shared.m_attack.m_attackEitr = 47;
            Cost(active, local, staff, 47, "updated item cost");
            Equip(local, Item("arrows", 99, 0));
            Cost(active, local, staff, 47, "wrong-type equipped item");
            Equip(local, Item("$ammo_spells", 88, 0));
            Cost(active, local, staff, 47, "stale equipped item");

            localInventory.GetAllItems().Clear();
            Equip(local, null);
            Cost(active, local, staff, 17, "no matching ammo");
            book = new Inventory(true);
            var bookScroll = Item("$ammo_spells", 61, 0);
            book.GetAllItems().Add(bookScroll);
            Cost(active, local, staff, 61, "unequipped book scroll");
            Equip(local, bookScroll);
            Cost(active, local, staff, 61, "equipped book scroll");

            var ordinary = Item("arrows", 23, 0);
            Cost(active, local, ordinary, 23, "non-spell weapon while a staff is equipped");
            localWeapon = ordinary;
            book = null;
            localInventory.GetAllItems().Add(selected);
            Equip(local, selected);
            Cost(active, local, staff, 73, "passed spell weapon while another weapon is equipped");
            localWeapon = staff;

            Attack secondary = Fake<Attack>();
            secondary.m_attackEitr = 103;
            Cost(secondary, local, staff, 73, "secondary receiver");
            AccessTools.Field(typeof(Attack), "m_character").SetValue(ordinary.m_shared.m_attack, local);
            Cost(ordinary.m_shared.m_attack, local, ordinary, 23, "initialized shared blocker");
            Check(ordinary.m_shared.m_attack.m_attackEitr == 23, "shared blocker remains unchanged");

            book = new Inventory(true);
            book.GetAllItems().Add(bookScroll);
            Equip(local, bookScroll);
            skill = 0.5f;
            adjustment = 5;
            new Harmony(OtherOwner).Patch(target, postfix: Hook(nameof(AdjustCost)));
            Cost(active, local, staff, 61f * (1f - 0.33f * 0.5f) + 5f, "skill reduction and another postfix");
            skill = adjustment = 0;
            bookScroll.m_shared.m_attack.m_attackEitr = 0;
            Cost(active, local, staff, 0, "zero scroll cost");
            bookScroll.m_shared.m_attack.m_attackEitr = 61;

            var remoteScroll = Item("$ammo_spells", 79, 0);
            remoteInventory.GetAllItems().Add(remoteScroll);
            Cost(active, remote, staff, 79, "remote player uses their inventory");
            Cost(staff.m_shared.m_attack, local, staff, 61, "direct query without an active attack");
            Cost(active, Fake<Humanoid>(), staff, 17, "non-player attack");
            Check(staff.m_shared.m_attack.m_attackEitr == 17
                && AccessTools.Field(typeof(Attack), "m_character").GetValue(staff.m_shared.m_attack) == null,
                "shared staff attack remains unchanged");
        });

        Case("requires the inventory repair and the exact vendor hook", () =>
        {
            var errors = new List<string>();
            Action<string> previous = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                MagicRevampEitrPatch.Prepare(magic);
                MagicRevampEitrPatch.Verify();
                Check(errors.Any(m => m.StartsWith("MagicRevamp.Eitr: NOT APPLIED")), "unready inventory repair is reported");
                Check(!Harmony.HasAnyPatches(MagicRevampEitrPatch.Owner), "no dependent patch is installed");

                PrepareInventory();
                new Harmony(VendorOwner).Unpatch(target, HarmonyPatchType.Transpiler, VendorOwner);
                errors.Clear();
                MagicRevampEitrPatch.Prepare(magic);
                MagicRevampEitrPatch.Verify();
                Check(errors.Any(m => m.StartsWith("MagicRevamp.Eitr: NOT APPLIED")), "missing vendor hook is reported");
                Check(!Harmony.HasAnyPatches(MagicRevampEitrPatch.Owner), "missing vendor hook leaves no dependent patch");
            }
            finally { CompatibilityInstaller.Error = previous; }
        });

        Case("repatching retains the fix and loss of the vendor hook disables it", () =>
        {
            Install();
            var staff = Item("$ammo_spells", 17, 0);
            var scroll = Item("$ammo_spells", 73, 0);
            localWeapon = remoteWeapon = staff;
            localInventory.GetAllItems().Add(scroll);
            Equip(local, scroll);
            var errors = new List<string>();
            Action<string> previous = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                new Harmony(OtherOwner).Patch(target, postfix: Hook(nameof(AdjustCost)));
                adjustment = 5;
                Cost(staff.m_shared.m_attack, local, staff, 78, "another mod triggers a rebuild");
                adjustment = 0;
                new Harmony(VendorOwner).Unpatch(target, HarmonyPatchType.Transpiler, VendorOwner);
                Cost(staff.m_shared.m_attack, local, staff, 17, "vendor transpiler removed");
                Check(errors.Count(m => m.StartsWith("MagicRevamp.Eitr: NO LONGER APPLIED")) == 1, "lost rewrite reported once");
                Check(Harmony.GetPatchInfo(target)!.Postfixes.Any(p => p.owner == OtherOwner), "foreign postfix remains");
            }
            finally { CompatibilityInstaller.Error = previous; }
        });

        Case("loss of the query dependency returns to the vendor helper", () =>
        {
            Install();
            var staff = Item("$ammo_spells", 17, 0);
            var scroll = Item("$ammo_spells", 73, 0);
            localWeapon = remoteWeapon = staff;
            localInventory.GetAllItems().Add(scroll);
            Equip(local, scroll);
            Cost(staff.m_shared.m_attack, local, staff, 73, "query group active");
            var errors = new List<string>();
            Action<string> previous = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                MagicRevampInventoryPatch.Reset();
                Cost(staff.m_shared.m_attack, local, staff, 17, "query group inactive");
                PrepareInventory();
                Cost(staff.m_shared.m_attack, local, staff, 17, "lost eitr group stays inactive");
                Check(errors.Count(m => m.StartsWith("MagicRevamp.Eitr: NO LONGER APPLIED")) == 1, "dependency loss reported once");
            }
            finally { CompatibilityInstaller.Error = previous; }
        });

        foreach (string variant in new[] { "changed", "duplicate", "label", "block" })
        {
            Case("rejects " + variant + " rewrite shape without changing input", () =>
            {
                Install();
                var input = ((IEnumerable<CodeInstruction>)vendor.Invoke(null,
                    new object[] { PatchProcessor.GetOriginalInstructions(target) })!).Select(i => new CodeInstruction(i)).ToList();
                int site = input.FindIndex(i => i.Calls(vendorHelper));
                Check(site >= 3, "vendor site exists");
                if (variant == "changed") input[site - 1].opcode = OpCodes.Nop;
                if (variant == "duplicate") input.Insert(site, new CodeInstruction(OpCodes.Call, vendorHelper));
                if (variant == "label") input[site].labels.Add(new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel());
                if (variant == "block") input[site].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                var before = input.Select(i => new CodeInstruction(i)).ToList();
                List<CodeInstruction> output = MagicRevampEitrPatch.Rewrite(input).ToList();
                Check(Same(before, input) && Same(before, output), "input and returned instructions retained");
            });
        }
    }

    internal static void RunServer(string profile, Action<string, Action> test) => test("magic revamp eitr dedicated role remains inactive", () =>
    {
        Cleanup();
        var infos = new List<string>();
        Action<string> previous = CompatibilityInstaller.Info;
        try
        {
            CompatibilityInstaller.Info = infos.Add;
            string? path = Find(profile, "MagicRevamp.dll");
            MagicRevampEitrPatch.Prepare(path == null ? null : Assembly.LoadFrom(path));
            MagicRevampEitrPatch.Verify();
            Check(infos.Any(m => m.Contains(path == null ? "optional mod absent" : "inactive on dedicated server")), "server inactivity reported");
            Check(!Harmony.HasAnyPatches(MagicRevampEitrPatch.Owner), "server has no eitr hook");
        }
        finally { CompatibilityInstaller.Info = previous; Cleanup(); }
    });

    private static void Setup()
    {
        Type inventory = magic.GetType("MagicRevamp.Patches.InventoryPatch", true)!;
        Type cycle = magic.GetType("MagicRevamp.Functions.CycleSpells", true)!;
        var fixture = new Harmony(FixtureOwner);
        fixture.Patch(AccessTools.Method(typeof(Character), "GetSkillFactor"), prefix: Hook(nameof(Skill)));
        fixture.Patch(AccessTools.Method(typeof(Player), "GetSkillFactor"), prefix: Hook(nameof(Skill)));
        fixture.Patch(AccessTools.Method(typeof(Humanoid), "GetCurrentWeapon"), prefix: Hook(nameof(CurrentWeapon)));
        fixture.Patch(AccessTools.Method(cycle, "SpellBookContainer"), prefix: Hook(nameof(SpellBook)));

        var vendorHarmony = new Harmony(VendorOwner);
        vendorHarmony.Patch(target, transpiler: new HarmonyMethod(vendor));
        vendorHarmony.Patch(AccessTools.Method(typeof(Inventory), "ContainsItem"), postfix: new HarmonyMethod(AccessTools.Method(inventory, "ContainsItem_Postfix")));
        vendorHarmony.Patch(AccessTools.Method(typeof(Inventory), "GetAmmoItem"), postfix: new HarmonyMethod(AccessTools.Method(inventory, "GetAmmoItem_Postfix")));
        vendorHarmony.Patch(AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(ItemDrop.ItemData), typeof(int) }),
            postfix: new HarmonyMethod(AccessTools.Method(inventory, "RemoveItem_Postfix")));

        local = CreatePlayer(); remote = CreatePlayer();
        Player.m_localPlayer = local;
        localInventory = new Inventory(true); remoteInventory = new Inventory(true);
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(local, localInventory);
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(remote, remoteInventory);
    }

    private static void PrepareInventory()
    {
        MagicRevampInventoryPatch.Prepare(magic, backpacks);
        MagicRevampInventoryPatch.Verify();
        Check(MagicRevampInventoryPatch.Ready, "inventory group applied");
    }

    private static void Install()
    {
        PrepareInventory();
        MagicRevampEitrPatch.Prepare(magic);
        MagicRevampEitrPatch.Verify();
        Check(Harmony.GetPatchInfo(target)!.Transpilers.Count(p => p.owner == MagicRevampEitrPatch.Owner) == 1, "eitr group applied");
    }

    private static Player CreatePlayer()
    {
        Player player = Fake<Player>();
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
        pointers.Add(pointer);
        Marshal.WriteInt32(pointer, pointers.Count);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(player, pointer);
        return player;
    }

    private static ItemDrop.ItemData Item(string ammoType, float cost, int x)
    {
        var attack = Fake<Attack>();
        attack.m_attackEitr = cost;
        var data = Fake<ItemDrop.ItemData.SharedData>();
        data.m_attack = attack; data.m_ammoType = ammoType; data.m_itemType = ItemDrop.ItemData.ItemType.Ammo;
        var item = Fake<ItemDrop.ItemData>();
        item.m_shared = data; item.m_gridPos = new Vector2i(x, 0);
        return item;
    }

    private static void Equip(Humanoid player, ItemDrop.ItemData? item) => AccessTools.Field(typeof(Humanoid), "m_ammoItem").SetValue(player, item);
    private static bool Skill(ref float __result) { __result = skill; return false; }
    private static bool SpellBook(Player player, ref Inventory? __result) { __result = ReferenceEquals(player, local) ? book : null; return false; }
    private static bool CurrentWeapon(Humanoid __instance, ref ItemDrop.ItemData __result) { __result = ReferenceEquals(__instance, remote) ? remoteWeapon : localWeapon; return false; }
    private static void AdjustCost(ref float __result) { __result += adjustment; }
    private static T Fake<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampEitrTests), name));
    private static bool Same(IList<CodeInstruction> a, IList<CodeInstruction> b) => a.Count == b.Count && a.Zip(b,
        (x, y) => x.opcode == y.opcode && Equals(x.operand, y.operand) && x.labels.SequenceEqual(y.labels) && x.blocks.SequenceEqual(y.blocks)).All(v => v);
    private static string? Find(string profile, string file) =>
        Directory.GetFiles(Path.Combine(profile, "BepInEx", "plugins"), file, SearchOption.AllDirectories).SingleOrDefault();
    private static void Cost(Attack attack, Character character, ItemDrop.ItemData weapon, float expected, string name) =>
        Check(Math.Abs(attack.GetAttackEitr(character, weapon) - expected) < 0.0001f, name);

    private static void Cleanup()
    {
        new Harmony(OtherOwner).UnpatchSelf();
        MagicRevampEitrPatch.Reset();
        MagicRevampInventoryPatch.Reset();
        new Harmony(VendorOwner).UnpatchSelf();
        new Harmony(FixtureOwner).UnpatchSelf();
        Player.m_localPlayer = null;
        foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
        pointers.Clear(); book = null; skill = adjustment = 0;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
