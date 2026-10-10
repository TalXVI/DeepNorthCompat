using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class OdinShipTests
{
    private const string Fixture = "DeepNorthCompat.Tests.OdinShip";
    private static readonly Type core = typeof(Plugin).Assembly.GetType("DeepNorthCompat.OdinShipPatch", true)!;
    private static readonly Type input = typeof(Plugin).Assembly.GetType("DeepNorthCompat.OdinShipInputPatch", true)!;
    private static readonly Type fishPatch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.OdinShipFishPatch", true)!;
    private static readonly Dictionary<Object, string> names = new Dictionary<Object, string>();
    private static readonly List<ZDO> zdos = new List<ZDO>();
    private static readonly List<Vector3> forces = new List<Vector3>();
    private static readonly List<(long Target, string Method, object[] Args)> messages = new List<(long, string, object[])>();
    private static readonly List<string> notifications = new List<string>();
    private const string QueryRpc = "DNC.OdinShip.FishCapability.v2", CapabilityRpc = "DNC.OdinShip.FishCapabilityResult.v2", FeedRpc = "DNC.OdinShip.PaidFish.v2", ReplyRpc = "DNC.OdinShip.FishResult.v2";
    private static bool dead, playerLoaded;
    private static Ship parentShip = null!;
    private static ZNetView parentView = null!;
    private static WearNTear parentWear = null!;
    private static WearNTear? childWear;
    private static readonly List<string> paintedNames = new List<string>();
    private static readonly HashSet<KeyCode> heldKeys = new HashSet<KeyCode>(), downKeys = new HashSet<KeyCode>();
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static Assembly odin = null!;
    private static ZDOMan man = null!;
    private static Player player = null!;
    private static Transform transform = null!;
    private static int identifier;
    private static Ship.Speed speed;
    private static Vector3 position;
    private static long wardCreator, permittedPlayer;
    private static bool allowInput, text, map, radial, failSend;
    private static Action<long, string, object[]>? onSend;
    private static bool failAfterFeed;
    private static Func<Inventory, Vector2i>? selectSlot;

    internal static void Run(string profile, Action<string, Action> test)
    {
        test("OdinShip absent keeps all optional groups inactive", () =>
        {
            Cleanup(); foreach (Type group in new[] { core, input, fishPatch }) { Invoke(group, "Prepare", new object?[] { null }); Invoke(group, "Verify"); }
            Check(!Ours().Any(), "no optional hooks");
        });
        string? path = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_ODINSHIP_PATH")
            ?? Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "OdinShip.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (path == null) return; // Explicit fixture or actual optional installation; never fetch a vendor build.
        odin = Assembly.LoadFrom(path);
        object previousMan = AccessTools.Field(typeof(ZDOMan), "s_instance").GetValue(null);
        object previousNet = AccessTools.Field(typeof(ZNet), "m_instance").GetValue(null);
        object previousRouted = AccessTools.Field(typeof(ZRoutedRpc), "s_instance").GetValue(null);
        object previousPlayer = AccessTools.Field(typeof(Player), "m_localPlayer").GetValue(null);
        object previousWards = AccessTools.Field(typeof(PrivateArea), "m_allAreas").GetValue(null);
        var fixture = new Harmony(Fixture);
        try
        {
            man = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
            AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, man);
            Peer(200);
            Hook(fixture, typeof(ZNet), "IsServer", nameof(False));
            Hook(fixture, typeof(ZDOMan), "ClientChanged", nameof(Skip));
            Hook(fixture, typeof(ZDOMan), "SetDirtySector", nameof(Skip));
            Hook(fixture, typeof(Object), "get_name", nameof(Name));
            Hook(fixture, typeof(Component), "get_transform", nameof(Transform));
            Hook(fixture, typeof(Component), "get_gameObject", nameof(GameObject));
            Hook(fixture, typeof(Transform), "get_position", nameof(Position));
            Hook(fixture, typeof(Transform), "get_forward", nameof(Forward));
            Hook(fixture, typeof(Ship), "HasPlayerOnboard", nameof(True));
            Hook(fixture, typeof(Ship), "GetSpeedSetting", nameof(Speed));
            Hook(fixture, typeof(Ship), "CustomFixedUpdate", nameof(Skip), new[] { typeof(float) });
            Hook(fixture, typeof(Rigidbody), "AddForceAtPosition", nameof(Force), new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) });
            Hook(fixture, typeof(ZNetView), "InvokeRPC", nameof(Queue), new[] { typeof(long), typeof(string), typeof(object[]) });
            Hook(fixture, typeof(ZNetView), "InvokeRPC", nameof(QueueOwner), new[] { typeof(string), typeof(object[]) });
            Hook(fixture, typeof(Turret), "Awake", nameof(Skip));
            Hook(fixture, odin.GetType("OdinShip.ShipCustomization", true)!, "UpdateNameplates", nameof(Paint));
            fixture.Patch(AccessTools.Method(core, "TurretAwake"), transpiler: new HarmonyMethod(typeof(OdinShipTests), nameof(SceneLookups)));
            Hook(fixture, typeof(Input), "GetKey", nameof(KeyHeld), new[] { typeof(KeyCode) });
            Hook(fixture, typeof(Input), "GetKeyDown", nameof(KeyDown), new[] { typeof(KeyCode) });
            Hook(fixture, typeof(Player), "GetPlayer", nameof(FindPlayer), new[] { typeof(long) });

            Hook(fixture, typeof(Player), "TakeInput", nameof(TakeInput));
            Hook(fixture, typeof(TextInput), "IsVisible", nameof(Text));
            Hook(fixture, typeof(Minimap), "IsOpen", nameof(Map));
            Hook(fixture, typeof(Hud), "InRadial", nameof(Radial));
            Hook(fixture, typeof(PrivateArea), "IsEnabled", nameof(True));
            Hook(fixture, typeof(PrivateArea), "IsInside", nameof(True));
            Hook(fixture, typeof(PrivateArea), "IsPermitted", nameof(Permitted));
            Hook(fixture, typeof(Piece), "GetCreator", nameof(Creator));
            Hook(fixture, typeof(Character), "IsDead", nameof(Dead));
            Hook(fixture, typeof(Player), "IsDead", nameof(Dead));
            Hook(fixture, typeof(Player), "Message", nameof(Message), new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) });
            Hook(fixture, typeof(ZLog), "Log", nameof(Skip));
            Hook(fixture, typeof(Inventory), "FindEmptySlot", nameof(SelectSlot), new[] { typeof(bool) });
            transform = Fake<Transform>("transform");
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, Fake<ZNet>("net"));
            AccessTools.Field(typeof(ZRoutedRpc), "s_instance").SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZRoutedRpc)));
            player = Fake<Player>("player");
            SetView(typeof(Player), player, View(100)); SetView(typeof(Character), player, View(100));
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, player);

            void Case(string name, Action action) => test("OdinShip: " + name, () =>
            {
                Cleanup(); forces.Clear(); messages.Clear(); notifications.Clear(); position = Vector3.zero;
                allowInput = playerLoaded = true; text = map = radial = failSend = dead = false; Peer(200);
                SetView(typeof(Player), player, View(100)); SetView(typeof(Character), player, View(100)); Player.m_localPlayer = player;
                AccessTools.Field(typeof(Player), "m_isLoading").SetValue(player, false);
                paintedNames.Clear(); heldKeys.Clear(); downKeys.Clear(); childWear = null;
                onSend = null; failAfterFeed = false;
                selectSlot = null;
                AccessTools.Field(typeof(PrivateArea), "m_allAreas").SetValue(null, new List<PrivateArea>());
                action(); Cleanup();
            });

            Case("changed vendor build is rejected before any integration hook", () =>
            {
                foreach (Type group in new[] { core, fishPatch })
                {
                    try { Invoke(group, "Prepare", typeof(Ship).Assembly); throw new Exception("guard accepted unrelated build"); }
                    catch (TargetInvocationException error) when (error.InnerException is NotSupportedException) { }
                }
                Check(!Ours().Any(), "nothing installed");
            });
            Case("real canoe handler uses only the owning peer and the supplied timestep", () =>
            {
                Invoke(core, "Prepare", odin); Invoke(core, "Verify");
                MethodInfo handler = AccessTools.Method(odin.GetType("OdinShip.ShipPatches", true), "CustomFixedUpdate");
                new Harmony(Fixture + ".Vendor").Patch(AccessTools.Method(typeof(Ship), "CustomFixedUpdate"), postfix: new HarmonyMethod(handler));
                Ship ship = Fake<Ship>("RowingCanoe(Clone)"); SetView(typeof(Ship), ship, View(300));
                ship.m_backwardForce = 2; ship.m_stearForceOffset = 1;
                AccessTools.Field(typeof(Ship), "m_body").SetValue(ship, Fake<Rigidbody>("body"));
                MethodInfo tick = AccessTools.Method(typeof(Ship), "CustomFixedUpdate");
                speed = Ship.Speed.Full; tick.Invoke(ship, new object[] { 0.25f }); Check(forces.Count == 0, "non-owner applies no extra impulse");
                ViewOf(typeof(Ship), ship).GetZDO().SetOwner(200);
                tick.Invoke(ship, new object[] { 0.25f }); Check(forces.Count == 1 && Math.Abs(forces[0].z - 0.25f) < 0.00001f, "full force uses actual dt");
                speed = Ship.Speed.Half; tick.Invoke(ship, new object[] { 0.5f }); Check(Math.Abs(forces.Last().z - 0.4f) < 0.00001f, "half formula retained");
                speed = Ship.Speed.Stop; tick.Invoke(ship, new object[] { 0.5f }); Check(forces.Count == 2, "stopped ship unchanged");
                Check(AccessTools.Field(core, "frame").GetValue(null) == null, "frame released after postfixes");
            });
            Case("rename retains vendor sanitization and owner write", () =>
            {
                Invoke(core, "Prepare", odin); Invoke(core, "Verify");
                Type type = odin.GetType("OdinShip.ShipCustomization", true)!;
                object customization = FormatterServices.GetUninitializedObject(type);
                ZNetView view = View(200); AccessTools.Field(type, "m_nview").SetValue(customization, view);
                AccessTools.Method(type, "RPC_SetShipName").Invoke(customization, new object[] { 100L, "  " + new string('A', 35) + "  " });
                Check(view.GetZDO().GetString("shipName") == new string('A', 30), "vendor trim and length retained");
                Check(messages.Count == 1 && messages[0].Target == 200 && messages[0].Method == "OnCustomizationChanged", "vendor owner refresh retained");
                view.GetZDO().SetOwner(300); messages.Clear();
                AccessTools.Method(type, "RPC_SetShipName").Invoke(customization, new object[] { 100L, "other" });
                Check(messages.Count == 0 && view.GetZDO().GetString("shipName") == new string('A', 30), "non-owner does not write or broadcast");
            });
            Case("parent destruction refunds once and preserves other destruction callbacks", () =>
            {
                Invoke(core, "Prepare", odin); Invoke(core, "Verify");
                Turret turret = Fake<Turret>("warshipturret"); SetView(typeof(Turret), turret, View(200)); turret.m_returnAmmoOnDestroy = true;
                WearNTear parent = Fake<WearNTear>("parent"); int refunded = 0, other = 0;
                Action callback = () => refunded += 17;
                parent.m_onDestroyed = () => other++;
                Invoke(core, "Subscribe", parent, turret, callback); Invoke(core, "Subscribe", parent, turret, callback);
                parent.m_onDestroyed(); parent.m_onDestroyed();
                Check(refunded == 17 && other == 2, "one counted refund; independent callback retained");
                Turret observer = Fake<Turret>("warshipturret"); SetView(typeof(Turret), observer, View(300)); observer.m_returnAmmoOnDestroy = true;
                WearNTear remote = Fake<WearNTear>("parent"); Invoke(core, "Subscribe", remote, observer, callback); remote.m_onDestroyed();
                Check(refunded == 17, "observer cannot refund");
            });
            Case("turret hierarchy subscription works before or after parent Ship Awake", () =>
            {
                Invoke(core, "Prepare", odin);
                foreach (bool parentReady in new[] { false, true })
                {
                    parentShip = Fake<Ship>("WarShip(Clone)"); parentView = View(200); parentWear = Fake<WearNTear>("parent");
                    SetView(typeof(Ship), parentShip, parentReady ? parentView : null!);
                    Turret turret = Fake<Turret>("warshipturret"); SetView(typeof(Turret), turret, parentView);
                    AccessTools.Method(typeof(Turret), "Awake").Invoke(turret, null);
                    AccessTools.Method(typeof(Turret), "Awake").Invoke(turret, null);
                    if (!parentReady) Check(parentWear.m_onDestroyed == null, "pre-verification Awake only records a candidate");
                    Invoke(core, "Verify");
                    Check(parentWear.m_onDestroyed?.GetInvocationList().Length == 1, "actual postfix subscribes once without Ship private view");
                    Turret foreign = Fake<Turret>("warshipturret"); SetView(typeof(Turret), foreign, View(300));
                    AccessTools.Method(typeof(Turret), "Awake").Invoke(foreign, null);
                    childWear = Fake<WearNTear>("child");
                    Turret independent = Fake<Turret>("warshipturret"); SetView(typeof(Turret), independent, parentView);
                    AccessTools.Method(typeof(Turret), "Awake").Invoke(independent, null);
                    childWear = null;
                    Check(parentWear.m_onDestroyed?.GetInvocationList().Length == 1, "foreign view and child WearNTear rejected");
                }
            });
            Case("failed core IL verification removes only this group", () =>
            {
                MethodInfo handler = AccessTools.Method(odin.GetType("OdinShip.ShipPatches", true), "CustomFixedUpdate");
                new Harmony(Fixture + ".Vendor").Patch(AccessTools.Method(typeof(Ship), "CustomFixedUpdate"), postfix: new HarmonyMethod(handler));
                Invoke(core, "Prepare", odin);
                var code = new[] { new CodeInstruction(OpCodes.Ret) };
                Check(((IEnumerable<CodeInstruction>)Invoke(core, "RewriteCanoe", (object)code)!).Single() == code[0], "unchanged mismatch");
                Invoke(core, "Verify");
                Check(!Ours().Any(), "own group rolled back");
                Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Ship), "CustomFixedUpdate"))!.Postfixes.Any(p => p.owner == Fixture + ".Vendor"), "vendor registration retained");
            });
            Case("installation repeats without duplicating core hooks", () =>
            {
                Invoke(core, "Prepare", odin); Invoke(core, "Verify"); int count = Ours().Count();
                Invoke(core, "Prepare", odin); Invoke(core, "Verify"); Check(Ours().Count() == count && count == 6, "same registrations");
            });
            if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") != "server")
            {
                Case("name observer follows delayed authoritative state in either RPC order", () =>
                {
                    Invoke(core, "Prepare", odin);
                    Type type = odin.GetType("OdinShip.ShipCustomization", true)!;
                    object component = AccessTools.Method(typeof(OdinShipTests), "Fake").MakeGenericMethod(type).Invoke(null, new object[] { "customization" }); ZNetView nview = View(300);
                    AccessTools.Field(type, "m_nview").SetValue(component, nview);
                    AccessTools.Method(type, "Setup").Invoke(component, null);
                    Check(paintedNames.Count == 1 && paintedNames[0] == "", "initial vendor nameplate refresh");
                    Invoke(core, "Verify");
                    var parameters = new ZPackage(); ZRpc.Serialize(Array.Empty<object>(), ref parameters); parameters.SetPos(0);
                    nview.HandleRoutedRPC(new ZRoutedRpc.RoutedRPCData { m_senderPeerID = 300, m_targetPeerID = 200,
                        m_methodHash = "OnCustomizationChanged".GetStableHashCode(), m_parameters = parameters });
                    Check(paintedNames.Last() == "", "early RPC sees old ZDO");
                    nview.GetZDO().Set("shipName", "late name"); NameTick();
                    Check(paintedNames.Last() == "late name", "observer refreshes when authoritative name arrives");
                    int count = paintedNames.Count; NameTick(); Check(paintedNames.Count == count, "unchanged name does not refresh");
                    nview.GetZDO().Set("shipName", "next name"); NameTick();
                    Check(paintedNames.Last() == "next name", "consecutive name update observed without an RPC");
                });
                Case("original vendor keys work while movement and modifier keys are held", () =>
                {
                    Invoke(input, "Prepare", odin); Invoke(input, "Verify");
                    heldKeys.Add(KeyCode.W); heldKeys.Add(KeyCode.LeftShift);
                    foreach (KeyCode key in new[] { KeyCode.G, KeyCode.H, KeyCode.J, KeyCode.L, KeyCode.K, KeyCode.T })
                    {
                        downKeys.Clear(); downKeys.Add(key);
                        Check((bool)Invoke(input, "Pressed", key)!, "original " + key + " works without a modifier requirement");
                        downKeys.Clear(); downKeys.UnionWith(new[] { KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9, KeyCode.Keypad0 });
                        Check(!(bool)Invoke(input, "Pressed", key)!, "keypad alternatives do not replace " + key);
                    }
                });
                Case("input gating preserves all six vendor key constants and original hover hints", () =>
                {
                    Invoke(input, "Prepare", odin); Invoke(input, "Verify");
                    Check((bool)AccessTools.Field(input, "enabled").GetValue(null), "all exact IL anchors verified");
                    foreach (string type in new[] { "ShipCustomizationPatch", "TurretModePatch" })
                    {
                        Type vendor = odin.GetType("OdinShip." + type, true)!;
                        MethodInfo method = AccessTools.Method(vendor, "Player_Update_Postfix");
                        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(method);
                        int[] expected = type == "ShipCustomizationPatch"
                            ? new[] { (int)KeyCode.G, (int)KeyCode.H, (int)KeyCode.J, (int)KeyCode.L, (int)KeyCode.K }
                            : new[] { (int)KeyCode.T };
                        Check(code.Select((c, i) => (c, i)).Where(p => p.c.Calls(AccessTools.Method(input, "Pressed")))
                            .Select(p => Convert.ToInt32(code[p.i - 1].operand)).SequenceEqual(expected), "vendor key constants preserved");
                        MethodInfo hover = AccessTools.Method(vendor, type == "ShipCustomizationPatch"
                            ? "ShipControlls_GetHoverText_Postfix" : "Turret_GetHoverText_Postfix");
                        Check(!(Harmony.GetPatchInfo(hover)?.Transpilers.Any(p => p.owner == "DeepNorthCompat.OdinShip.Input") ?? false), "no DNC hint rewriting");
                        List<CodeInstruction> hints = PatchProcessor.GetCurrentInstructions(hover);
                        foreach (int key in expected)
                            Check(hints.Any(c => c.opcode == OpCodes.Ldstr && ((string)c.operand).Contains("<b>" + (KeyCode)key + "</b>")), "original hover key retained");
                    }
                });
                Case("vendor keypresses are blocked by gameplay and UI input gates", () =>
                {
                    Invoke(input, "Prepare", odin); Invoke(input, "Verify"); downKeys.Add(KeyCode.G);
                    Check((bool)Invoke(input, "Pressed", KeyCode.G)!, "ordinary input allowed");
                    foreach (Action block in new Action[] { () => allowInput = false, () => text = true, () => map = true, () => radial = true })
                    {
                        allowInput = true; text = map = radial = false; block();
                        Check(!(bool)Invoke(input, "Pressed", KeyCode.G)!, "UI focus blocks the actual keypress");
                    }
                    allowInput = true; text = map = radial = false; Player.m_localPlayer = null!;
                    Check(!(bool)Invoke(input, "Pressed", KeyCode.G)!, "missing local player blocks input");
                });
                Case("input IL mismatch rolls back all input hooks", () =>
                {
                    Invoke(input, "Prepare", odin);
                    MethodInfo method = AccessTools.Method(odin.GetType("OdinShip.ShipCustomizationPatch", true), "Player_Update_Postfix");
                    var code = new[] { new CodeInstruction(OpCodes.Ret) };
                    Invoke(input, "GateKeys", code, method); Invoke(input, "Verify");
                    Check(!Ours().Any(), "no partial input gates");
                });
            }
            else Case("input group remains inactive on dedicated servers", () =>
            { Invoke(input, "Prepare", odin); Invoke(input, "Verify"); Check(!Ours().Any(), "no server input hooks"); });

            Case("missing fish hooks roll back only the fish group", () =>
            {
                Invoke(core, "Prepare", odin); Invoke(core, "Verify"); int coreHooks = Ours().Count();
                Invoke(fishPatch, "Prepare", odin);
                new Harmony("DeepNorthCompat.OdinShip.FishPress").UnpatchSelf(); Invoke(fishPatch, "Verify");
                Check(!(bool)AccessTools.Field(fishPatch, "enabled").GetValue(null) && Ours().Count() == coreHooks, "fish inactive; core retained");
            });
            Case("capability precedes payment and only the selected owner can answer", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2);
                ItemDrop.ItemData held = inventory.GetAllItems().Single();
                Check(Feed(press, held) && held.m_stack == 2, "query keeps inventory intact");
                long id = (long)messages.Last().Args[0];
                Check(messages.Last().Method == QueryRpc && messages.Last().Target == 200, "v2 query to owner");
                Deliver(press, 999, 100, CapabilityRpc, id, true);
                Check(held.m_stack == 2 && Payments.Count == 1, "wrong capability sender ignored");
                Deliver(press, 200, 0, CapabilityRpc, id, true);
                Check(held.m_stack == 2, "broadcast capability ignored");
                Deliver(press, 200, 100, CapabilityRpc, id, true);
                Check(held.m_stack == 1 && messages.Last().Method == FeedRpc, "one debit after positive capability");
                var packet = new ZPackage(((ZPackage)messages.Last().Args[0]).GetArray());
                Check(packet.ReadLong() == id && packet.ReadLong() == 700 && packet.ReadString() == "Fish1", "profile and allowed fish only");
                Deliver(press, 999, 100, ReplyRpc, id, false);
                Check(held.m_stack == 1 && Payments.Count == 1, "forged result ignored");
                Deliver(press, 200, 100, ReplyRpc, id, true);
                Check(Payments.Count == 0 && held.m_stack == 1, "accepted payment closed");
            });
            Case("unsupported, absent, and expired capability retain every fish", () =>
            {
                foreach (string mode in new[] { "unsupported", "expired", "owner changed" })
                {
                    object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2);
                    ItemDrop.ItemData held = inventory.GetAllItems().Single(); Feed(press, held);
                    long id = (long)messages.Last().Args[0];
                    if (mode == "expired") { Expire(id); Invoke(fishPatch, "Tick"); }
                    if (mode == "owner changed") PressView(press).GetZDO().SetOwner(300);
                    Deliver(press, 200, 100, CapabilityRpc, id, mode != "unsupported");
                    Check(held.m_stack == 2 && Payments.Count == 0, mode + " cannot debit");
                }
                object noOwner = Press(0); Inventory untouched = InventoryWithFish(2);
                Check(!Feed(noOwner, untouched.GetAllItems().Single()), "zero owner refused");
                Check(notifications.Count > 0, "player receives feedback");
            });
            Case("delayed capability rechecks actor, held item, range, and busy state", () =>
            {
                foreach (string mode in new[] { "item", "range", "dead", "busy", "inventory", "loading" })
                {
                    object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2);
                    ItemDrop.ItemData held = inventory.GetAllItems().Single(); Feed(press, held);
                    long id = (long)messages.Last().Args[0];
                    if (mode == "item") inventory.GetAllItems().Clear();
                    if (mode == "range") position = new Vector3(20, 0, 0);
                    if (mode == "dead") dead = true;
                    if (mode == "loading") AccessTools.Field(typeof(Player), "m_isLoading").SetValue(player, true);
                    if (mode == "busy") PressView(press).GetZDO().Set("FP_IsProcessing".GetStableHashCode(), true);
                    if (mode == "inventory") InventoryWithFish(2);
                    Deliver(press, 200, 100, CapabilityRpc, id, true);
                    Check(held.m_stack == 2 && Payments.Count == 0, mode + " cancels before debit");
                    position = Vector3.zero; dead = false;
                    AccessTools.Field(typeof(Player), "m_isLoading").SetValue(player, false);
                }
            });
            Case("rejection restores original metadata once after owner change", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2);
                ItemDrop.ItemData held = inventory.GetAllItems().Single(); long id = Pay(press, held);
                held.m_customData["tag"] = "changed"; held.m_variant = 9; held.m_crafterID = 99;
                PressView(press).GetZDO().SetOwner(300);
                Deliver(press, 300, 100, ReplyRpc, id, false);
                Check(Payments.Count == 1 && inventory.GetAllItems().Count == 1, "new owner cannot refund old payment");
                Deliver(press, 200, 100, ReplyRpc, id, false);
                Deliver(press, 200, 100, ReplyRpc, id, false);
                ItemDrop.ItemData refund = inventory.GetAllItems().Single(i => i != held);
                Check(Payments.Count == 0 && held.m_stack == 1 && refund.m_stack == 1, "exactly one refund in a separate stack");
                Check(refund.m_customData["tag"] == "original" && refund.m_variant == 2 && refund.m_crafterID == 42
                    && refund.m_worldLevel == 1 && refund.m_quality == 3 && refund.m_durability == 7, "paid metadata preserved");
            });
            Case("full inventory retains a confirmed refund until a slot becomes free", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(1);
                long id = Pay(press, inventory.GetAllItems().Single());
                Fill(inventory);
                Deliver(press, 200, 100, ReplyRpc, id, false);
                Deliver(press, 200, 100, ReplyRpc, id, false);
                Check(Payments.Count == 1 && inventory.GetAllItems().Count == 16, "refund still owed; no occupied slot overwritten");
                inventory.GetAllItems().RemoveAt(0); Invoke(fishPatch, "Tick"); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 0 && inventory.GetAllItems().Count == 16
                    && inventory.GetAllItems().Single(i => i.m_shared.m_name == "Fish1").m_customData["tag"] == "original", "one native-slot restoration");
            });
            Case("refund waits through death and does not credit a different payer", () =>
            {
                object press = Press(200); Peer(100); Inventory original = InventoryWithFish(1);
                long id = Pay(press, original.GetAllItems().Single()); dead = true;
                Deliver(press, 200, 100, ReplyRpc, id, false); Check(Payments.Count == 1, "death retains refund");
                dead = false; ViewOf(typeof(Player), player).GetZDO().Set(ZDOVars.s_playerID, 701L);
                Inventory replacement = InventoryWithFish(0); replacement.GetAllItems().Clear(); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 1 && replacement.GetAllItems().Count == 0, "different character receives nothing");
                ViewOf(typeof(Player), player).GetZDO().Set(ZDOVars.s_playerID, 700L);
                AccessTools.Field(typeof(Player), "m_isLoading").SetValue(player, true); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 1 && replacement.GetAllItems().Count == 0, "profile loading cannot receive a refund that later load would erase");
                AccessTools.Field(typeof(Player), "m_isLoading").SetValue(player, false); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 0 && replacement.GetAllItems().Single().m_stack == 1 && original.GetAllItems().Count == 0, "same payer's active inventory receives refund");
            });
            Case("refund respects the installed slot selector instead of reserved empty cells", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(1);
                long id = Pay(press, inventory.GetAllItems().Single()); Fill(inventory);
                AccessTools.Field(typeof(Inventory), "m_height").SetValue(inventory, 5);
                selectSlot = _ => new Vector2i(-1, -1);
                Deliver(press, 200, 100, ReplyRpc, id, false);
                Check(Payments.Count == 1 && inventory.GetAllItems().Count == 16, "reserved fifth row cannot receive refund");
                selectSlot = _ => throw new IOException("slot selector temporarily unavailable"); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 1 && inventory.GetAllItems().Count == 16, "selector failure retains refund without escaping into gameplay");
                inventory.GetAllItems().RemoveAt(0); selectSlot = _ => new Vector2i(0, 0); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 0 && inventory.GetItemAt(0, 0).m_shared.m_name == "Fish1", "selected normal cell receives refund");
            });
            Case("lost reply and send failure never guess a refund or resend", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2);
                Feed(press, inventory.GetAllItems().Single()); long id = (long)messages.Last().Args[0]; failSend = true;
                Deliver(press, 200, 100, CapabilityRpc, id, true); failSend = false;
                Expire(id); int sends = messages.Count; Invoke(fishPatch, "Tick"); Invoke(fishPatch, "Tick");
                Check(inventory.GetAllItems().Single().m_stack == 1 && Payments.Count == 1 && messages.Count == sends, "uncertain payment retained; no fabricated refund");
                Deliver(press, 200, 100, ReplyRpc, id, false);
                Check(Payments.Count == 0 && inventory.GetAllItems().Single().m_stack == 2, "late rejection returns fish once");
            });
            Case("paid receiver records missing-player rejection without creating fish", () =>
            {
                object press = Press(200); playerLoaded = false;
                Receive(press, 100, Packet(1)); Receive(press, 100, Packet(1));
                Check(QueueSize(press) == 0 && messages.Count == 2 && messages.All(m => m.Method == ReplyRpc && !(bool)m.Args[1]), "one recorded rejection, repeated reply, no queue or world drop");
            });
            Case("synchronous local routing can complete before a send throws", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2); failAfterFeed = true;
                onSend = (destination, method, args) =>
                {
                    long sender = destination == 200 ? 100 : 200; Peer(destination);
                    Deliver(press, sender, destination, method, args);
                };
                Check(Feed(press, inventory.GetAllItems().Single()), "synchronous feed handled");
                Check(QueueSize(press) == 1 && inventory.GetAllItems().Single().m_stack == 1 && Payments.Count == 0,
                    "completed acceptance is not changed into uncertainty or a duplicate refund");
            });
            Case("refund callbacks cannot restore the same fish twice", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2); long id = Pay(press, inventory.GetAllItems().Single());
                bool previous = Player.m_localPlayerExists; Player.m_localPlayerExists = true;
                try
                {
                    inventory.m_onChanged = () => { Invoke(fishPatch, "Tick"); throw new IOException("callback after restored item"); };
                    Deliver(press, 200, 100, ReplyRpc, id, false);
                    Check(Payments.Count == 0 && inventory.GetAllItems().Single().m_stack == 2, "consumed working copy closes refund despite callback exception and reentrant tick");
                }
                finally { Player.m_localPlayerExists = previous; inventory.m_onChanged = null; }
            });
            Case("failed debit callback restores the removed fish without sending payment", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(2);
                inventory.m_onChanged = () => throw new IOException("callback after debit");
                long id = Pay(press, inventory.GetAllItems().Single());
                Check(Payments.Count == 0 && inventory.GetAllItems().Single().m_stack == 2 && messages.All(m => m.Method != FeedRpc), "inventory mutation rolled back before dispatch");
                inventory.m_onChanged = null;
            });
            Case("pending payments are bounded and an ended session cannot credit the next one", () =>
            {
                object press = Press(200); Peer(100); Inventory inventory = InventoryWithFish(50);
                ItemDrop.ItemData second = Item(); second.m_stack = 15; second.m_gridPos = new Vector2i(1, 0); inventory.GetAllItems().Add(second);
                for (int i = 0; i < 64; i++) Pay(press, inventory.GetAllItems()[0]);
                Check(Payments.Count == 64 && !Feed(press, inventory.GetAllItems()[0]) && inventory.GetAllItems().Single().m_stack == 1, "limit precedes debit");
                Peer(300); Invoke(fishPatch, "Tick");
                Check(Payments.Count == 0 && inventory.GetAllItems().Single().m_stack == 1, "no refund across sessions");
            });
            Case("accepted receipt survives player unload and owner change", () =>
            {
                object press = Press(200); Receive(press, 100, Packet(2)); playerLoaded = false;
                PressView(press).GetZDO().SetOwner(300); Receive(press, 100, Packet(2));
                Check(QueueSize(press) == 1 && messages.Count == 2 && messages.All(m => (bool)m.Args[1]), "replay returns prior acceptance");
            });
            Case("receiver denies lost ownership, capacity, processing, identity, range, and ward", () =>
            {
                foreach (string mode in new[] { "owner", "full", "busy", "sender", "range", "ward" })
                {
                    object press = Press(mode == "owner" ? 300 : 200); Peer(200);
                    if (mode == "full") PressView(press).GetZDO().Set("FP_queued".GetStableHashCode(), 10);
                    if (mode == "busy") PressView(press).GetZDO().Set("FP_IsProcessing".GetStableHashCode(), true);
                    if (mode == "range") position = new Vector3(20, 0, 0);
                    if (mode == "ward")
                    {
                        var ward = Fake<PrivateArea>("ward"); AccessTools.Field(typeof(PrivateArea), "m_piece").SetValue(ward, Fake<Piece>("ward piece"));
                        AccessTools.Field(typeof(PrivateArea), "m_allAreas").SetValue(null, new List<PrivateArea> { ward }); wardCreator = 900; permittedPlayer = 0;
                    }
                    int before = QueueSize(press); Receive(press, mode == "sender" ? 999 : 100, Packet(3));
                    Check(QueueSize(press) == before && !(bool)messages.Last().Args[1], mode + " rejected without receiver item creation");
                    position = Vector3.zero; AccessTools.Field(typeof(PrivateArea), "m_allAreas").SetValue(null, new List<PrivateArea>());
                }
            });
            Case("real routed scope restores previous target and ignores broadcasts", () =>
            {
                object press = Press(200); AccessTools.Field(fishPatch, "target").SetValue(null, 777L);
                Deliver(press, 100, 0, FeedRpc, Packet(4)); Check(QueueSize(press) == 0, "broadcast cannot queue");
                Receive(press, 100, Packet(4));
                Check(QueueSize(press) == 1 && (long)AccessTools.Field(fishPatch, "target").GetValue(null) == 777L, "real handler enters and restores target scope");
            });
            Case("registration adds only v2 handlers and leaves legacy automation untouched", () =>
            {
                object press = Press(200); ZNetView nview = PressView(press);
                Invoke(fishPatch, "Register", press); Invoke(fishPatch, "Register", press);
                var functions = (System.Collections.IDictionary)AccessTools.Field(typeof(ZNetView), "m_functions").GetValue(nview);
                Check(functions.Count == 4 && !functions.Contains("DNC.OdinShip.PaidFish.v1".GetStableHashCode())
                    && !functions.Contains("RPC_AddFish".GetStableHashCode()), "no v1 alias or legacy interception");
                Deliver(press, 100, 200, QueryRpc, 55L);
                Check(messages.Last().Method == CapabilityRpc && (bool)messages.Last().Args[1], "verified owner can answer");
            });
            string vcpPath = Path.Combine(profile, "BepInEx/plugins/MidnightMods-ValheimCommunityPatch/ValheimCommunityPatch.dll");
            if (File.Exists(vcpPath) && AssemblyName.GetAssemblyName(vcpPath).Version >= new System.Version(0, 35, 0, 0))
            {
                Assembly vcp = Assembly.LoadFrom(vcpPath);
                Type stationRefund = vcp.GetType("ValheimCommunityPatch.Patches.Correctness.StationRefundPatch", true)!;
                Type reads = vcp.GetType("ValheimCommunityPatch.Patches.Performance.ZdoReadLookupPatch", true)!;
                foreach (bool vcpFirst in new[] { true, false })
                    Case("VCP station RPC tracking and ZDO reads compose with " + (vcpFirst ? "VCP" : "DNC") + " installed first", () =>
                    {
                        var vendor = new Harmony(Fixture + ".Vendor");
                        void InstallVcp()
                        {
                            vendor.Patch(AccessTools.Method(typeof(ZNetView), "HandleRoutedRPC"),
                                prefix: new HarmonyMethod(AccessTools.Method(stationRefund, "HandleRoutedRpcPrefix")),
                                postfix: new HarmonyMethod(AccessTools.Method(stationRefund, "HandleRoutedRpcPostfix")));
                            vendor.CreateClassProcessor(reads).Patch();
                        }
                        FieldInfo route = AccessTools.Field(stationRefund, "_target");
                        object previous = route.GetValue(null);
                        try
                        {
                            if (vcpFirst) InstallVcp();
                            object press = Press(200);
                            if (!vcpFirst) InstallVcp();
                            PressView(press).GetZDO().Set("shipName", "replicated name");
                            Check(PressView(press).GetZDO().GetString("shipName") == "replicated name", "VCP reads preserve authoritative name data");
                            route.SetValue(null, 901L);
                            Peer(100); Inventory inventory = InventoryWithFish(2);
                            long id = Pay(press, inventory.GetAllItems().Single());
                            Check(inventory.GetAllItems().Single().m_stack == 1, "one paid fish through the shared routed handler");
                            Deliver(press, 200, 100, ReplyRpc, id, false);
                            Check(inventory.GetAllItems().Single().m_stack == 2 && Payments.Count == 0, "one inventory refund without a station world drop");
                            Check((long)route.GetValue(null) == 901L && (long)AccessTools.Field(fishPatch, "target").GetValue(null) == 0,
                                "both routed scopes restore their independent previous target");
                        }
                        finally { route.SetValue(null, previous); }
                    });
            }
            string? serverboundPath = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_SERVERBOUND_PATH");
            if (serverboundPath != null)
            {
                Type routing = Assembly.LoadFrom(serverboundPath).GetType("Serverbound.Compatibility.OdinShipOwnershipPatch", true)!;
                foreach (bool routingFirst in new[] { true, false })
                    Case("Serverbound and DNC compose with " + (routingFirst ? "Serverbound" : "DNC") + " installed first", () =>
                    {
                        Invoke(routing, "ResetForTests");
                        try
                        {
                            if (routingFirst) Invoke(routing, "Prepare", odin);
                            Invoke(core, "Prepare", odin); Invoke(input, "Prepare", odin);
                            if (!routingFirst) Invoke(routing, "Prepare", odin);
                            Invoke(routing, "Verify"); Invoke(core, "Verify"); Invoke(input, "Verify");
                            Check((bool)AccessTools.Property(routing, "IsInstalled").GetValue(null, null), "Serverbound verified");
                            Check((bool)AccessTools.Field(core, "enabled").GetValue(null), "DNC core verified");
                            foreach (string type in new[] { "ShipCustomizationPatch", "TurretModePatch" })
                            {
                                MethodInfo method = AccessTools.Method(odin.GetType("OdinShip." + type, true), "Player_Update_Postfix");
                                List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(method);
                                string helper = type == "ShipCustomizationPatch" ? "CanUseCustomizationInputs" : "TryRouteTurretMode";
                                Check(code.Count(c => c.Calls(AccessTools.Method(routing, helper))) == 1, "owner routing remains in effective IL");
                                if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") != "server")
                                    Check(code.Count(c => c.Calls(AccessTools.Method(input, "Pressed"))) == (type == "ShipCustomizationPatch" ? 5 : 1), "input gating remains in effective IL");
                            }
                            MethodInfo rename = AccessTools.Method(odin.GetType("OdinShip.ShipCustomization", true), "RPC_SetShipName");
                            Check(!Harmony.GetPatchInfo(rename)!.Transpilers.Any(p => p.owner == "DeepNorthCompat.OdinShip.Core"), "name observer does not race the ZDO through a broadcast rewrite");
                            Check(Harmony.GetPatchInfo(rename)!.Prefixes.Count(p => p.PatchMethod.DeclaringType == routing) == 1, "Serverbound rename authorization retained");
                        }
                        finally { Invoke(routing, "ResetForTests"); }
                    });
            }
        }
        finally
        {
            Cleanup(); fixture.UnpatchSelf(); new Harmony(Fixture + ".Vendor").UnpatchSelf();
            AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, previousMan);
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, previousNet);
            AccessTools.Field(typeof(ZRoutedRpc), "s_instance").SetValue(null, previousRouted);
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer);
            AccessTools.Field(typeof(PrivateArea), "m_allAreas").SetValue(null, previousWards);
            names.Clear(); zdos.Clear();
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer); pointers.Clear();
        }
    }

    private static object Press(long owner)
    {
        Invoke(fishPatch, "Prepare", odin); Invoke(fishPatch, "Verify");
        Type type = odin.GetType("OdinShip.FishPress", true)!; object press = FormatterServices.GetUninitializedObject(type);
        AccessTools.Field(type, "m_nview").SetValue(press, View(owner));
        AccessTools.Field(type, "m_fishRequired").SetValue(press, 10); AccessTools.Field(type, "m_isInitialized").SetValue(press, true);
        ItemDrop drop = Fake<ItemDrop>("Fish1"); drop.m_itemData = Item();
        AccessTools.Field(type, "m_allowedFish").SetValue(press, new List<ItemDrop> { drop }); Invoke(fishPatch, "Register", press);
        return press;
    }
    private static ItemDrop.ItemData Item() => new ItemDrop.ItemData { m_dropPrefab = Fake<GameObject>("Fish1"),
        m_shared = new ItemDrop.ItemData.SharedData { m_name = "Fish1", m_maxStackSize = 50 }, m_stack = 1,
        m_quality = 3, m_variant = 2, m_worldLevel = 1, m_durability = 7, m_crafterID = 42, m_crafterName = "original",
        m_customData = new Dictionary<string, string> { ["tag"] = "original" } };
    private static Inventory InventoryWithFish(int count)
    {
        var inventory = new Inventory("fish", null, 4, 4); ItemDrop.ItemData item = Item(); item.m_stack = count;
        inventory.GetAllItems().Add(item); AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory); return inventory;
    }
    private static bool Feed(object press, ItemDrop.ItemData item) =>
        (bool)AccessTools.Method(press.GetType(), "OnAddFish").Invoke(press, new object?[] { null, player, item });
    private static System.Collections.IDictionary Payments => (System.Collections.IDictionary)AccessTools.Field(fishPatch, "payments").GetValue(null);
    private static void Expire(long id) => AccessTools.Field(Payments[id]!.GetType(), "Expires").SetValue(Payments[id], -1d);
    private static long Pay(object press, ItemDrop.ItemData item)
    {
        Check(Feed(press, item), "capability query started"); long id = (long)messages.Last().Args[0];
        Deliver(press, 200, 100, CapabilityRpc, id, true); return id;
    }
    private static ZPackage Packet(long id)
    {
        var packet = new ZPackage(); packet.Write(id); packet.Write(700L); packet.Write("Fish1"); return new ZPackage(packet.GetArray());
    }
    private static void Receive(object press, long sender, ZPackage packet) => Deliver(press, sender, 200, FeedRpc, packet);
    private static void Deliver(object press, long sender, long destination, string method, params object[] args)
    {
        var parameters = new ZPackage(); ZRpc.Serialize(args, ref parameters); parameters.SetPos(0);
        PressView(press).HandleRoutedRPC(new ZRoutedRpc.RoutedRPCData { m_senderPeerID = sender, m_targetPeerID = destination,
            m_methodHash = method.GetStableHashCode(), m_parameters = parameters });
    }
    private static void Fill(Inventory inventory)
    {
        for (int i = 0; i < 16; i++)
        {
            ItemDrop.ItemData item = Item(); item.m_shared = new ItemDrop.ItemData.SharedData { m_name = "Filler", m_maxStackSize = 1 };
            item.m_gridPos = new Vector2i(i % 4, i / 4); inventory.GetAllItems().Add(item);
        }
    }
    private static int QueueSize(object press) => (int)AccessTools.Method(press.GetType(), "GetQueueSize").Invoke(press, null);
    private static ZNetView PressView(object press) => (ZNetView)AccessTools.Field(press.GetType(), "m_nview").GetValue(press);
    private static ZNetView ViewOf(Type type, object instance) => (ZNetView)AccessTools.Field(type, "m_nview").GetValue(instance);
    private static void SetView(Type type, object instance, ZNetView view) => AccessTools.Field(type, "m_nview").SetValue(instance, view);
    private static ZNetView View(long owner)
    {
        ZNetView view = Fake<ZNetView>("view"); var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
        zdo.m_uid = new ZDOID(100, (uint)++identifier); zdo.SetOwner(owner); zdo.Set(ZDOVars.s_playerID, 700L); zdos.Add(zdo);
        AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
        FieldInfo functions = AccessTools.Field(typeof(ZNetView), "m_functions"); functions.SetValue(view, Activator.CreateInstance(functions.FieldType)); return view;
    }
    private static T Fake<T>(string name) where T : Object
    {
        T value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt64(pointer, ++identifier);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer); names.Add(value, name); return value;
    }
    private static void Peer(long id)
    { AccessTools.Field(typeof(ZDOMan), "m_sessionID").SetValue(man, id); foreach (ZDO zdo in zdos) zdo.SetOwnerInternal(zdo.GetOwner()); }
    private static IEnumerable<Patch> Ours() => Harmony.GetAllPatchedMethods().SelectMany(m =>
    { Patches p = Harmony.GetPatchInfo(m)!; return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers); })
        .Where(p => p.owner.StartsWith("DeepNorthCompat.OdinShip", StringComparison.Ordinal));
    private static object? Invoke(Type type, string name, params object?[] args) => AccessTools.Method(type, name).Invoke(null, args);
    private static void Hook(Harmony harmony, Type type, string name, string prefix, Type[]? args = null) =>
        harmony.Patch(AccessTools.Method(type, name, args), prefix: new HarmonyMethod(AccessTools.Method(typeof(OdinShipTests), prefix)));
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static void Cleanup()
    {
        foreach (string group in new[] { "Core", "Input", "FishPress" }) new Harmony("DeepNorthCompat.OdinShip." + group).UnpatchSelf();
        new Harmony(Fixture + ".Vendor").UnpatchSelf();
        foreach (Type type in new[] { core, input, fishPatch })
            foreach (string name in new[] { "pending", "enabled" }) AccessTools.Field(type, name)?.SetValue(null, false);
        Invoke(core, "ResetForTests"); Invoke(fishPatch, "ResetForTests");
    }
    private static bool Skip() => false;
    private static bool True(ref bool __result) { __result = true; return false; }
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool Name(Object __instance, ref string __result) { __result = names.TryGetValue(__instance, out string? value) ? value : "fixture"; return false; }
    private static bool Transform(ref Transform __result) { __result = transform; return false; }
    private static bool GameObject(Component __instance, ref GameObject __result) { __result = Fake<GameObject>(names[__instance]); return false; }
    private static bool Position(ref Vector3 __result) { __result = position; return false; }
    private static bool Forward(ref Vector3 __result) { __result = Vector3.forward; return false; }
    private static bool Speed(ref Ship.Speed __result) { __result = speed; return false; }
    private static bool Force(Vector3 __0) { forces.Add(__0); return false; }
    private static bool Queue(long __0, string __1, object[] __2)
    {
        messages.Add((__0, __1, __2));
        onSend?.Invoke(__0, __1, __2);
        if (failAfterFeed && __1 == FeedRpc) throw new IOException("fixture send failed after synchronous acceptance");
        if (failSend) throw new IOException("fixture transport failed after dispatch began");
        return false;
    }
    private static bool QueueOwner(ZNetView __instance, string __0, object[] __1) => Queue(__instance.GetZDO().GetOwner(), __0, __1);
    private static WearNTear SceneWear(Component component) => ReferenceEquals(component, parentShip) ? parentWear : childWear!;
    private static ZNetView SceneView(Component component) => parentView;
    private static Ship SceneShip(Component component) => parentShip;
    private static IEnumerable<CodeInstruction> SceneLookups(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction source in instructions)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && method.IsGenericMethod)
            {
                Type type = method.GetGenericArguments()[0];
                string? helper = method.Name == "GetComponent" && type == typeof(WearNTear) ? nameof(SceneWear)
                    : method.Name == "GetComponent" && type == typeof(ZNetView) ? nameof(SceneView)
                    : method.Name == "GetComponentInParent" && type == typeof(Ship) ? nameof(SceneShip) : null;
                if (helper != null) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(OdinShipTests), helper); }
            }
            yield return code;
        }
    }
    private static bool Paint(object __instance) { paintedNames.Add((string)AccessTools.Method(__instance.GetType(), "GetText").Invoke(__instance, null)); return false; }
    private static void NameTick() { AccessTools.Field(core, "nextNameTick").SetValue(null, 0d); Invoke(core, "Tick"); }
    private static bool KeyHeld(KeyCode __0, ref bool __result) { __result = heldKeys.Contains(__0); return false; }
    private static bool KeyDown(KeyCode __0, ref bool __result) { __result = downKeys.Contains(__0); return false; }
    private static bool SelectSlot(Inventory __instance, ref Vector2i __result)
    {
        if (selectSlot == null) return true;
        __result = selectSlot(__instance); return false;
    }
    private static bool FindPlayer(long __0, ref Player __result) { __result = playerLoaded && __0 == 700 ? player : null!; return false; }
    private static bool TakeInput(ref bool __result) { __result = allowInput; return false; }
    private static bool Text(ref bool __result) { __result = text; return false; }
    private static bool Map(ref bool __result) { __result = map; return false; }
    private static bool Radial(ref bool __result) { __result = radial; return false; }
    private static bool Creator(ref long __result) { __result = wardCreator; return false; }
    private static bool Permitted(long __0, ref bool __result) { __result = permittedPlayer == __0; return false; }
    private static bool Dead(ref bool __result) { __result = dead; return false; }
    private static bool Message(string __1) { notifications.Add(__1); return false; }
}
