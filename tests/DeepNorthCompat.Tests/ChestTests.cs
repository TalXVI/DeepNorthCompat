using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using AzuCraftyBoxes.IContainers;
using BepInEx.Configuration;
using BepInEx.Logging;
using DeepNorthCompat;
using HarmonyLib;
using MultiUserChest;
using MultiUserChest.Patches;
using UnityEngine;
using Plugin = DeepNorthCompat.Plugin;

// Real container state, serialization, MUC transfer and crafting hooks run.
// Native objects, spatial discovery, transport and output instantiation are fixtures.
internal static class ChestTests
{
    private sealed class PlayerOwner : InventoryOwner
    {
        private readonly Inventory inventory;
        private readonly ZNetView view;
        internal PlayerOwner(Inventory inventory, ZNetView view) { this.inventory = inventory; this.view = view; }
        public override Inventory Inventory => inventory;
        public override ZNetView ZNetView => view;
        public override bool IsValid() => true;
    }
    private static readonly Type sync = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ChestSyncPatch", true)!;
    private static readonly Type authority = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ChestCraftPatch", true)!;
    private static readonly Type diagnostics = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ChestDiagnostics", true)!;
    private static readonly Type registry = typeof(Plugin).Assembly.GetType("DeepNorthCompat.ChestRegistry", true)!;
    private static readonly Type observer = typeof(Plugin).Assembly.GetType("DeepNorthCompat.DiagnosticsPatch", true)!;
    private static readonly Type report = typeof(Plugin).Assembly.GetType("DeepNorthCompat.DiagnosticReport", true)!;
    private static readonly Type severity = typeof(Plugin).Assembly.GetType("DeepNorthCompat.Severity", true)!;
    private const string RequestRpc = "DeepNorthCompat.ChestHandoffRequest.v2";
    private static readonly Dictionary<UnityEngine.Object, string> names = new Dictionary<UnityEngine.Object, string>();
    private static readonly Dictionary<int, ItemDrop.ItemData> prototypes = new Dictionary<int, ItemDrop.ItemData>();
    private static readonly Dictionary<Inventory, InventoryOwner> owners = new Dictionary<Inventory, InventoryOwner>();
    private static readonly List<(long Target, string Method, object[] Arguments)> messages = new List<(long, string, object[])>();
    private static readonly List<RequestChestAdd> additions = new List<RequestChestAdd>();
    private static readonly List<IContainer> nearby = new List<IContainer>();
    private static readonly List<ZDO> zdos = new List<ZDO>();
    private static MethodInfo load = null!, stack = null!, craft = null!;
    private static ZDOMan man = null!;
    private static Player player = null!;
    private static float now;
    private static uint id = 50000;
    private static int outputs;
    private static bool failOutput;
    private static bool genericCraft;

    internal static void Run(string profile, Action<string, Action> test)
    {
        var fixture = new Harmony("DeepNorthCompat.Tests.Chests");
        Hook(fixture, typeof(UnityEngine.Object), "get_name", nameof(Name));
        Hook(fixture, typeof(Component), "get_transform", nameof(Transform));
        Hook(fixture, typeof(Component), "get_gameObject", nameof(GameObject));
        Hook(fixture, typeof(Transform), "get_position", nameof(Position));
        Hook(fixture, typeof(Transform), "get_rotation", nameof(Rotation));
        Hook(fixture, typeof(ZNet), "IsServer", nameof(False));
        Hook(fixture, typeof(ZDOMan), "ClientChanged", nameof(Skip));
        Hook(fixture, typeof(ZDOMan), "SetDirtySector", nameof(Skip));
        Hook(fixture, typeof(ZDOMan), "ForceSendZDO", nameof(Skip), new[] { typeof(long), typeof(ZDOID) });
        Hook(fixture, typeof(Container), "UpdateUseVisual", nameof(Skip));
        Hook(fixture, typeof(PrivateArea), "IsInside", nameof(True), new[] { typeof(Vector3), typeof(float) });
        Hook(fixture, typeof(Player), "GetCurrentCraftingStation", nameof(NoStation));
        Hook(fixture, typeof(Player), "GetPlayer", nameof(FindPlayer), new[] { typeof(long) });
        Hook(fixture, typeof(Player), "NoCostCheat", nameof(False));
        Hook(fixture, typeof(ZoneSystem), "GetGlobalKey", nameof(False), new[] { typeof(GlobalKeys) });
        Hook(fixture, typeof(Player), "Message", nameof(Skip), new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) });
        Hook(fixture, typeof(Time), "get_unscaledTime", nameof(TimeNow));
        Hook(fixture, typeof(SystemInfo), "get_operatingSystem", nameof(OperatingSystem));
        Hook(fixture, typeof(InventoryOwner), "GetOwner", nameof(FindOwner), new[] { typeof(Inventory) });
        Hook(fixture, typeof(GamePatches), "InvokeRPC", nameof(QueueMuc));
        Hook(fixture, typeof(ZNetView), "InvokeRPC", nameof(Queue), new[] { typeof(long), typeof(string), typeof(object[]) });
        Hook(fixture, typeof(Inventory), "AddItem", nameof(Hydrate), new[] { typeof(int), typeof(ItemDrop.ItemData), typeof(bool) });
        Hook(fixture, typeof(ZLog), "Log", nameof(Skip), new[] { typeof(object) });
        Hook(fixture, typeof(ZLog), "LogWarning", nameof(Skip), new[] { typeof(object) });
        Hook(fixture, typeof(BepInEx.ThreadingHelper), "StartSyncInvoke", nameof(Skip));
        AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField")
            .SetValue(null, Fake<BepInEx.ThreadingHelper>("thread-helper"));
        Assembly crafty = typeof(VanillaContainer).Assembly;
        Hook(fixture, crafty.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true)!, "ShouldPrevent", nameof(False));
        Type frame = crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame", true)!;
        fixture.Patch(AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(Player)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ChestTests), nameof(Query))));
        Invoke(registry, "Install", crafty);
        Invoke(authority, "Install", crafty);
        Assembly quick = Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/Goldenrevolver-Quick_Stack_Store_Sort_Trash_Restock/QuickStackStore.dll"));
        Invoke(sync, "Install", typeof(InventoryPatch).Assembly, quick);
        var muc = new Harmony("DeepNorthCompat.Tests.ChestMUC"); muc.PatchAll(typeof(InventoryPatch));
        load = AccessTools.Method(typeof(Container), "Load");
        stack = AccessTools.Method(quick.GetType("QuickStackStore.QuickStackModule", true)!, "QuickStackIntoThisContainer");
        craft = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
        fixture.Patch(craft, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChestTests), nameof(CraftBody))));
        fixture.Patch(AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int),
            typeof(long), typeof(string), typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool) }),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChestTests), nameof(AddBody))));
        Assembly impact = Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/MidnightMods-ImpactfulSkills/ImpactfulSkills.dll"));
        Invoke(typeof(Plugin).Assembly.GetType("DeepNorthCompat.QualityPatch", true)!, "Install", impact, crafty, false);
        var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unsaved-chests.cfg"), false) { SaveOnConfigSet = false };
        Type impactConfig = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        AccessTools.Field(impactConfig, "EnableQualityIngredientScaling").SetValue(null, config.Bind("chests", "amount", true));
        AccessTools.Field(impactConfig, "ScaleCraftedEquipmentQuality").SetValue(null, config.Bind("chests", "equipment", true));
        Type plugin = crafty.GetType("AzuCraftyBoxes.AzuCraftyBoxesPlugin", true)!;
        AccessTools.Field(plugin, "mRange").SetValue(null, config.Bind("chests", "range", 60f));
        Type toggle = plugin.GetNestedType("Toggle", BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo bind = typeof(ConfigFile).GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(ConfigDefinition));
        object leave = bind.MakeGenericMethod(toggle).Invoke(config, new object[] { new ConfigDefinition("chests", "leave"), Enum.ToObject(toggle, 0), new ConfigDescription("fixture") });
        AccessTools.Field(plugin, "leaveOne").SetValue(null, leave);
        AccessTools.Field(plugin, "debugLogsEnabled").SetValue(null, bind.MakeGenericMethod(toggle).Invoke(config,
            new object[] { new ConfigDefinition("chests", "debug"), Enum.ToObject(toggle, 0), new ConfigDescription("fixture") }));
        AccessTools.Field(plugin, "yamlData").SetValue(null, new Dictionary<string, Dictionary<string, List<string>>>());
        Type quickConfig = quick.GetType("QuickStackStore.QSSConfig+QuickStackConfig", true)!;
        AccessTools.Field(quickConfig, "QuickStackTrophiesIntoSameContainer").SetValue(null, config.Bind("chests", "trophies", false));
        ZDOMan previousMan = ZDOMan.instance; ZNet previousNet = ZNet.instance;
        Player previousPlayer = Player.m_localPlayer;
        object previousRouted = AccessTools.Field(typeof(ZRoutedRpc), "s_instance").GetValue(null);
        man = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
        AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, man); Peer(100);
        AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, Fake<ZNet>("net"));
        AccessTools.Field(typeof(ZRoutedRpc), "s_instance").SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZRoutedRpc)));
        player = Fake<Player>("player"); SetView(typeof(Player), player, View(100)); SetView(typeof(Character), player, View(100));
        AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, player);
        AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, PlayerInventory());
        try
        {
            void Case(string name, Action run) => test("chests: " + name, () =>
            {
                Invoke(registry, "Reset"); Invoke(diagnostics, "Clear"); Invoke(report, "Clear"); Peer(100); now = 0; outputs = 0; failOutput = false; genericCraft = false; messages.Clear(); additions.Clear(); nearby.Clear();
                player.GetInventory().GetAllItems().Clear(); run();
            });
            bool server = (bool)Invoke(typeof(Guard), "KnownServerBuild")!;
            void ClientCase(string name, Action run) { if (!server) Case(name, run); }
            Case("remote owner can receive Quick Stack without ownership transfer", () =>
            {
                Container chest = Chest(200); chest.GetInventory().GetAllItems().Add(Item("wood", 10));
                player.GetInventory().GetAllItems().Add(Item("wood", 5)); InventoryPatch.AddItemPostfix(player.GetInventory());
                Check(Stack(chest) == 1 && additions.Count == 1 && !chest.IsOwner(), "remote request");
                var authoritative = new Inventory("owner", null, 5, 3); authoritative.GetAllItems().Add(Item("wood", 10));
                Check(ContainerRPCHandler.RequestItemAdd(authoritative, additions[0]).Success && authoritative.CountItems("wood") == 15, "owner deposit");
            });
            Case("owner loss releases use and restores actual loading and Quick Stack", () =>
            {
                Container chest = Chest(100); chest.SetInUse(true); ChestZdo(chest).SetOwner(200);
                Payload(chest, "wood", 10); player.GetInventory().GetAllItems().Add(Item("wood", 5)); InventoryPatch.AddItemPostfix(player.GetInventory());
                Check((bool)load.Invoke(chest, null) && !chest.IsInUse(), "non-owner MUC refresh released flag");
                Check(Adapter(chest).ItemCount("wood") == 10 && Stack(chest) == 1, "both consumers restored");
            });
            Case("closing as non-owner clears only local use", () =>
            {
                Container chest = Chest(100); chest.SetInUse(true); ChestZdo(chest).Set(ZDOVars.s_inUse, 1); ChestZdo(chest).SetOwner(200);
                uint before = ChestZdo(chest).DataRevision; chest.SetInUse(false);
                Check(!chest.IsInUse() && ChestZdo(chest).DataRevision == before && ChestZdo(chest).GetInt(ZDOVars.s_inUse) == 1, "network state retained");
            });
            Case("owner's active edits still block loading", () =>
            {
                Container chest = Chest(100); chest.GetInventory().GetAllItems().Add(Item("wood", 7)); chest.SetInUse(true); Payload(chest, "wood", 10);
                Check(!(bool)load.Invoke(chest, null) && chest.GetInventory().CountItems("wood") == 7 && chest.IsInUse(), "active edits protected");
            });
            ClientCase("craft waits for both owner grant and current chest data", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player });
                Check(outputs == 0 && chest.GetInventory().CountItems("wood") == 10 && messages.Count == 1, "no output or consumption before grant");
                Grant(chest, messages[0]);
                // Ownership alone cannot release the wait before the matching reply.
                craft.Invoke(gui, new object[] { player }); Check(outputs == 0, "waits for owner reply");
                var reply = messages.Last(); Invoke(authority, "ReceiveReply", chest, 200L, reply.Arguments[0]);
                craft.Invoke(gui, new object[] { player });
                Check(outputs == 1 && chest.GetInventory().CountItems("wood") == 5 && chest.IsOwner(), "authoritative craft consumed once");
                var saved = new Inventory("saved", null, 5, 3); saved.Load(new ZPackage(ChestZdo(chest).GetByteArray(ZDOVars.s_items)));
                Check(saved.CountItems("wood") == 5, "owner payload saved");
            });
            ClientCase("failed output rolls back handed-off chest and player inventory", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); Grant(chest, messages[0]);
                Invoke(authority, "ReceiveReply", chest, 200L, messages.Last().Arguments[0]); failOutput = true;
                craft.Invoke(gui, new object[] { player });
                Check(outputs == 0 && chest.GetInventory().CountItems("wood") == 10 && player.GetInventory().CountItems("output") == 0, "rollback");
            });
            ClientCase("ordinary ingredients use Crafty's actual owner-side consumption and save", () =>
            {
                genericCraft = true;
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                Recipe recipe = (Recipe)AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").GetValue(gui);
                recipe.m_resources[0].m_resItem.m_itemData.m_shared.m_maxQuality = 1;
                player.GetInventory().GetAllItems().Add(Item("wood", 2));
                craft.Invoke(gui, new object[] { player }); Check(outputs == 0, "ordinary craft waits");
                Grant(chest, messages[0]); Invoke(authority, "ReceiveReply", chest, 200L, messages.Last().Arguments[0]); craft.Invoke(gui, new object[] { player });
                Check(outputs == 1 && chest.IsOwner() && chest.GetInventory().CountItems("wood") == 7
                    && player.GetInventory().CountItems("wood") == 0, "actual Crafty removed split ingredients");
                var saved = new Inventory("saved", null, 5, 3); saved.Load(new ZPackage(ChestZdo(chest).GetByteArray(ZDOVars.s_items)));
                Check(saved.CountItems("wood") == 7, "actual owner payload saved");
            });
            ClientCase("refresh invalidates Crafty's same-frame inventory bank", () =>
            {
                Type bank = crafty.GetType("AzuCraftyBoxes.Util.Functions.UiItemBank", true)!;
                Container chest = Chest(100); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest));
                Invoke(authority, "InvalidateQuery");
                Invoke(bank, "Begin", nearby); Check((int)Invoke(bank, "GetTotalAnyQuality", "wood")! == 10, "cached preview");
                Payload(chest, "wood", 4); ChestSync(chest); Invoke(authority, "InvalidateQuery");
                Invoke(bank, "Begin", nearby); Check((int)Invoke(bank, "GetTotalAnyQuality", "wood")! == 4, "refreshed same-frame count");
            });
            ClientCase("grant and ownership still wait for the promised data revision", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player });
                uint revision = ChestZdo(chest).DataRevision;
                Invoke(authority, "ReceiveReply", chest, 200L, Reply(Nonce(messages[0]), 0, "", revision + 1, "", 100L)); ChestZdo(chest).SetOwner(100);
                craft.Invoke(gui, new object[] { player }); Check(outputs == 0 && Pending() != null, "waits for payload");
                Payload(chest, "wood", 12); craft.Invoke(gui, new object[] { player });
                Check(outputs == 1 && chest.GetInventory().CountItems("wood") == 7, "latest payload consumed");
            });
            ClientCase("every selected chest must grant before split ingredients are consumed", () =>
            {
                Container first = Chest(200), second = Chest(200); Payload(first, "wood", 2); Payload(second, "wood", 4);
                ChestSync(first); ChestSync(second); nearby.Add(Adapter(first)); nearby.Add(Adapter(second)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); Check(messages.Count == 2 && outputs == 0, "two requests");
                var requests = messages.ToArray(); Grant(first, requests[0]); Invoke(authority, "ReceiveReply", first, 200L, messages.Last().Arguments[0]);
                craft.Invoke(gui, new object[] { player }); Check(outputs == 0 && first.GetInventory().CountItems("wood") == 2, "partial grant cannot consume");
                Grant(second, requests[1]); Invoke(authority, "ReceiveReply", second, 200L, messages.Last().Arguments[0]); craft.Invoke(gui, new object[] { player });
                Check(outputs == 1 && first.GetInventory().CountItems("wood") + second.GetInventory().CountItems("wood") == 1, "consumed five once");
            });
            ClientCase("deferred UpdateRecipe keeps AAA's crafting timer active", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player });
                FieldInfo timer = AccessTools.Field(typeof(InventoryGui), "m_craftTimer"); timer.SetValue(gui, -1f);
                Invoke(authority, "KeepTimer", gui);
                Check((float)timer.GetValue(gui) == 2f && !(bool)Invoke(authority, "Update", gui)!, "timer remains active while waiting");
                Invoke(authority, "Cancel"); Check((float)timer.GetValue(gui) == -1f, "cancel ends timer");
            });
            ClientCase("consumption excludes new remote sources without changing preview cache", () =>
            {
                Container owned = Chest(100), remote = Chest(200); nearby.Add(Adapter(owned)); nearby.Add(Adapter(remote));
                AccessTools.Field(authority, "consuming").SetValue(null, true);
                try
                {
                    var selected = (List<IContainer>)AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(Player))
                        .Invoke(null, new object[] { player, 60f });
                    Check(selected.Count == 1 && selected[0].GetInventory() == owned.GetInventory() && nearby.Count == 2, "owned-only snapshot");
                }
                finally { AccessTools.Field(authority, "consuming").SetValue(null, false); }
            });
            ClientCase("forged reply cannot authorize output", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player });
                Invoke(authority, "ReceiveReply", chest, 999L, Reply(Nonce(messages[0]), 0, "", ChestZdo(chest).DataRevision, "", 100L));
                craft.Invoke(gui, new object[] { player });
                Check(outputs == 0 && chest.GetInventory().CountItems("wood") == 10, "wrong sender ignored");
            });
            ClientCase("cancelling while awaiting a handoff cannot craft after a late reply", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); Grant(chest, messages[0]); Invoke(authority, "Cancel");
                Invoke(authority, "ReceiveReply", chest, 200L, messages.Last().Arguments[0]);
                Check(outputs == 0 && chest.GetInventory().CountItems("wood") == 10 && Pending() == null, "late reply inert");
            });
            Case("owner explains each denial and redirects requests it cannot answer", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); Peer(200);
                Ask(chest, 999L, 1, 700L); Check(LastReply().Status == 1 && LastReply().Reason == "identity-mismatch" && ChestZdo(chest).GetOwner() == 200, "identity checked");
                AccessTools.Field(typeof(Container), "m_inUse").SetValue(chest, true);
                Ask(chest, 100L, 2, 700L); Check(LastReply().Reason == "in-use" && ChestZdo(chest).GetOwner() == 200, "busy chest checked");
                AccessTools.Field(typeof(Container), "m_inUse").SetValue(chest, false);
                Ask(chest, 100L, 3, 701L); Check(LastReply().Reason == "requester-not-loaded", "unloaded requester named");
                ChestZdo(chest).SetOwner(300);
                Ask(chest, 100L, 4, 700L); Check(LastReply().Status == 2 && LastReply().Owner == 300 && ChestZdo(chest).GetOwner() == 300, "non-owner redirects");
            });
            Case("owner moves its copy above an equal or newer requester revision", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); Peer(200); uint revision = ChestZdo(chest).DataRevision;
                Ask(chest, 100L, 1, 700L, 0);
                Check(LastReply().Status == 0 && LastReply().Revision == revision && LastReply().Owner == 100, "older requester keeps the owner's revision");
                Check(LastReply().Hash == (string)Invoke(registry, "PayloadHash", ChestZdo(chest))!, "payload hash sent");
                ChestZdo(chest).SetOwner(200); Ask(chest, 100L, 2, 700L, revision);
                Check(LastReply().Revision == revision + 1 && ChestZdo(chest).DataRevision == revision + 1, "equal revision advanced");
                ChestZdo(chest).SetOwner(200); Ask(chest, 100L, 3, 700L, revision + 5);
                Check(LastReply().Revision == revision + 6 && (uint)Invoke(registry, "LoadedRevision", chest)! == revision + 6, "newer requester revision overtaken");
            });
            Case("every loaded chest answers handoffs, independent of the other chest groups", () =>
            {
                Container chest = Chest(200); ZNetView view = (ZNetView)Invoke(registry, "View", chest)!;
                var functions = (IDictionary)AccessTools.Field(typeof(ZNetView), "m_functions").GetValue(view);
                Check(functions.Contains(RequestRpc.GetStableHashCode()), "request handler registered at load");
                Invoke(registry, "Track", chest); Check((int)AccessTools.Property(registry, "Count").GetValue(null) == 1, "tracking is idempotent");
            });
            Case("reset and destroyed views unload chests without errors", () =>
            {
                Container chest = Chest(100); ZNetView view = (ZNetView)Invoke(registry, "View", chest)!; ZDOID chestId = ChestZdo(chest).m_uid;
                SetViewZdo(view, null!);
                Check(AccessTools.Method(registry, "Find", new[] { typeof(ZDOID) }).Invoke(null, new object[] { chestId }) == null && ((Container[])AccessTools.Property(registry, "Containers").GetValue(null)).Length == 0, "reset view unloaded");
                Invoke(diagnostics, "Record", "probe", chest, "", Enum.ToObject(severity, 0), "");
                Invoke(registry, "Forget", view);
                Check(Invoke(registry, "IdOf", chest) == null && (int)AccessTools.Property(registry, "Count").GetValue(null) == 0, "destroyed view forgotten");
            });
            Case("ward permissions authorize the requester without a local player", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); Peer(200);
                var areas = (List<PrivateArea>)AccessTools.Field(typeof(PrivateArea), "m_allAreas").GetValue(null);
                PrivateArea ward = Fake<PrivateArea>("ward"); ZNetView wardView = View(200); SetView(typeof(PrivateArea), ward, wardView);
                wardView.GetZDO().Set(ZDOVars.s_enabled, true); ward.m_radius = 32;
                Piece piece = Fake<Piece>("ward-piece"); AccessTools.Field(typeof(Piece), "m_creator").SetValue(piece, 999L);
                AccessTools.Field(typeof(PrivateArea), "m_piece").SetValue(ward, piece); areas.Add(ward);
                AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, null);
                try
                {
                    Ask(chest, 100L, 1, 700L);
                    Check(LastReply().Reason == "ward" && ChestZdo(chest).GetOwner() == 200, "unpermitted requester denied");
                    wardView.GetZDO().Set(ZDOVars.s_permitted, 1); wardView.GetZDO().Set("pu_id0", 700L); wardView.GetZDO().Set("pu_name0", "requester");
                    Ask(chest, 100L, 2, 700L);
                    Check(LastReply().Status == 0 && ChestZdo(chest).GetOwner() == 100, "explicit requester permission granted");
                }
                finally { areas.Remove(ward); AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, player); }
            });
            ClientCase("a request reaching a former owner is resent to the current owner", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); var first = messages[0];
                ChestZdo(chest).SetOwner(300);
                Grant(chest, first, 200); Invoke(authority, "ReceiveReply", chest, 200L, messages.Last().Arguments[0]);
                Invoke(authority, "Ready");
                Check(messages.Last().Target == 300 && messages.Last().Method == RequestRpc && outputs == 0, "resent to the new owner");
                Grant(chest, messages.Last(), 300); Invoke(authority, "ReceiveReply", chest, 300L, messages.Last().Arguments[0]);
                craft.Invoke(gui, new object[] { player });
                Check(outputs == 1 && chest.GetInventory().CountItems("wood") == 5, "new owner's grant crafted once");
            });
            ClientCase("a granted payload that differs from the owner's cancels before consumption", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); Grant(chest, messages[0]); var granted = LastReply();
                Invoke(authority, "ReceiveReply", chest, 200L, Reply(granted.Nonce, 0, "", granted.Revision, new string('0', 64), 100L));
                craft.Invoke(gui, new object[] { player });
                Check(Pending() == null && outputs == 0 && chest.GetInventory().CountItems("wood") == 10, "diverged payload refused");
            });
            ClientCase("a denial cancels with the owner's reason", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player });
                Invoke(authority, "ReceiveReply", chest, 200L, Reply(Nonce(messages[0]), 1, "in-use", 0, "", 200L));
                Check(Pending() == null && outputs == 0, "denied craft cancelled");
            });
            ClientCase("a crafting peer denies its planned and granted chests to others", () =>
            {
                Container owned = Chest(100), remote = Chest(200); Payload(owned, "wood", 2); Payload(remote, "wood", 4);
                ChestSync(owned); ChestSync(remote); nearby.Add(Adapter(owned)); nearby.Add(Adapter(remote)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); Check(messages.Count == 1, "one remote request");
                Ask(owned, 300L, 9, 700L); Check(LastReply().Reason == "owner-crafting", "planned chest held");
                Grant(remote, messages[0]); Invoke(authority, "ReceiveReply", remote, 200L, messages.Last().Arguments[0]);
                Check((bool)Invoke(authority, "Ready")!, "handoff complete");
                Ask(remote, 300L, 10, 700L); Check(LastReply().Reason == "owner-crafting", "granted chest reserved until the craft runs");
                craft.Invoke(gui, new object[] { player }); Check(outputs == 1, "crafted");
                Ask(remote, 300L, 11, 700L); Check(LastReply().Reason == "identity-mismatch", "reservation released after the craft");
            });
            ClientCase("single-ingredient recipes hand off only the source Crafty uses", () =>
            {
                InventoryGui gui = Gui(); Recipe recipe = (Recipe)AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").GetValue(gui);
                ItemDrop stone = Fake<ItemDrop>("stone"); stone.m_itemData = Item("stone", 1);
                recipe.m_requireOnlyOneIngredient = true;
                recipe.m_resources = new[] { recipe.m_resources[0], new Piece.Requirement { m_resItem = stone, m_amount = 5 } };
                Container partial = Chest(200), full = Chest(200); Payload(partial, "wood", 3); Payload(full, "stone", 10);
                ChestSync(partial); ChestSync(full); nearby.Add(Adapter(partial)); nearby.Add(Adapter(full));
                craft.Invoke(gui, new object[] { player });
                Check(messages.Count == 1 && Requested().Single() == full, "only the full source requested");
                Invoke(authority, "Cancel"); messages.Clear(); player.GetInventory().GetAllItems().Add(Item("wood", 5));
                craft.Invoke(gui, new object[] { player });
                Check(messages.Count == 0 && outputs == 1, "the player's own ingredient needs no handoff");
            });
            ClientCase("an unloaded chest cancels a waiting craft", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); SetViewZdo((ZNetView)Invoke(registry, "View", chest)!, null!);
                Check(!(bool)Invoke(authority, "Ready")! && Pending() == null && outputs == 0, "cancelled");
            });
            ClientCase("craft records show what moved and flag output without consumption", () =>
            {
                string directory = Sandbox();
                Invoke(diagnostics, "Configure", directory, false, 1L << 20);
                try
                {
                    Container chest = Chest(100); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                    Recipe recipe = (Recipe)AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").GetValue(gui);
                    recipe.m_resources[0].m_resItem.m_itemData.m_shared.m_maxQuality = 1;
                    craft.Invoke(gui, new object[] { player });
                    genericCraft = true; craft.Invoke(gui, new object[] { player });
                    string text = Report();
                    Check(text.Contains("PROBLEM craft-output-without-consumption") && text.Contains("delta=output:q1:w0=+1"), "unbalanced craft flagged");
                    Check(Logs(directory).Contains("craft-result") && Logs(directory).Contains("wood:q1:w0=-5"), "balanced craft recorded");
                }
                finally { Invoke(diagnostics, "Disable"); }
            });
            ClientCase("handoff timeout cancels before consumption", () =>
            {
                Container chest = Chest(200); Payload(chest, "wood", 10); ChestSync(chest); nearby.Add(Adapter(chest)); InventoryGui gui = Gui();
                craft.Invoke(gui, new object[] { player }); now = 9; Invoke(authority, "Ready");
                Check(Pending() == null && outputs == 0 && chest.GetInventory().CountItems("wood") == 10, "timeout conserved items");
            });
            Case("diagnostics capture owner loss, arrivals, non-owner saves and a pasteable report", () =>
            {
                string directory = Sandbox();
                Invoke(diagnostics, "Configure", directory, false, 2400L); Invoke(observer, "Install");
                try
                {
                    Container chest = Chest(100); chest.SetInUse(true); ChestZdo(chest).SetOwner(200); Payload(chest, "wood", 10); load.Invoke(chest, null);
                    string records = Logs(directory);
                    Check(records.Contains("local-use-release") && records.Contains("MUC-non-owner-refresh") && records.Contains("inventory-refreshed")
                        && records.Contains("wood:q1:w0=10") && records.Contains("from=100;to=200"), "actionable state records");
                    MethodInfo received = AccessTools.Method(observer, "Received");
                    var packet = new ZPackage(); packet.Write(0); packet.Write(ChestZdo(chest).m_uid); packet.Write((ushort)4); packet.Write(ChestZdo(chest).DataRevision + 1);
                    packet.Write(300L); packet.Write(Vector3.zero); packet.Write(new ZPackage()); packet.Write(ZDOID.None);
                    var arrival = new ZPackage(packet.GetArray()); received.Invoke(null, new object?[] { null, arrival });
                    Check(arrival.GetPos() == 0 && Logs(directory).Contains("incoming_owner=300") && Logs(directory).Contains("applies_data=True"), "arrival evidence");
                    var truncated = new ZPackage(); truncated.Write(1); var broken = new ZPackage(truncated.GetArray());
                    received.Invoke(null, new object?[] { null, broken });
                    Check(broken.GetPos() == 0 && Logs(directory).Contains("diagnostics-hook-error"), "malformed packet contained");
                    AccessTools.Method(typeof(Container), "Save").Invoke(chest, null);
                    Invoke(diagnostics, "Mark", "tester", "chest showed old items");
                    MethodInfo capture = AccessTools.Method(report, "Capture");
                    for (int i = 0; i < 2; i++) capture.Invoke(null, new object[] { "OtherMod", LogLevel.Error, "NullReferenceException: boom\n  at Other.Method ()" });
                    capture.Invoke(null, new object[] { "DeepNorthCompat", LogLevel.Info, "Chests: APPLIED; fixture" });
                    var hello = new ZPackage(); hello.Write("0.0.1"); hello.Write("server"); hello.Write(0L); hello.Write("Chests");
                    Invoke(report, "ReceiveHello", 1L, hello);
                    string text = Report();
                    Check(text.Contains("## Problems and warnings") && text.Contains("WARNING local-use-release") && text.Contains("from=100;to=200"), "finding with its chest history");
                    Check(text.Contains("PROBLEM NON_OWNER_WRITE") && text.Contains("stack: "), "non-owner save with its caller");
                    Check(text.Contains("tester: chest showed old items") && text.Contains("from OtherMod, 2x") && text.Contains("Chests: APPLIED; fixture"), "marker, deduplicated error and own log");
                    Check(text.Contains("PROBLEM server-hello") && text.Contains("Server: DeepNorthCompat 0.0.1"), "version mismatch");
                    Invoke(report, "WorldShutdown");
                    Check(File.Exists(Path.Combine(directory, "reports", "DeepNorthCompat-report-client-latest.md")), "latest report saved");
                    for (int i = 0; i < 25; i++)
                    {
                        string old = Path.Combine(directory, "DeepNorthCompat-events-client-20200101T000000-" + i.ToString("x12") + "-001.tsv");
                        File.WriteAllText(old, "#\n"); File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1).AddMinutes(i));
                    }
                    for (int i = 0; i < 400; i++) Invoke(diagnostics, "Record", "rotation-test", chest, "record=" + i, Enum.ToObject(severity, 0), "");
                    Invoke(diagnostics, "Close");
                    string session = (string)AccessTools.Field(diagnostics, "Session").GetValue(null);
                    FileInfo[] files = new DirectoryInfo(directory).GetFiles("DeepNorthCompat-events-client-*.tsv");
                    FileInfo[] current = files.Where(file => file.Name.Contains("-" + session + "-")).ToArray();
                    Check(files.Select(file => file.Name.Split('-')[4]).Distinct().Count() == 20, "newest sessions retained");
                    Check(current.Length == 17 && current.Any(file => file.Name.EndsWith("-001.tsv")) && files.All(file => file.Length <= 2400), "parts bounded, start kept");
                }
                finally { new Harmony("DeepNorthCompat.Diagnostics").UnpatchSelf(); Invoke(diagnostics, "Disable"); }
                Invoke(diagnostics, "Configure", directory, true, 2400L); Invoke(diagnostics, "Close");
                Check(Directory.GetFiles(directory, "*server*.tsv").Length == 1, "server directory role"); Invoke(diagnostics, "Disable");
            });
        }
        finally
        {
            Invoke(registry, "Reset"); Invoke(diagnostics, "Disable"); Invoke(diagnostics, "Clear"); Invoke(report, "Clear"); fixture.UnpatchSelf(); muc.UnpatchSelf();
            foreach (string owner in new[] { "Chests.Registry", "Chests", "ChestCraft", "Diagnostics", "Quality" }) new Harmony("DeepNorthCompat." + owner).UnpatchSelf();
            AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, previousMan); AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, previousNet);
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer); AccessTools.Field(typeof(ZRoutedRpc), "s_instance").SetValue(null, previousRouted);
        }
    }
    private static object? Invoke(Type type, string method, params object?[] arguments) => AccessTools.Method(type, method).Invoke(null,
        arguments.Select(value => value is ZPackage packet ? new ZPackage(packet.GetArray()) : value).ToArray());
    private static string ReadLog(string path)
    { using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) return reader.ReadToEnd(); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Peer(long peer)
    {
        AccessTools.Field(typeof(ZDOMan), "m_sessionID").SetValue(man, peer);
        foreach (ZDO zdo in zdos) zdo.SetOwnerInternal(zdo.GetOwner());
    }
    private static object? Pending() => AccessTools.Field(authority, "pending").GetValue(null);
    private static Container[] Requested()
    {
        object craft = Pending()!;
        return ((IEnumerable)AccessTools.Field(craft.GetType(), "Requests").GetValue(craft)).Cast<object>()
            .Select(request => (Container)AccessTools.Field(request.GetType(), "Chest").GetValue(request)).ToArray();
    }
    private static long Nonce((long Target, string Method, object[] Arguments) request) => new ZPackage(((ZPackage)request.Arguments[0]).GetArray()).ReadLong();
    private static (long Nonce, byte Status, string Reason, uint Revision, string Hash, long Owner) LastReply()
    {
        var packet = new ZPackage(((ZPackage)messages.Last().Arguments[0]).GetArray());
        return (packet.ReadLong(), packet.ReadByte(), packet.ReadString(), packet.ReadUInt(), packet.ReadString(), packet.ReadLong());
    }
    private static ZPackage Reply(long nonce, byte status, string reason, uint revision, string hash, long owner)
    {
        var packet = new ZPackage(); packet.Write(nonce); packet.Write(status); packet.Write(reason); packet.Write(revision); packet.Write(hash); packet.Write(owner); return packet;
    }
    // Sends a handoff request from another peer to the chest's owner on this peer.
    private static void Ask(Container chest, long sender, long nonce, long playerId, uint revision = 0)
    {
        var packet = new ZPackage(); packet.Write(nonce); packet.Write(playerId); packet.Write(revision);
        Invoke(authority, "ReceiveRequest", chest, sender, packet);
    }
    // The owner runs in another process, so it must not see this peer's pending craft.
    private static void Grant(Container chest, (long Target, string Method, object[] Arguments) request, long owner = 200)
    {
        FieldInfo field = AccessTools.Field(authority, "pending"); object? local = field.GetValue(null); field.SetValue(null, null);
        try { Peer(owner); Invoke(authority, "ReceiveRequest", chest, 100L, request.Arguments[0]); }
        finally { Peer(100); field.SetValue(null, local); }
    }
    private static string Sandbox() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox", "chest-logs-" + Guid.NewGuid().ToString("N"));
    private static string Logs(string directory) => string.Join("", Directory.GetFiles(directory, "*.tsv").Select(ReadLog));
    private static string Report() => (string)AccessTools.Method(report, "Build", new[] { typeof(bool) }).Invoke(null, new object[] { false });
    private static void ChestSync(Container chest) => load.Invoke(chest, null);
    private static VanillaContainer Adapter(Container chest)
    { var adapter = new VanillaContainer(chest); AccessTools.Field(typeof(VanillaContainer), "_prefabName").SetValue(adapter, "chest"); return adapter; }
    private static T Fake<T>(string name) where T : UnityEngine.Object
    { var value = (T)FormatterServices.GetUninitializedObject(typeof(T)); AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id)); names[value] = name; return value; }
    private static void SetView(Type type, object target, ZNetView view) => AccessTools.Field(type, "m_nview").SetValue(target, view);
    private static ZNetView View(long owner)
    {
        ZNetView view = Fake<ZNetView>("view"); var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); zdo.m_uid = new ZDOID(100, ++id); zdo.SetOwner(owner);
        zdos.Add(zdo);
        zdo.Set(ZDOVars.s_playerID, 700L); SetViewZdo(view, zdo);
        FieldInfo functions = AccessTools.Field(typeof(ZNetView), "m_functions"); functions.SetValue(view, Activator.CreateInstance(functions.FieldType)); return view;
    }
    private static void SetViewZdo(ZNetView view, ZDO zdo) => AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
    private static ZDO ChestZdo(Container chest) => ((ZNetView)AccessTools.Field(typeof(Container), "m_nview").GetValue(chest)).GetZDO();
    private static Container Chest(long owner)
    {
        Container chest = Fake<Container>("chest"); SetView(typeof(Container), chest, View(owner));
        AccessTools.Field(typeof(Container), "m_inventory").SetValue(chest, new Inventory("chest", null, 5, 3));
        AccessTools.Field(typeof(Container), "m_width").SetValue(chest, 5); AccessTools.Field(typeof(Container), "m_height").SetValue(chest, 3);
        AccessTools.Field(typeof(Container), "m_lastRevision").SetValue(chest, uint.MaxValue);
        AccessTools.Field(typeof(Container), "m_privacy").SetValue(chest, Container.PrivacySetting.Public);
        chest.m_openEffects = new EffectList(); chest.m_closeEffects = new EffectList();
        owners[chest.GetInventory()] = new ContainerInventoryOwner(chest); Invoke(registry, "Track", chest); return chest;
    }
    private static Inventory PlayerInventory()
    { var inventory = new Inventory("player", null, 8, 4); owners[inventory] = new PlayerOwner(inventory, View(100)); return inventory; }
    private static ItemDrop.ItemData Item(string name, int amount)
    {
        var item = new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData { m_name = name, m_maxStackSize = 50, m_maxQuality = 3 },
            m_stack = amount, m_quality = 1, m_gridPos = new Vector2i(0, 0), m_dropPrefab = Fake<GameObject>(name) };
        prototypes[name.GetStableHashCode()] = item; return item;
    }
    private static void Payload(Container chest, string name, int count)
    { var inventory = new Inventory("payload", null, 5, 3); inventory.GetAllItems().Add(Item(name, count)); var packet = new ZPackage(); inventory.Save(packet); ChestZdo(chest).Set(ZDOVars.s_items, packet.GetArray()); }
    private static int Stack(Container chest) => (int)stack.Invoke(null, new object[] { null!, player.GetInventory().GetAllItems().ToList(), player.GetInventory(), chest.GetInventory(), false });
    private static InventoryGui Gui()
    {
        var gui = Fake<InventoryGui>("gui"); Recipe recipe = Fake<Recipe>("recipe"); ItemDrop input = Fake<ItemDrop>("wood"); input.m_itemData = Item("wood", 1);
        recipe.m_resources = new[] { new Piece.Requirement { m_resItem = input, m_amount = 5 } }; recipe.m_item = Fake<ItemDrop>("output"); recipe.m_item.m_itemData = Item("output", 1);
        AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").SetValue(gui, recipe); AccessTools.Field(typeof(InventoryGui), "m_craftTimer").SetValue(gui, 2f); return gui;
    }
    private static IEnumerable<CodeInstruction> CraftBody(IEnumerable<CodeInstruction> _) => new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ChestTests), nameof(CraftOutput))), new CodeInstruction(OpCodes.Ret) };
    private static void CraftOutput(InventoryGui gui, Player value)
    {
        if (value.GetInventory().AddItem("output", 1, 1, 0, 0L, "", new Vector2i(-1, -1), false, false, false) == null || !genericCraft) return;
        Recipe recipe = (Recipe)AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").GetValue(gui);
        Assembly crafty = typeof(VanillaContainer).Assembly;
        Type frame = crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes+QueryFrame", true)!;
        object containers = AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(Player)).Invoke(null, new object[] { value, 60f });
        Invoke(crafty.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true)!, "ProcessRequirements",
            recipe.m_resources, 1, value.GetInventory(), containers, -1, 1, null);
    }
    private static IEnumerable<CodeInstruction> AddBody(IEnumerable<CodeInstruction> _) => new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ChestTests), nameof(AddOutput))), new CodeInstruction(OpCodes.Ret) };
    private static ItemDrop.ItemData? AddOutput(Inventory inventory)
    { if (failOutput) return null; var item = Item("output", 1); inventory.GetAllItems().Add(item); outputs++; return item; }
    private static void Hook(Harmony harmony, Type type, string method, string prefix, Type[]? arguments = null) => harmony.Patch(AccessTools.Method(type, method, arguments), prefix: new HarmonyMethod(AccessTools.Method(typeof(ChestTests), prefix)));
    private static bool Skip() => false;
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool Name(UnityEngine.Object __instance, ref string __result) { __result = names.TryGetValue(__instance, out string? name) ? name : "fixture"; return false; }
    private static bool Transform(ref Transform __result) { __result = Fake<Transform>("transform"); return false; }
    private static bool GameObject(Component __instance, ref GameObject __result) { __result = Fake<GameObject>(names[__instance]); return false; }
    private static bool Position(ref Vector3 __result) { __result = Vector3.zero; return false; }
    private static bool Rotation(ref Quaternion __result) { __result = Quaternion.identity; return false; }
    private static bool NoStation(ref CraftingStation __result) { __result = null!; return false; }
    private static bool FindPlayer(long __0, ref Player __result) { __result = __0 == 700 ? player : null!; return false; }
    private static bool TimeNow(ref float __result) { __result = now; return false; }
    private static bool OperatingSystem(ref string __result) { __result = "offline"; return false; }
    private static bool FindOwner(Inventory __0, ref InventoryOwner __result) { __result = owners.TryGetValue(__0, out InventoryOwner? owner) ? owner : null!; return false; }
    private static bool Queue(long __0, string __1, object[] __2) { messages.Add((__0, __1, __2)); return false; }
    private static bool QueueMuc(IPackage __2) { additions.Add((RequestChestAdd)__2); return false; }
    private static bool Query(ref List<IContainer> __result) { __result = nearby; return false; }
    private static bool Hydrate(Inventory __instance, int __0, ItemDrop.ItemData __1, ref bool __result)
    { ItemDrop.ItemData prototype = prototypes[__0]; __1.m_shared = prototype.m_shared; __1.m_dropPrefab = prototype.m_dropPrefab; __instance.GetAllItems().Add(__1); __result = true; return false; }
}
