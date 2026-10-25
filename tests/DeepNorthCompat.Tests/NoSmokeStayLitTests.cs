using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

// The vendor's mode control, ownership claim and ZDO writes run. Unity component lookup,
// object names, notifications and dirty-sector bookkeeping are fixtures, without transport.
internal static class NoSmokeStayLitTests
{
    private const string Owner = "DeepNorthCompat.NoSmokeStayLit.Controls";
    private const string Fixture = "DeepNorthCompat.Tests.NoSmokeStayLit";
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static ZNetView view = null!;
    private static GameObject gameObject = null!;

    internal static void Run(string profile, Action<string, Action> test)
    {
        if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server") return;

        Assembly vendor = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"),
            "NoSmokeStayLit.dll", SearchOption.AllDirectories).Single());
        Type utils = vendor.GetType("NoSmokeStayLit.TastyUtils", true)!;
        MethodInfo toggle = AccessTools.Method(utils, "ToggleFireplaceMode");
        var fixture = new Harmony(Fixture);
        object previousMan = AccessTools.Field(typeof(ZDOMan), "s_instance").GetValue(null);
        object previousNet = AccessTools.Field(typeof(ZNet), "m_instance").GetValue(null);
        Player previousPlayer = Player.m_localPlayer;
        FieldInfo helper = AccessTools.Field(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField");
        object previousHelper = helper.GetValue(null);

        try
        {
            helper.SetValue(null, Fake<BepInEx.ThreadingHelper>());
            fixture.Patch(AccessTools.Method(typeof(BepInEx.ThreadingHelper), "StartSyncInvoke"), prefix: Hook(nameof(Skip)));
            fixture.Patch(toggle, transpiler: Hook(nameof(ComponentLookup)));
            fixture.Patch(AccessTools.Method(typeof(Component), "get_gameObject"), prefix: Hook(nameof(GameObject)));
            fixture.Patch(AccessTools.Method(typeof(Object), "get_name"), prefix: Hook(nameof(Name)));
            fixture.Patch(AccessTools.Method(utils, "StationName"), prefix: Hook(nameof(Name)));
            fixture.Patch(AccessTools.Method(utils, "Notify"), prefix: Hook(nameof(Skip)));
            fixture.Patch(AccessTools.Method(typeof(ZDOMan), "SetDirtySector"), prefix: Hook(nameof(Skip)));
            fixture.Patch(AccessTools.Method(typeof(ZNet), "IsServer"), prefix: Hook(nameof(Client)));

            var man = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
            AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, man);
            AccessTools.Field(typeof(ZDOMan), "m_sessionID").SetValue(man, 100L);
            AccessTools.Field(typeof(ZDOMan), "m_clientChangeQueue").SetValue(man, new HashSet<ZDOID>());
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, Fake<ZNet>());
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, null);
            var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/nosmoke-unsaved.cfg"), false)
                { SaveOnConfigSet = false };
            AccessTools.Field(vendor.GetType("NoSmokeStayLit.Patches.Configs", true), "fe_piece_groundtorch_timer")
                .SetValue(null, config.Bind("Timed Torches", "Iron Torch On Timer", true));
            AccessTools.Field(vendor.GetType("NoSmokeStayLit.Patches.Configs", true), "fe_custom_instance_timer")
                .SetValue(null, config.Bind("Custom", "Custom Items on Timer", ""));
            gameObject = Fake<GameObject>();
            view = Fake<ZNetView>();
            var fireplace = Fake<Fireplace>();
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = new ZDOID(100, 982001);
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
            AccessTools.Field(typeof(Fireplace), "m_nview").SetValue(fireplace, view);

            test("NoSmokeStayLit cycles a remote-owned fireplace and publishes its mode", () =>
            {
                CompatibilityInstaller.Install(guid => guid == "TastyChickenLegs.NoSmokeStayLit" ? vendor : null,
                    _ => { }, _ => { }, _ => { });
                CompatibilityInstaller.Verify();

                // ON -> TIMER -> OFF -> ON, with ownership lost again before each key press.
                zdo.Set("TT_toggleOn", 1);
                foreach (int expected in new[] { 2, 0, 1 })
                {
                    zdo.SetOwner(200);
                    uint revision = zdo.DataRevision;
                    toggle.Invoke(null, new object[] { fireplace });
                    Check(zdo.GetInt("TT_toggleOn") == expected, "remote-owned fireplace did not cycle to " + expected);
                    Check(zdo.GetOwner() == 100 && zdo.DataRevision > revision
                        && ((HashSet<ZDOID>)AccessTools.Field(typeof(ZDOMan), "m_clientChangeQueue").GetValue(man)).Contains(zdo.m_uid),
                        $"mode write must belong to the client and enter the game's outgoing change queue; owner={zdo.GetOwner()}, revision={zdo.DataRevision}, previous={revision}, server={ZNet.instance.IsServer()}");
                }

                uint ownerRevision = zdo.OwnerRevision;
                toggle.Invoke(null, new object[] { fireplace });
                Check(zdo.GetInt("TT_toggleOn") == 2 && zdo.OwnerRevision == ownerRevision,
                    "an already-owned fireplace cycles once without another ownership change");
            });
        }
        finally
        {
            new Harmony(Fixture).UnpatchSelf();
            new Harmony(Owner).UnpatchSelf();
            AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, previousMan);
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, previousNet);
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, previousPlayer);
            helper.SetValue(null, previousHelper);

            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            pointers.Clear();
        }
    }

    private static IEnumerable<CodeInstruction> ComponentLookup(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.Name == "GetComponent"
                && method.IsGenericMethod && method.GetGenericArguments().Single() == typeof(ZNetView))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NoSmokeStayLitTests), nameof(View));
            }

            yield return instruction;
        }
    }

    private static ZNetView View(Component component) => view;
    private static bool GameObject(ref GameObject __result) { __result = gameObject; return false; }
    private static bool Name(ref string __result) { __result = "piece_groundtorch(Clone)"; return false; }
    private static bool Skip() => false;
    private static bool Client(ref bool __result) { __result = false; return false; }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(NoSmokeStayLitTests), name));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static T Fake<T>() where T : Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
        pointers.Add(pointer);
        Marshal.WriteInt64(pointer, pointers.Count);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer);
        return value;
    }
}
