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
using ItemData = ItemDrop.ItemData;

// Executes the vendor cycle and preview methods. Unity scene checks, equipment effects,
// and the spellbook lookup are fixtures; inventory order and selection are real.
internal static class MagicRevampCycleTests
{
    private const string Fixture = "DeepNorthCompat.Tests.MagicRevamp.Cycle";
    private const string Conflict = Fixture + ".Conflict";
    private const string Ammo = "$ammo_spells";
    private static readonly List<string> errors = new List<string>();
    private static Assembly magic = null!, backpacks = null!;
    private static MethodInfo cycle = null!, names = null!, count = null!, next = null!;
    private static FieldInfo ammoField = null!;
    private static Player player = null!;
    private static Inventory inventory = null!;
    private static Inventory? book;
    private static ItemData weapon = null!;
    private static ItemData? equipped;
    private static string message = "";
    private static int equipCalls, showCalls;
    private static bool staff = true;
    private static bool changeNext;
    private static bool rejectRollback;

    internal static void Run(string profile, Action<string, Action> test)
    {
        string plugins = Path.Combine(profile, "BepInEx/plugins");
        string[] magicFiles = Directory.GetFiles(plugins, "MagicRevamp.dll", SearchOption.AllDirectories);
        string[] backpackFiles = Directory.GetFiles(plugins, "Backpacks.dll", SearchOption.AllDirectories);
        if (magicFiles.Length == 0)
        {
            test("MagicRevamp cycling remains inactive without the optional mod", () =>
            {
                var messages = new List<string>();
                Action<string> previous = CompatibilityInstaller.Info;
                try
                {
                    CompatibilityInstaller.Info = messages.Add;
                    MagicRevampCyclePatch.Prepare(null, null); MagicRevampCyclePatch.Verify();
                    Check(messages.Any(m => m.Contains("optional mod absent; inactive")), "absent mod is reported inactive");
                }
                finally { CompatibilityInstaller.Info = previous; }
            });
            return;
        }

        magic = Assembly.LoadFrom(magicFiles.Single());
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server")
        {
            test("MagicRevamp cycling is inactive on dedicated servers", () =>
            {
                var messages = new List<string>();
                Action<string> previous = CompatibilityInstaller.Info;
                try
                {
                    CompatibilityInstaller.Info = messages.Add;
                    MagicRevampCyclePatch.Prepare(magic, null); MagicRevampCyclePatch.Verify();
                    Check(messages.Any(m => m.Contains("client-only; inactive on dedicated server")), "server role is reported inactive");
                }
                finally { CompatibilityInstaller.Info = previous; }
            });
            return;
        }
        if (backpackFiles.Length == 0)
        {
            test("MagicRevamp cycling rejects its missing hard dependency", () =>
            {
                try { MagicRevampCyclePatch.Prepare(magic, null); throw new Exception("missing Backpacks accepted"); }
                catch (InvalidOperationException) { }
            });
            return;
        }
        backpacks = Assembly.LoadFrom(backpackFiles.Single());

        Type type = magic.GetType("MagicRevamp.Functions.CycleSpells", true)!;
        cycle = AccessTools.Method(type, "CycleSpell");
        names = AccessTools.Method(type, "GetSpellHudNames");
        count = AccessTools.Method(type, "CountSpellStacks");
        next = AccessTools.Method(type, "EquipNextSpell");
        ammoField = AccessTools.Field(typeof(Humanoid), "m_ammoItem");
        Player previousPlayer = Player.m_localPlayer;
        Action<string> previousInfo = CompatibilityInstaller.Info, previousError = CompatibilityInstaller.Error;
        var fixture = new Harmony(Fixture);

        try
        {
            fixture.Patch(AccessTools.Method(type, "SpellBookContainer"), prefix: Hook(nameof(Book)));
            fixture.Patch(AccessTools.Method(type, "StavesEquipped"), prefix: Hook(nameof(Staff)));
            fixture.Patch(AccessTools.Method(type, "ShowSpellEquipped"), prefix: Hook(nameof(Show)));
            fixture.Patch(AccessTools.Method(typeof(Humanoid), "GetCurrentWeapon"), prefix: Hook(nameof(Weapon)));
            fixture.Patch(AccessTools.Method(typeof(Humanoid), "EquipItem", new[] { typeof(ItemData), typeof(bool) }), prefix: Hook(nameof(Equip)));
            fixture.Patch(AccessTools.Method(typeof(Player), "Message", new[]
                { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) }), prefix: Hook(nameof(Message)));

            player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            inventory = new Inventory("fixture", null, 8, 3);
            AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory);
            Player.m_localPlayer = player;
            CompatibilityInstaller.Info = _ => { }; CompatibilityInstaller.Error = errors.Add;

            test("MagicRevamp vendor cycle and preview expose the hotbar and arrow defects", VendorBehavior);
            test("MagicRevamp cycle rejects unsupported builds and absent Backpacks", Guards);
            MagicRevampCyclePatch.Prepare(magic, backpacks);
            MagicRevampCyclePatch.Verify();
            Check(errors.Count == 0, string.Join("\n", errors));
            test("MagicRevamp cycle installation is idempotent", Idempotence);
            test("MagicRevamp cycling excludes every player hotbar slot", Hotbar);
            test("MagicRevamp cycle start, next, wrap and preview share grid order", GridOrder);
            test("MagicRevamp cycle preserves first duplicate representative", Duplicates);
            test("MagicRevamp cycle includes the first row of a spellbook", SpellBook);
            test("MagicRevamp one or zero candidates preserve manual availability", ManualSelection);
            test("MagicRevamp cycle ignores fully removed current scrolls", RemovedCurrent);
            test("MagicRevamp cycle retains staff gating and nonspell preview", UnrelatedBehavior);
            test("MagicRevamp cycle stops if a later transpiler changes its call sites", LaterConflict);
            test("MagicRevamp cycle rejects a changed next call at installation", InitialConflict);
            test("MagicRevamp cycle rejects and rolls back partial registration", Rollback);
            test("MagicRevamp cycle reports rollback failure with hooks disabled", RollbackFailure);
        }
        finally
        {
            rejectRollback = false;
            MagicRevampCyclePatch.Reset();
            new Harmony(Conflict).UnpatchSelf();
            new Harmony(Fixture).UnpatchSelf();
            Player.m_localPlayer = previousPlayer;
            CompatibilityInstaller.Info = previousInfo; CompatibilityInstaller.Error = previousError;
            book = null;
        }
    }

    private static void VendorBehavior()
    {
        Reset();
        ItemData utility = Scroll("portal", 0, 0), first = Scroll("fire", 0, 1), last = Scroll("ice", 1, 1);
        ItemData arrow = Scroll("arrow", 2, 1); arrow.m_shared.m_name = "$item_arrowwood"; arrow.m_shared.m_ammoType = "arrow";
        inventory.GetAllItems().AddRange(new[] { utility, first, last, arrow });
        Current(last);
        InvokeCycle();
        Check(ReferenceEquals(equipped, utility), "the original wrap selects the hotbar utility scroll");
        Current(last);
        Check(Preview().Item4 == "$item_arrowwood", "the original preview lists unrelated ammo");
    }

    private static void Guards()
    {
        try { MagicRevampCyclePatch.Prepare(magic, null); throw new Exception("missing dependency accepted"); }
        catch (InvalidOperationException) { }
        try { MagicRevampCyclePatch.Prepare(typeof(MagicRevampCycleTests).Assembly, backpacks); throw new Exception("unsupported MagicRevamp accepted"); }
        catch (NotSupportedException) { }
        try { MagicRevampCyclePatch.Prepare(magic, typeof(MagicRevampCycleTests).Assembly); throw new Exception("unsupported Backpacks accepted"); }
        catch (NotSupportedException) { }
        MagicRevampCyclePatch.Prepare(null, backpacks); MagicRevampCyclePatch.Verify();
        Check(Harmony.GetPatchInfo(cycle)?.Transpilers.All(p => p.owner != MagicRevampCyclePatch.Owner) != false,
            "failed and absent guards add no cycle hook");
    }

    private static void Hotbar()
    {
        for (int x = 0; x < 8; x++)
        {
            Reset();
            ItemData utility = Scroll("portal", x, 0), first = Scroll("fire", 0, 1), last = Scroll("ice", 1, 1);
            inventory.GetAllItems().AddRange(new[] { utility, first, last });
            Current(last); InvokeCycle();
            Check(ReferenceEquals(equipped, first), "wrap must skip player hotbar slot " + x);
            Check((int)count.Invoke(null, null) == 3, "the vendor's shared manual-availability count remains unchanged");
        }
    }

    private static void Idempotence()
    {
        MagicRevampCyclePatch.Prepare(magic, backpacks); MagicRevampCyclePatch.Verify();
        Check(errors.Count == 0 && Harmony.GetPatchInfo(cycle).Transpilers.Count(p => p.owner == MagicRevampCyclePatch.Owner) == 1
            && Harmony.GetPatchInfo(names).Prefixes.Count(p => p.owner == MagicRevampCyclePatch.Owner) == 1,
            "repeated installation keeps exactly one hook on each method");
    }

    private static void GridOrder()
    {
        Reset();
        ItemData utility = Scroll("portal", 0, 0), first = Scroll("fire", 0, 1), last = Scroll("ice", 3, 1);
        ItemData arrow = Scroll("arrow", 2, 1); arrow.m_shared.m_name = "$item_arrowwood"; arrow.m_shared.m_ammoType = "arrow";
        inventory.GetAllItems().AddRange(new[] { last, utility, arrow, first });
        Current(null); Check(Preview().Item4 == first.m_shared.m_name, "no-current preview chooses the first grid candidate");
        InvokeCycle(); Check(ReferenceEquals(equipped, first), "no-current cycle chooses the first grid candidate");
        var preview = Preview();
        Check(preview.Item2 == first.m_shared.m_name && preview.Item4 == last.m_shared.m_name && preview.Item6 == last.m_shared.m_name,
            "preview shows actual current, next and previous candidates");
        InvokeCycle(); Check(ReferenceEquals(equipped, last), "next follows grid order, not list order");
        Check(Preview().Item4 == first.m_shared.m_name, "preview agrees with wrap");
        InvokeCycle(); Check(ReferenceEquals(equipped, first), "wrap returns to the first candidate");
    }

    private static void Duplicates()
    {
        Reset();
        ItemData firstCopy = Scroll("fire", 5, 1), laterCopy = Scroll("fire", 0, 1);
        ItemData middle = Scroll("ice", 1, 1), next = Scroll("wind", 3, 1);
        inventory.GetAllItems().AddRange(new[] { firstCopy, laterCopy, middle, next });
        Current(middle); InvokeCycle(); Check(ReferenceEquals(equipped, next), "list-first duplicate retains its later grid position");
        InvokeCycle(); Check(ReferenceEquals(equipped, firstCopy), "first physical representative is selected");
        InvokeCycle(); Check(ReferenceEquals(equipped, middle), "duplicate is not an extra cycle entry");
    }

    private static void SpellBook()
    {
        Reset();
        ItemData hotbar = Scroll("portal", 0, 0);
        inventory.GetAllItems().Add(hotbar);
        book = new Inventory("book", null, 6, 2);
        ItemData first = Scroll("fire", 0, 0), second = Scroll("ice", 1, 0);
        book.GetAllItems().AddRange(new[] { second, first });
        Current(null); InvokeCycle(); Check(ReferenceEquals(equipped, first), "book row zero remains eligible");
        Check(Preview().Item4 == second.m_shared.m_name, "book preview follows its own inventory");
        InvokeCycle(); Check(ReferenceEquals(equipped, second), "book cycle includes both first-row slots");
        InvokeCycle(); Check(ReferenceEquals(equipped, first), "book cycle wraps without using player hotbar");
        Current(hotbar);
        Check(Preview().Item2 == hotbar.m_shared.m_name && Preview().Item4 == first.m_shared.m_name,
            "a manually selected player hotbar scroll remains current with a book equipped");
        InvokeCycle(); Check(ReferenceEquals(equipped, first), "a manual player scroll cycles into the equipped book");
    }

    private static void ManualSelection()
    {
        Reset();
        ItemData manual = Scroll("portal", 0, 0), only = Scroll("fire", 0, 1);
        inventory.GetAllItems().AddRange(new[] { manual, only });
        Current(manual);
        var preview = Preview();
        Check(preview.Item2 == manual.m_shared.m_name && preview.Item4 == only.m_shared.m_name,
            "manually selected hotbar spell remains current while the preview shows the eligible spell");
        InvokeCycle(); Check(ReferenceEquals(equipped, only), "one candidate is reachable from a different manual spell");
        InvokeCycle(); Check(equipCalls == 1 && message.Contains("$bmr_onlyhaveonespell"), "one unchanged candidate retains the vendor message");

        Reset(); inventory.GetAllItems().Add(manual); Current(manual); InvokeCycle();
        Check(equipCalls == 0 && message.Contains("$bmr_nospellinventory"), "no cycle candidates do not re-equip the manual spell");
        Check((int)count.Invoke(null, null) == 1 && ReferenceEquals(ammoField.GetValue(player), manual),
            "shared count and manually equipped hotbar spell remain available");
        preview = Preview();
        Check(preview.Item2 == manual.m_shared.m_name && preview.Item4 == "" && preview.Item6 == "", "manual-only preview shows current without a fictitious next");

        Reset(); inventory.GetAllItems().Add(only); Current(null); InvokeCycle();
        Check(ReferenceEquals(equipped, only), "one candidate is reachable with no current spell");
    }

    private static void UnrelatedBehavior()
    {
        Reset(); inventory.GetAllItems().Add(Scroll("fire", 0, 1));
        staff = false; InvokeCycle(); Check(equipCalls == 0 && showCalls == 0 && message == "", "staff gate remains authoritative");
        staff = true;
        weapon.m_shared.m_ammoType = "arrow";
        ItemData arrow = Scroll("arrow", 2, 1); arrow.m_shared.m_name = "$item_arrowwood"; arrow.m_shared.m_ammoType = "arrow";
        inventory.GetAllItems().Add(arrow); Current(arrow);
        Check(Preview().Item2 == "$item_arrowwood", "nonspell preview retains the original vendor enumeration");
        InvokeCycle();
        Check(message.Contains("$bmr_onlyhaveonespell"), "nonspell weapon keeps the vendor cycle behavior");
    }

    private static void RemovedCurrent()
    {
        foreach (bool useBook in new[] { false, true })
        {
            Reset();
            Inventory source = useBook ? book = new Inventory("book", null, 6, 2) : inventory;
            int row = useBook ? 0 : 1;
            ItemData consumed = Scroll("fire", 0, row), remaining = Scroll("fire", 1, row);
            source.GetAllItems().AddRange(new[] { consumed, remaining });
            Current(consumed);
            Check(source.RemoveItem(consumed, 1) && !source.GetAllItems().Contains(consumed), "the game removes the consumed stack");
            var preview = Preview();
            Check(preview.Item2 == "" && preview.Item4 == remaining.m_shared.m_name,
                "a removed current stack is not shown as active");
            InvokeCycle();
            Check(ReferenceEquals(equipped, remaining), "a remaining stack of the same spell is selectable");

            source.RemoveItem(remaining, 1);
            preview = Preview();
            Check(preview.Item2 == "" && preview.Item4 == "" && preview.Item6 == "",
                "removing the final stack leaves no stale current or preview");
        }
    }

    private static void Rollback()
    {
        MagicRevampCyclePatch.Reset();
        errors.Clear(); MagicRevampCyclePatch.Prepare(magic, backpacks);
        new Harmony(MagicRevampCyclePatch.Owner).Unpatch(names, HarmonyPatchType.Prefix, MagicRevampCyclePatch.Owner);
        MagicRevampCyclePatch.Verify();
        Check(errors.Count == 1 && Harmony.GetPatchInfo(cycle)?.Transpilers.All(p => p.owner != MagicRevampCyclePatch.Owner) != false,
            "a missing preview hook removes the cycle hook");
        VendorBehavior();
    }

    private static void LaterConflict()
    {
        errors.Clear(); changeNext = false;
        new Harmony(Conflict).Patch(cycle, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCycleTests), nameof(ChangeCount)))
            { priority = Priority.First });
        Check(errors.Count == 1 && errors[0].Contains("NO LONGER APPLIED"), "changed call site disables the group");
        VendorBehavior();
        MagicRevampCyclePatch.Prepare(magic, backpacks); MagicRevampCyclePatch.Verify();
        Check(errors.Count == 1 && Harmony.GetPatchInfo(cycle).Transpilers.Count(p => p.owner == MagicRevampCyclePatch.Owner) == 1,
            "reinstall after a conflict does not add duplicate hooks or enable a partial group");
        VendorBehavior();
        new Harmony(Conflict).UnpatchSelf();
    }

    private static void RollbackFailure()
    {
        MagicRevampCyclePatch.Reset();
        errors.Clear(); MagicRevampCyclePatch.Prepare(magic, backpacks);
        new Harmony(MagicRevampCyclePatch.Owner).Unpatch(names, HarmonyPatchType.Prefix, MagicRevampCyclePatch.Owner);
        new Harmony(Fixture).Patch(AccessTools.Method(typeof(Harmony), "UnpatchSelf"), prefix: Hook(nameof(RejectRollback)));
        rejectRollback = true;
        MagicRevampCyclePatch.Verify();
        rejectRollback = false;
        Check(errors.Count == 1 && errors[0].Contains("rollback failed"), "rollback failure is reported");
        VendorBehavior();
    }

    private static void InitialConflict()
    {
        MagicRevampCyclePatch.Reset();
        errors.Clear(); changeNext = true;
        new Harmony(Conflict).Patch(cycle, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCycleTests), nameof(ChangeCount)))
            { priority = Priority.First });
        MagicRevampCyclePatch.Prepare(magic, backpacks); MagicRevampCyclePatch.Verify();
        Check(errors.Count == 1 && errors[0].Contains("NOT APPLIED")
            && Harmony.GetPatchInfo(cycle)?.Transpilers.All(p => p.owner != MagicRevampCyclePatch.Owner) != false,
            "an unmatched next call rejects the group");
        VendorBehavior();
        new Harmony(Conflict).UnpatchSelf();
        changeNext = false;
    }

    private static void Reset()
    {
        inventory.GetAllItems().Clear(); book = null; equipped = null; message = "";
        equipCalls = showCalls = 0; staff = true;
        weapon = new ItemData { m_shared = new ItemData.SharedData { m_name = "$bmr_staff", m_ammoType = Ammo } };
        Current(null);
    }

    private static ItemData Scroll(string name, int x, int y) => new ItemData
    {
        m_gridPos = new Vector2i(x, y), m_stack = 1,
        m_shared = new ItemData.SharedData { m_name = "$bmr_scroll_" + name, m_itemType = ItemData.ItemType.Ammo, m_ammoType = Ammo }
    };

    private static void Current(ItemData? item) => ammoField.SetValue(player, item);
    private static void InvokeCycle() => cycle.Invoke(null, null);
    private static (int, string, int, string, int, string) Preview() =>
        ((int, string, int, string, int, string))names.Invoke(null, new object[] { player });
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(MagicRevampCycleTests), name));
    private static bool Book(ref Inventory? __result) { __result = book; return false; }
    private static bool Staff(ref bool __result) { __result = staff; return false; }
    private static bool Show() { showCalls++; return false; }
    private static bool Weapon(ref ItemData __result) { __result = weapon; return false; }
    private static bool Equip(ItemData __0, ref bool __result)
    {
        equipped = __0; equipCalls++; Current(__0); __result = true; return false;
    }
    private static bool Message(string __1) { message = __1; return false; }
    private static void RejectRollback(Harmony __instance)
    {
        if (rejectRollback && __instance.Id == MagicRevampCyclePatch.Owner)
            throw new InvalidOperationException("Injected rollback failure");
    }
    private static IEnumerable<CodeInstruction> ChangeCount(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo replacement = AccessTools.Method(typeof(MagicRevampCycleTests), changeNext ? nameof(VendorNext) : nameof(VendorCount));
        MethodInfo original = changeNext ? next : count;
        foreach (CodeInstruction instruction in instructions)
        {
            var copy = new CodeInstruction(instruction);
            if (copy.Calls(original)) { copy.opcode = OpCodes.Call; copy.operand = replacement; }
            yield return copy;
        }
    }
    private static int VendorCount() => (int)count.Invoke(null, null);
    private static ItemData VendorNext() => (ItemData)next.Invoke(null, null);
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
}
