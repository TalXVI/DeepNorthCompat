using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Security.Cryptography;
using BepInEx.Configuration;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class BetterMapTests
{
    private const string Fixture = "DeepNorthCompat.Tests.BetterMap";
    private static readonly Type[] groups = new[] { "Cartography", "Session", "Persistence", "Portal", "Vehicle" }
        .Select(n => typeof(Plugin).Assembly.GetType("DeepNorthCompat.BetterMap" + n + "Patch", true)!).ToArray();
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly List<string> errors = new List<string>(), warnings = new List<string>();
    private static readonly Dictionary<int, Sprite> sprites = new Dictionary<int, Sprite>();
    private static readonly Dictionary<Texture2D, Color[]> pixels = new Dictionary<Texture2D, Color[]>();
    private static Assembly vendor = null!, cartography = null!;
    private static ConfigFile config = null!;
    private static int sequence;
    private static long world = 42;
    private static long localId = 99;
    private static Vector3 portalPosition;
    private static string portalTag = "Accepted portal";
    private static string snapshot = "";
    private static bool failPlacement;

    private static Type Ours(string name) => typeof(Plugin).Assembly.GetType("DeepNorthCompat.BetterMap" + name + "Patch", true)!;
    private static Type Vendor(string name) => vendor.GetType("BetterMap.Scripts." + name, true)!;
    private static object? Call(Type type, string method, params object?[] arguments) => AccessTools.Method(type, method).Invoke(null, arguments);
    private static void Set(Type type, string field, object? value, object? instance = null) => AccessTools.Field(type, field).SetValue(instance, value);
    private static object Get(Type type, string field, object? instance = null) => AccessTools.Field(type, field).GetValue(instance);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(BetterMapTests), name));

    internal static void Run(string profile, Action<string, Action> test)
    {
        bool server = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_TEST_MODE") == "server";
        int collected = 1, completed = 0;
        var coveredGroups = new Dictionary<string, string>();
        void Coverage(bool present)
        {
            string Hash(Assembly assembly)
            {
                using (var stream = File.OpenRead(assembly.Location))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            string entries = string.Join(",", groups.Select(type =>
            {
                string name = type.Name.Replace("BetterMap", "").Replace("Patch", "");
                string status = coveredGroups.TryGetValue(name, out string value) ? value : "inactive";
                return "\"" + name + "\":\"" + status + "\"";
            }));
            string targets = "\"Valheim\":\"" + Hash(typeof(Minimap).Assembly) + "\"";
            if (present)
            {
                targets += ",\"BetterMap\":\"" + Hash(vendor) + "\"";
                if (!server) targets += ",\"Cartography\":\"" + Hash(cartography) + "\"";
            }
            System.Console.WriteLine("COVERAGE: {\"integration\":\"BetterMap\",\"role\":\"" + (server ? "server" : "client")
                + "\",\"status\":\"" + (!present ? "inactive" : completed == collected ? "complete" : "partial")
                + "\",\"casesCollected\":" + collected + ",\"casesPassed\":" + completed
                + ",\"groups\":{" + entries + "},\"targets\":{" + targets + "}}");
        }
        test("BetterMap absent leaves all five integrations inactive", () =>
        {
            foreach (Type type in groups)
            {
                Call(type, "Prepare", type == Ours("Cartography") ? new object?[] { null, null } : new object?[] { null });
                Call(type, "Verify");
            }
            Check(!Installed().Any(), "absent mod installed hooks");
            completed++;
        });
        string? path = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_BETTERMAP_PATH")
            ?? Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "BetterMap.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (path == null)
        {
            Coverage(false);
            if (Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_ALLOW_INACTIVE_BETTERMAP") != "1")
                throw new Exception("BetterMap coverage is required; set DEEPNORTHCOMPAT_BETTERMAP_PATH or use --allow-inactive-bettermap for absence validation.");
            System.Console.WriteLine("INACTIVE: BetterMap vendor regressions excluded by explicit absence validation.");
            return;
        }
        vendor = Assembly.LoadFrom(path);
        cartography = Assembly.LoadFrom(Path.Combine(profile, "BepInEx/plugins/Advize-CartographySkill/Advize_CartographySkill.dll"));
        void Case(string name, Action action)
        {
            collected++;
            test("BetterMap " + name, () =>
            {
                Setup(profile);
                try { action(); completed++; } finally { Cleanup(); }
            });
        }
        Case("rejects changed vendor builds before installing hooks", () =>
        {
            foreach (Type type in groups)
            {
                // Client-only roles reject installation independently of an optional DLL.
                if (server && type != Ours("Session")) continue;
                try
                {
                    Call(type, "Prepare", type == Ours("Cartography") ? new object?[] { typeof(Plugin).Assembly, cartography } : new object?[] { typeof(Plugin).Assembly });
                    throw new Exception("changed build accepted");
                }
                catch (TargetInvocationException error) { Check(error.InnerException is NotSupportedException, "unexpected rejection"); }
            }
            Check(!Installed().Any(), "rejected build left hooks");
        });
        Case("installs only the audited groups for this role", () =>
        {
            Install();
            foreach (Type type in groups)
                Check((bool)Get(type, "enabled") == (!server || type == Ours("Session")), type.Name + " role gate");
            Check(errors.Count == 0, string.Join("\n", errors));
            foreach (Type type in groups)
                coveredGroups[type.Name.Replace("BetterMap", "").Replace("Patch", "")] =
                    !server || type == Ours("Session") ? "exercised" : "inactive";
        });
        Case("deferred vendor installation and repeated preparation preserve one hook per group", () =>
        {
            new Harmony("xtav1m.BetterMap").UnpatchSelf();
            foreach (Type type in groups)
                Call(type, "Prepare", type == Ours("Cartography") ? new object?[] { vendor, cartography } : new object?[] { vendor });
            new Harmony("xtav1m.BetterMap").PatchAll(vendor);
            foreach (Type type in groups) Call(type, "Verify");
            Check(errors.Count == 0, string.Join("\n", errors)); int count = Installed().Count(); Install();
            Check(Installed().Count() == count, "repeated prepare duplicated hooks");
            if (server) return;
            Minimap map = NewMap(18); Player player = NewPlayer(""); ConfigureSkill(player, 50);
            Set(typeof(Minimap), "m_exploreInterval", 10f, map); Explore(map, player);
            Check(map.m_exploreRadius == 150f, "later vendor registration bypassed Cartography integration");
            Place(1, "$item_mushroom", new Vector3(50, 0, 50)); byte[] data = GetMap(map);
            map = NewMap(19); SetMap(map, data);
            Check((int)Pins(map).Single().m_type == 20, "later vendor registration bypassed persistence integration");
        });
        Case("separates discovery records for two characters in the same world", () =>
        {
            Install(); NewPlayer("42;1;50;50");
            Check(Has(1, new Vector3(50, 0, 50)), "first character record did not load");
            Player second = NewPlayer("");
            Check(!Has(1, new Vector3(50, 0, 50)) && second.m_customData["BetterMap.pinned"] == "", "record leaked into second character");
        });
        Case("flushes discovery before the character snapshot and refuses another character", () =>
        {
            Install(); Player first = NewPlayer(""); AddRecord(1, new Vector3(50, 0, 50));
            var fixture = new Harmony(Fixture);
            fixture.Patch(AccessTools.Method(typeof(PlayerProfile), "SavePlayerData"), prefix: new HarmonyMethod(AccessTools.Method(typeof(BetterMapTests), nameof(Snapshot))) { priority = Priority.Last });
            var profileData = (PlayerProfile)FormatterServices.GetUninitializedObject(typeof(PlayerProfile));
            profileData.SavePlayerData(first);
            Check(snapshot.Contains("42;1;50;50"), "discovery was missing from player snapshot");
            AddRecord(1, new Vector3(70, 0, 70)); Player second = NewPlayer("");
            Call(Vendor("Pins.PinRecord"), "Flush");
            Check(second.m_customData["BetterMap.pinned"] == "", "dirty records copied to another character");
        });
        Case("a failed discovery flush does not abort the character save", () =>
        {
            Install(); Player first = NewPlayer(""); AddRecord(1, new Vector3(50, 0, 50));
            var fixture = new Harmony(Fixture);
            fixture.Patch(AccessTools.Method(Vendor("Pins.PinRecord"), "Flush"), prefix: Hook(nameof(VendorFailure)));
            fixture.Patch(AccessTools.Method(typeof(PlayerProfile), "SavePlayerData"), prefix: new HarmonyMethod(AccessTools.Method(typeof(BetterMapTests), nameof(Snapshot))) { priority = Priority.Last });
            snapshot = "not reached";
            ((PlayerProfile)FormatterServices.GetUninitializedObject(typeof(PlayerProfile))).SavePlayerData(first);
            Check(snapshot == "" && errors.Any(e => e.Contains("not flushed") && e.Contains("fixture vendor failure")), "flush failure stopped or silently skipped the character save");
        });
        Case("cancelled logout keeps state and committed shutdown clears transient state", () =>
        {
            Install(); NewPlayer("42;1;50;50"); Check(Has(1, new Vector3(50, 0, 50)), "initial record");
            var game = Fake<Game>();
            new Harmony(Fixture).Patch(AccessTools.Method(typeof(Game), "Logout"), prefix: Hook(nameof(Skip)));
            game.Logout(false, false);
            Check((bool)Get(Vendor("Pins.PinRecord"), "_loaded"), "cancelled logout reset pin cache");
            // A repeated Logout request must not run BetterMap's unconditional reset.
            Set(Vendor("Vehicles.VehicleRpc"), "_registered", true);
            game.Logout(false, false);
            Check((bool)Get(Vendor("Vehicles.VehicleRpc"), "_registered"), "cancelled logout reset RPC state");
            Set(Ours("Persistence"), "namesMap", Minimap.instance);
            new Harmony(Fixture).Patch(AccessTools.Method(typeof(Game), "Shutdown"), prefix: Hook(nameof(CommitShutdown)));
            AccessTools.Method(typeof(Game), "Shutdown").Invoke(game, new object[] { false });
            Check(!(bool)Get(Vendor("Pins.PinRecord"), "_loaded") && !(bool)Get(Vendor("Vehicles.VehicleRpc"), "_registered"), "committed shutdown did not reset");
            Check(Get(Ours("Persistence"), "namesMap") == null, "DNC retained the old map cache");
            Check(Player.m_localPlayer.m_customData["BetterMap.pinned"] == "42;1;50;50", "saved discovery was cleared");
        });
        Case("clears compressed ZDO keys before a new session rebuilds the table", () =>
        {
            Install(); Minimap map = NewMap(18); ZDOID.Reset(); _ = new ZDOID(77, 1); _ = new ZDOID(88, 2);
            var key = new ZDOID(123, 3);
            IDictionary tracked = (IDictionary)Get(Vendor("Vehicles.VehicleTracker"), "_tracked");
            Type entry = Vendor("Vehicles.VehicleTracker+Tracked");
            Minimap.PinData oldPin = Add(map, 0, "Old vehicle", new Vector3(10, 0, 10), false);
            object value = Activator.CreateInstance(entry, true)!; Set(entry, "Pin", oldPin, value); tracked.Add(key, value);
            _ = new ZRoutedRpc(false);
            _ = new ZDOMan(4);
            Check(!Pins(map).Contains(oldPin), "new session retained the old vehicle pin");
            // The actual reconciliation path hashes old keys if the constructor failed to clear them.
            Call(Vendor("Vehicles.VehicleTracker"), "Reconcile");
            var package = new ZPackage(); package.Write(new ZDOID(77, 4));
            Check(new ZPackage(package.GetArray()).ReadZDOID().UserID == 77, "new session could not serialize a rebuilt ID");
        });
        Case("world changes retain discovery data and a new player object restores vehicle memories", () =>
        {
            Install(); Player first = NewPlayer("42;1;50;50|43;6;60;60");
            Check(Has(1, new Vector3(50, 0, 50)), "first world not loaded"); world = 43;
            Check(!Has(1, new Vector3(50, 0, 50)) && Has(6, new Vector3(60, 0, 60)), "world cache retained old records");
            AddRecord(6, new Vector3(80, 0, 80)); Call(Vendor("Pins.PinRecord"), "Flush");
            Check(first.m_customData["BetterMap.pinned"].Contains("42;1;50;50"), "other world's records discarded");
            first.m_customData["BetterMap.vehicles"] = "43;77;1";
            Call(Vendor("Vehicles.VehicleMemory"), "Used");
            Player next = NewPlayer(""); next.m_customData["BetterMap.vehicles"] = "43;88;2";
            object used = Call(Vendor("Vehicles.VehicleMemory"), "Used")!;
            Check((int)AccessTools.PropertyGetter(used.GetType(), "Count").Invoke(used, null) == 1, "vehicle memories did not reload");
            object owned = ((IEnumerable)used).Cast<object>().Single();
            Check((long)Get(owned.GetType(), "User", owned) == 88L, "previous character's vehicle retained");
        });
        if (server) { Coverage(true); return; }

        Case("missing restore registration rolls back persistence and dependent portal hooks", () =>
        {
            Install(); var target = AccessTools.Method(typeof(Minimap), "SetMapData");
            new Harmony("xtav1m.BetterMap").Unpatch(target, HarmonyPatchType.Postfix, "xtav1m.BetterMap");
            Set(Ours("Persistence"), "pending", true); Call(Ours("Persistence"), "Verify");
            Set(Ours("Portal"), "pending", true); Call(Ours("Portal"), "Verify");
            Check(!(bool)Get(Ours("Persistence"), "enabled") && !(bool)Get(Ours("Portal"), "enabled"), "failed identity adapter retained portal suppression");
            Check(!Installed().Any(p => p.owner == "DeepNorthCompat.Map.Persistence" || p.owner == "DeepNorthCompat.Map.Portals"), "failed groups retained hooks");
            Check((bool)Get(Ours("Session"), "enabled") && (bool)Get(Ours("Vehicle"), "enabled"), "failure disabled unrelated groups");
        });

        Case("applies loaded Cartography levels and live settings through the real exploration wrapper", () =>
        {
            Install(); Minimap map = NewMap(18); Player player = NewPlayer("");
            ConfigureSkill(player, 50);
            AccessTools.Field(typeof(Minimap), "m_exploreInterval").SetValue(map, 10f);
            Explore(map, player); Check(map.m_exploreRadius == 150f, "loaded skill radius overwritten or not initialized");
            ConfigEntry<float> changedBase = config.Bind("Cartography", "Base", 100f); changedBase.Value = 120f;
            SetCartography("baseExploreRadius", changedBase);
            Explore(map, player); Check(map.m_exploreRadius == 170f, "live base radius not applied");
            SetCartography("enableSkill", config.Bind("Cartography", "Disabled", false));
            Explore(map, player); Check(map.m_exploreRadius == 100f, "disabled skill suppressed BetterMap setting");
            Player.m_localPlayer = null!; Explore(map, player); Check(map.m_exploreRadius == 100f, "missing local player changed vendor radius");
        });
        Case("round-trips every legend category and preserves automatic identity after slot reassignment", () =>
        {
            Install(); Minimap old = NewMap(19); NewPlayer("");
            SetFog(old, new[] { false, true, false, true }, new[] { true, false, false, true });
            for (int category = 0; category <= 8; category++)
            {
                Minimap.PinData pin = Place(category, "$fixture_" + category, new Vector3(category * 10, 0, 0));
                pin.m_checked = true; if (category % 2 == 0) pin.m_ownerID = 71;
            }
            byte[] saved = GetMap(old); Minimap current = NewMap(18); NewPlayer(""); SetMap(current, saved);
            Check(Pins(current).Count == 9, "pin count changed");
            for (int i = 0; i < 9; i++)
            {
                Minimap.PinData pin = Pins(current)[i];
                Check((int)pin.m_type == 18 + i && ReferenceEquals(pin.m_icon, sprites[i]) && pin.m_checked && pin.m_ownerID == (i % 2 == 0 ? 71 : 0) && pin.m_pos.x == i * 10, "category or native metadata changed");
                Check(Has(i, pin.m_pos) == (i % 2 == 1), "discovery records must return for the character's own automatic pins only");
            }
            var outer = new ZPackage(saved); Check(outer.ReadInt() == 8, "native map version changed");
            CheckFog(GetMap(current), new[] { false, true, false, true }, new[] { true, false, false, true });
        });
        Case("cartography import remaps categories without unhiding filters or changing shared ownership", () =>
        {
            Install(); Minimap map = NewMap(18); Player player = NewPlayer("");
            player.m_customData["BetterMap.hidden"] = "BetterMap.Forage";
            Set(Vendor("Pins.PinFilters"), "_restoredOnto", map);
            Visible(map)[19] = false;
            byte[] shared = Metadata(Shared(20, "$item_mushroom", new Vector3(12, 0, 23), 71, true), new[] { (1, true) });
            Check(map.AddSharedMapData(shared), "map import returned false");
            Minimap.PinData pin = Pins(map).Single();
            Check((int)pin.m_type == 19 && ReferenceEquals(pin.m_icon, sprites[1]) && pin.m_ownerID == 71 && pin.m_checked, "shared category/owner/check changed");
            Check(!Visible(map)[19] && player.m_customData["BetterMap.hidden"] == "BetterMap.Forage", "import erased hidden filter");
        });
        Case("native shared writer carries categories, automatic portals and author IDs through a table", () =>
        {
            Install(); Minimap map = NewMap(19); NewPlayer("");
            Minimap.PinData forage = Place(1, "$item_mushroom", new Vector3(12, 0, 23)); forage.m_checked = true;
            forage.m_author = new Splatform.PlatformUserID("Steam", "76561198000000001");
            portalPosition = new Vector3(50, 0, 50); portalTag = "Old"; Portal();
            Add(map, (int)Minimap.PinType.Death, "Death", new Vector3(80, 0, 80));
            SetFog(map, new[] { false, true, false, false }, new[] { true, false, false, false });
            var previous = new ZPackage(); previous.Write(3); previous.Write(4);
            foreach (bool cell in new[] { false, false, true, false }) previous.Write(cell);
            previous.Write(0);
            byte[] shared = map.GetSharedMapData(previous.GetArray());
            localId = 100; NewPlayer(""); map = NewMap(18); map.AddSharedMapData(shared);
            Minimap.PinData imported = Pins(map).Single(p => p.m_name == "$item_mushroom");
            Check(Pins(map).Count == 2 && (int)imported.m_type == 19 && imported.m_checked && imported.m_ownerID == 99
                && imported.m_author.ToString() == forage.m_author.ToString(), "native table writer lost identity, author or owner");
            Check(!Has(1, imported.m_pos), "imported shared pin wrote a discovery record");
            CheckFog(GetMap(map), new[] { false, false, false, false }, new[] { true, true, true, false });
            portalTag = "Remote accepted"; Portal();
            Check(Pins(map).Single(p => p.m_pos == portalPosition).m_name == portalTag, "native shared writer lost automatic portal identity");
        });
        Case("identical local pins retain independent automatic and manual identities", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Same";
            Portal(); Add(map, (int)Minimap.PinType.Icon4, "Same", portalPosition);
            byte[] local = GetMap(map); map = NewMap(18); SetMap(map, local);
            portalTag = "Accepted"; Portal();
            Check(Pins(map).Count == 2 && Pins(map)[0].m_name == "Accepted" && Pins(map)[1].m_name == "Same", "duplicate local rows lost automatic identity");
        });
        Case("foreign metadata stays foreign through later saves even in a BetterMap slot", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer("");
            map.AddSharedMapData(Metadata(Shared(19, "Foreign", new Vector3(12, 0, 23), 71, true), new[] { (-1, false) }));
            byte[] local = GetMap(map); map = NewMap(19); SetMap(map, local); Call(Ours("Persistence"), "Tick");
            Check((int)Pins(map).Single().m_type == 19, "foreign pin was reclassified on its second save");
        });
        Case("failed native loading restores filter scope", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); byte[] data = GetMap(map);
            Set(typeof(Minimap), "m_textureSize", 3, map);
            try { SetMap(map, data); throw new Exception("invalid map size accepted"); }
            catch (TargetInvocationException error) { Check(error.InnerException!.Message.Contains("minimap mismatch"), "wrong load failure"); }
            Set(typeof(Minimap), "m_textureSize", 2, map); Visible(map)[19] = false;
            Add(map, 19, "Manual", Vector3.zero);
            Check(Visible(map)[19], "manual AddPin inherited the failed import scope");
        });
        Case("failed automatic portal placement leaves later manual pins manual", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10);
            new Harmony(Fixture).Patch(AccessTools.Method(typeof(Minimap), "AddPin"), prefix: Hook(nameof(PlacementFailure)));
            failPlacement = true;
            try { Portal(); throw new Exception("placement failure was not exercised"); }
            catch (TargetInvocationException error)
            { Check(error.InnerException is InvalidOperationException && error.InnerException.Message == "fixture placement failure", "wrong placement failure"); }
            finally { failPlacement = false; }
            Minimap.PinData manual = Add(map, (int)Minimap.PinType.Icon4, "Manual", portalPosition);
            portalTag = "Accepted after failure"; Portal();
            Check(manual.m_name == "Manual" && Pins(map).Count == 2, "failed placement classified a later manual pin as automatic");
            byte[] saved = GetMap(map); map = NewMap(19); SetMap(map, saved);
            portalTag = "Accepted after reload"; Portal();
            Check(Pins(map).Count == 2 && Pins(map).Count(p => p.m_name == "Manual") == 1
                && Pins(map).Count(p => p.m_name == portalTag) == 1, "failed placement leaked manual identity through a save");
        });
        Case("readers without the repair ignore its trailer and retain native pin fields", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer("");
            Minimap.PinData pin = Add(map, 2, "Manual", new Vector3(12, 0, 23)); pin.m_checked = true; pin.m_ownerID = 71;
            pin.m_author = new Splatform.PlatformUserID("Steam", "76561198000000001");
            byte[] saved = GetMap(map); Set(Ours("Persistence"), "enabled", false);
            map = NewMap(18); SetMap(map, saved);
            Minimap.PinData restored = Pins(map).Single();
            Check(restored.m_type == pin.m_type && restored.m_name == pin.m_name && restored.m_pos == pin.m_pos
                && restored.m_checked == pin.m_checked && restored.m_ownerID == pin.m_ownerID && restored.m_author == pin.m_author, "native reader did not ignore trailer");
        });
        Case("keeps foreign pins and rejects malformed category trailers without changing native pins", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer("");
            byte[] shared = Metadata(Shared(2, "Manual foreign pin", new Vector3(12, 0, 23), 71, true), new[] { (-1, false) });
            map.AddSharedMapData(shared);
            Check(Pins(map).Single().m_type == Minimap.PinType.Icon2, "foreign pin reclassified");
            map = NewMap(18);
            byte[] bad = Metadata(Shared(19, "Manual foreign pin", new Vector3(12, 0, 23), 71, true), new[] { (1, true) });
            bad[bad.Length - 14] = 99; // Invalid category in the versioned trailer.
            map.AddSharedMapData(bad);
            Check((int)Pins(map).Single().m_type == 19 && warnings.Any(w => w.Contains("rejected")), "bad metadata silently reclassified a pin");
            byte[] saved = GetMap(map); map = NewMap(19); SetMap(map, saved);
            Check((int)Pins(map).Single().m_type == 19, "later save guessed a category after rejecting metadata");
        });
        Case("recovers legacy collapsed types only from unique records and labels after player loading", () =>
        {
            Install(); Minimap old = NewMap(93); NewPlayer("");
            Minimap.PinData pin = Add(old, 99, "$piece_beehive", new Vector3(50, 0, 50));
            Set(Ours("Persistence"), "enabled", false); byte[] native = GetMap(old); Set(Ours("Persistence"), "enabled", true);
            Minimap current = NewMap(18); Player.m_localPlayer = null!; SetMap(current, native);
            Check(Pins(current).Single().m_type == Minimap.PinType.Icon3, "legacy fixture did not collapse");
            NewPlayer("42;6;50;50"); Call(Ours("Persistence"), "Tick");
            Check((int)Pins(current).Single().m_type == 24 && ReferenceEquals(Pins(current).Single().m_icon, sprites[6]), "legacy type/icon not recovered together");
            current = NewMap(18); NewPlayer("42;6;50;50|42;7;50;50"); SetMap(current, native);
            Call(Ours("Persistence"), "Tick");
            Check(Pins(current).Single().m_type == Minimap.PinType.Icon3, "ambiguous legacy record was guessed");
        });
        Case("unresolved legacy recovery survives an intervening save", () =>
        {
            Install(); Minimap old = NewMap(93); NewPlayer("");
            Add(old, 99, "$piece_beehive", new Vector3(50, 0, 50));
            Set(Ours("Persistence"), "enabled", false); byte[] native = GetMap(old); Set(Ours("Persistence"), "enabled", true);
            Minimap current = NewMap(18); Player.m_localPlayer = null!; SetMap(current, native);
            byte[] pending = GetMap(current);
            current = NewMap(18); NewPlayer("42;6;50;50"); SetMap(current, pending); Call(Ours("Persistence"), "Tick");
            Check((int)Pins(current).Single().m_type == 24 && ReferenceEquals(Pins(current).Single().m_icon, sprites[6]), "saving before player data arrived permanently lost legacy recovery");
        });
        Case("optional boss altar pins remain saveable without changing their vanilla icon", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer("");
            Minimap.PinData boss = Place(9, "Altar of Eikthyr", new Vector3(50, 0, 50));
            Check(boss.m_type == Minimap.PinType.Icon3 && ReferenceEquals(boss.m_icon, sprites[0]), "boss fixture did not use the upstream fallback icon");
            byte[] saved = GetMap(map); map = NewMap(19); SetMap(map, saved);
            Check(Pins(map).Single().m_type == Minimap.PinType.Icon3 && Pins(map).Single().m_name == "Altar of Eikthyr", "boss category became a legend category");
            byte[] shared = map.GetSharedMapData(null); localId = 100; NewPlayer(""); map = NewMap(18); map.AddSharedMapData(shared);
            Check(Pins(map).Count == 1 && !Has(9, new Vector3(50, 0, 50)), "shared boss pin wrote a discovery record");
            Place(9, "Altar of Eikthyr", new Vector3(50, 0, 50)); Check(Pins(map).Count == 1, "shared boss pin lost its automatic identity");
        });
        Case("shared deduplication retains the first row's checked state, author and automatic identity", () => Deduplicate(false, "Steam_76561198000000002"));
        Case("shared deduplication distinguishes equal checks by author", () => Deduplicate(true, "Steam_76561198000000002"));
        Case("shared deduplication distinguishes equal authors by checked state", () => Deduplicate(false, "Steam_76561198000000001"));
        Case("category recovery waits for a later legend allocation", () =>
        {
            Install(); Minimap map = NewMap(19); NewPlayer(""); Place(1, "$item_mushroom", new Vector3(50, 0, 50));
            byte[] data = GetMap(map); map = NewMap(18);
            IDictionary types = (IDictionary)Get(Vendor("Pins.PinLegend"), "_types"), categories = (IDictionary)Get(Vendor("Pins.PinLegend"), "_categories");
            types.Clear(); categories.Clear(); SetMap(map, data);
            for (int i = 0; i < 9; i++)
            {
                object category = Enum.ToObject(Vendor("Pins.PinCategory"), i); var type = (Minimap.PinType)(18 + i);
                types.Add(category, type); categories.Add(type, category);
            }
            Call(Ours("Persistence"), "Tick");
            Check((int)Pins(map).Single().m_type == 19 && ReferenceEquals(Pins(map).Single().m_icon, sprites[1]), "a fallback slot was permanently cached before the legend was ready");
        });
        Case("destroying a portal removes its automatic pin and retains an overlapping manual pin", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Portal";
            Portal(); Minimap.PinData automatic = Pins(map).Single();
            Minimap.PinData manual = Add(map, (int)automatic.m_type, "Manual", portalPosition);
            Set(Vendor("Pins.PortalPins"), "_prefabs", new HashSet<int> { 123 });
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); Set(typeof(ZDO), "m_prefab", 123, zdo);
            Check(zdo.GetPrefab() == 123 && Has(10, portalPosition), "portal destruction fixture was not registered");
            Call(Vendor("Pins.PortalPins"), "Destroyed", zdo);
            Check(Pins(map).Count == 1 && ReferenceEquals(Pins(map)[0], manual), "portal destruction retained: " + string.Join(",", Pins(map).Select(p => p.m_name)));
            Check(!Has(10, portalPosition), "destroyed portal retained its discovery record");
        });
        Case("disabled portal group retains the vendor's destruction selection", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Portal";
            Portal(); Minimap.PinData automatic = Pins(map).Single();
            Add(map, (int)automatic.m_type, "Manual", portalPosition);
            Set(Vendor("Pins.PortalPins"), "_prefabs", new HashSet<int> { 123 });
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); Set(typeof(ZDO), "m_prefab", 123, zdo);
            Set(Ours("Portal"), "enabled", false); Call(Vendor("Pins.PortalPins"), "Destroyed", zdo);
            Check(Pins(map).Count == 1 && ReferenceEquals(Pins(map)[0], automatic), "vendor baseline no longer removes the overlapping manual pin");
        });
        Case("version-one trailers load and upgrade without changing foreign classifications", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer("");
            var trailer = new ZPackage(); trailer.Write(1); trailer.Write(1); trailer.Write(1); trailer.Write(true);
            var footer = new ZPackage(); footer.Write(trailer.Size()); footer.Write(0x444E4D50);
            byte[] old = Shared(20, "$item_mushroom", new Vector3(12, 0, 23), 71, true).Concat(trailer.GetArray()).Concat(footer.GetArray()).ToArray();
            map.AddSharedMapData(old);
            Check((int)Pins(map).Single().m_type == 19 && !Has(1, new Vector3(12, 0, 23)), "version-one category lost or shared pin wrote a discovery record");
            Place(1, "$item_mushroom", new Vector3(12, 0, 23)); Check(Pins(map).Count == 1, "version-one automatic identity lost");
            byte[] upgraded = GetMap(map); int length = BitConverter.ToInt32(upgraded, upgraded.Length - 8);
            Check(BitConverter.ToInt32(upgraded, upgraded.Length - 8 - length) == 2, "metadata writer did not upgrade version one");
            map = NewMap(18); trailer = new ZPackage(); trailer.Write(1); trailer.Write(1); trailer.Write(-1); trailer.Write(false);
            footer = new ZPackage(); footer.Write(trailer.Size()); footer.Write(0x444E4D50);
            old = Shared(19, "$piece_beehive", new Vector3(50, 0, 50), 71, true).Concat(trailer.GetArray()).Concat(footer.GetArray()).ToArray();
            NewPlayer("42;6;50;50"); map.AddSharedMapData(old); Call(Ours("Persistence"), "Tick");
            Check((int)Pins(map).Single().m_type == 19, "version-one foreign identity was treated as unresolved legacy data");
        });
        Case("failed portal destruction IL rolls back the portal group alone", () =>
        {
            Install(); Type portal = Ours("Portal");
            Call(portal, "DestructionSelection", (object)new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) });
            Set(portal, "pending", true); Call(portal, "Verify");
            Check(!(bool)Get(portal, "enabled") && !Installed().Any(p => p.owner == "DeepNorthCompat.Map.Portals"), "portal group retained suppression after an IL mismatch");
            Check((bool)Get(Ours("Persistence"), "enabled") && (bool)Get(Ours("Vehicle"), "enabled"), "portal failure disabled another group");
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ZNetScene), "OnZDODestroyed"))!.Prefixes.Any(p => p.owner == "xtav1m.BetterMap"), "portal rollback removed the vendor's destruction hook");
        });
        Case("portal updates wait for accepted tags and preserve checked state, author, ownership and manual pins", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer("");
            portalPosition = new Vector3(10, 0, 10); portalTag = "Old tag";
            Portal(); Minimap.PinData automatic = Pins(map).Single(); automatic.m_checked = true; automatic.m_ownerID = 71;
            automatic.m_author = new Splatform.PlatformUserID("Steam", "76561198000000001");
            object author = AccessTools.Field(typeof(Minimap.PinData), "m_author").GetValue(automatic);
            Minimap.PinData manual = Add(map, (int)Minimap.PinType.Icon4, "Manual", portalPosition);
            Call(Vendor("Pins.NamedPins"), "Retag", portalPosition, "Unaccepted request");
            Check(automatic.m_name == "Old tag" && manual.m_name == "Manual", "request renamed existing pins");
            portalTag = "Accepted by another player"; Portal();
            Check(automatic.m_name == portalTag && automatic.m_checked && automatic.m_ownerID == 71 && Equals(author, AccessTools.Field(typeof(Minimap.PinData), "m_author").GetValue(automatic)), "portal metadata or label lost");
            Check(Pins(map).Count == 2 && manual.m_name == "Manual", "manual pin was replaced or renamed");
            map.RemovePin(automatic); Portal(); Check(Pins(map).Count == 1 && ReferenceEquals(Pins(map)[0], manual), "deliberate deletion was recreated");
        });
        Case("portal identity survives save/reload and cartography imports", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Old tag";
            Portal(); byte[] local = GetMap(map); map = NewMap(18); SetMap(map, local);
            portalTag = "New tag"; Portal(); Check(Pins(map).Single().m_name == "New tag", "local portal identity lost");
            map = NewMap(18);
            map.AddSharedMapData(Metadata(Shared((int)Minimap.PinType.Icon4, "Old shared tag", portalPosition, 71, true), new[] { (10, true) }));
            Portal(); Check(Pins(map).Single().m_name == "New tag" && Pins(map).Single().m_ownerID == 71, "shared automatic portal not updated in place");
        });
        Case("a table read that drops a shared automatic portal pin lets the portal be pinned again", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Shared";
            map.AddSharedMapData(Metadata(Shared((int)Minimap.PinType.Icon4, "Shared", portalPosition, 71, true), new[] { (10, true) }));
            Portal(); Check(Pins(map).Count == 1 && Pins(map)[0].m_ownerID == 71, "shared automatic portal gained a second pin");
            var later = new ZPackage(); later.Write(3); later.Write(4); for (int i = 0; i < 4; i++) later.Write(false);
            later.Write(0); map.AddSharedMapData(later.GetArray());
            Check(Pins(map).Count == 0, "table read fixture retained the shared pin");
            Portal();
            Check(Pins(map).Count == 1 && Pins(map)[0].m_ownerID == 0 && Has(10, portalPosition), "dropped shared portal was not pinned again as the reader's own");
        });
        Case("automatic placement respects pins already on the map", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); var spot = new Vector3(12, 0, 23);
            map.AddSharedMapData(Metadata(Shared(19, "$item_mushroom", spot, 71, true), new[] { (1, true) }));
            Place(1, "$item_mushroom", spot + new Vector3(3, 0, 0));
            Check(Pins(map).Count == 1 && !Has(1, spot), "shared automatic pin gained a second pin or a discovery record");
            Place(2, "$item_other", spot);
            Check(Pins(map).Count == 2, "a pin of another category suppressed placement");
            var manual = new Vector3(100, 0, 100); Add(map, 19, "Manual", manual); Place(1, "$item_mushroom", manual);
            Check(Pins(map).Count == 4, "a manual pin suppressed automatic placement");
            var own = new Vector3(200, 0, 200); Place(1, "$item_mushroom", own);
            Call(Vendor("Pins.PinRecord"), "Wipe"); Check(!Has(1, own), "forget fixture retained the record");
            Place(1, "$item_mushroom", own);
            Check(Pins(map).Count == 5 && Has(1, own), "own automatic pin without a record stacked or stayed unrecorded");
        });
        Case("removing a shared automatic pin by hand keeps its place from being pinned again", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Shared";
            map.AddSharedMapData(Metadata(Shared((int)Minimap.PinType.Icon4, "Shared", portalPosition, 71, true), new[] { (10, true) }));
            // Unity UI visibility is the boundary. The fixture names the pin under the cursor.
            new Harmony(Fixture).Patch(AccessTools.Method(typeof(Minimap), "GetClosestPin"), prefix: Hook(nameof(ClosestPin)));
            Check(map.RemovePin(portalPosition, 1f) && Pins(map).Count == 0, "by-hand removal fixture did not remove the pin");
            Portal(); Check(Pins(map).Count == 0, "pin removed by hand came straight back");
        });
        Case("destroying a portal removes a shared automatic pin that has no discovery record", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10);
            map.AddSharedMapData(Metadata(Shared((int)Minimap.PinType.Icon4, "Shared", portalPosition, 71, true), new[] { (10, true) }));
            Set(Vendor("Pins.PortalPins"), "_prefabs", new HashSet<int> { 123 });
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); Set(typeof(ZDO), "m_prefab", 123, zdo);
            Call(Vendor("Pins.PortalPins"), "Destroyed", zdo);
            Check(Pins(map).Count == 0 && !Has(10, portalPosition), "destroyed portal left its shared pin or a discovery record");
        });
        Case("a vendor failure while writing categories keeps the native map save", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); Add(map, 2, "Manual", new Vector3(12, 0, 23));
            new Harmony(Fixture).Patch(AccessTools.Method(Vendor("Pins.PinLegend"), "CategoryOf"), prefix: Hook(nameof(VendorFailure)));
            byte[] saved = GetMap(map);
            Check(BitConverter.ToInt32(saved, saved.Length - 4) != 0x444E4D50 && errors.Any(e => e.Contains("trailer not written") && e.Contains("fixture vendor failure")), "save failure was not reported or left a trailer");
            map = NewMap(18); SetMap(map, saved);
            Check(Pins(map).Single().m_name == "Manual" && Pins(map).Single().m_type == Minimap.PinType.Icon2, "native save lost its pin");
        });
        Case("a vendor failure while reading categories leaves the native map load intact", () =>
        {
            Install(); Minimap map = NewMap(19); NewPlayer(""); Place(1, "$item_mushroom", new Vector3(12, 0, 23)); byte[] saved = GetMap(map);
            map = NewMap(18);
            new Harmony(Fixture).Patch(AccessTools.Method(Vendor("Pins.PinLegend"), "HasType"), prefix: Hook(nameof(VendorFailure)));
            SetMap(map, saved);
            Check(Pins(map).Single().m_name == "$item_mushroom" && warnings.Any(w => w.Contains("map data not read") && w.Contains("fixture vendor failure")), "read failure stopped the native load or was not reported");
        });
        Case("a vendor failure after the native load leaves the loaded pins in place", () =>
        {
            Install(); Minimap map = NewMap(18); NewPlayer(""); Place(1, "$item_mushroom", new Vector3(12, 0, 23)); byte[] saved = GetMap(map);
            map = NewMap(18);
            new Harmony(Fixture).Patch(AccessTools.Method(Vendor("Pins.PinFilters"), "Refresh"), prefix: Hook(nameof(VendorFailure)));
            SetMap(map, saved);
            Check((int)Pins(map).Single().m_type == 19 && errors.Any(e => e.Contains("not fully classified") && e.Contains("fixture vendor failure")), "post-load failure escaped or was not reported");
        });
        Case("a trailer from a newer metadata version falls back to legacy recovery", () =>
        {
            Install(); Minimap old = NewMap(93); NewPlayer("");
            Add(old, 99, "$piece_beehive", new Vector3(50, 0, 50));
            Set(Ours("Persistence"), "enabled", false); byte[] native = GetMap(old); Set(Ours("Persistence"), "enabled", true);
            var trailer = new ZPackage(); trailer.Write(3); trailer.Write(1); trailer.Write(6); trailer.Write(false); trailer.Write(false);
            var footer = new ZPackage(); footer.Write(trailer.Size()); footer.Write(0x444E4D50);
            byte[] future = native.Concat(trailer.GetArray()).Concat(footer.GetArray()).ToArray();
            Minimap current = NewMap(18); NewPlayer("42;6;50;50"); SetMap(current, future); Call(Ours("Persistence"), "Tick");
            Check(warnings.Any(w => w.Contains("rejected")), "unsupported trailer version was accepted");
            Check((int)Pins(current).Single().m_type == 24 && ReferenceEquals(Pins(current).Single().m_icon, sprites[6]), "unreadable trailer blocked legacy recovery");
        });
        Case("shared deduplication keeps the first of two identical rows", () => Deduplicate(true, "Steam_76561198000000001"));
        Case("legacy recovery runs when discovery records arrive after the pins settled", () =>
        {
            Install(); Minimap old = NewMap(93); NewPlayer("");
            Add(old, 99, "$piece_beehive", new Vector3(50, 0, 50));
            Set(Ours("Persistence"), "enabled", false); byte[] native = GetMap(old); Set(Ours("Persistence"), "enabled", true);
            Minimap current = NewMap(18); Player player = NewPlayer(""); SetMap(current, native); Call(Ours("Persistence"), "Tick");
            Check(Pins(current).Single().m_type == Minimap.PinType.Icon3, "legacy fixture did not collapse");
            player.m_customData["BetterMap.pinned"] = "42;6;50;50"; Call(Ours("Persistence"), "Tick");
            Check((int)Pins(current).Single().m_type == 24 && ReferenceEquals(Pins(current).Single().m_icon, sprites[6]), "records arriving on the same character were ignored");
        });
        Case("Cartography radius follows a skill gain and corrects an outside overwrite", () =>
        {
            Install(); Minimap map = NewMap(18); Player player = NewPlayer(""); ConfigureSkill(player, 50);
            Set(typeof(Minimap), "m_exploreInterval", 10f, map);
            Explore(map, player); Check(map.m_exploreRadius == 150f, "loaded skill radius not applied");
            map.m_exploreRadius = 100f; Explore(map, player);
            Check(map.m_exploreRadius == 150f, "an overwritten radius was not corrected");
            var levels = (Dictionary<Skills.SkillType, Skills.Skill>)Get(typeof(Skills), "m_skillData", Get(typeof(Player), "m_skills", player));
            levels[(Skills.SkillType)1337].m_level = 75f; Explore(map, player);
            Check(map.m_exploreRadius == 175f, "a skill gain did not change the radius");
        });
        Case("remote vehicle movement and heading-only changes request redraws", () =>
        {
            Install(); Minimap map = NewMap(18); ZDOID.Reset();
            Type tracker = Vendor("Vehicles.VehicleTracker"), trackedType = Vendor("Vehicles.VehicleTracker+Tracked"), vehicleType = Vendor("Vehicles.VehicleTracker+Vehicle");
            object tracked = Activator.CreateInstance(trackedType, true)!; var pin = new Minimap.PinData { m_pos = new Vector3(10, 0, 10) };
            Set(trackedType, "Pin", pin, tracked); ((IDictionary)Get(tracker, "_tracked")).Add(new ZDOID(77, 1), tracked);
            object wanted = Activator.CreateInstance(vehicleType)!; Set(vehicleType, "Pos", new Vector3(20, 0, 20), wanted); Set(vehicleType, "Heading", 90f, wanted);
            ((IDictionary)Get(tracker, "_wanted")).Add(new ZDOID(77, 1), wanted);
            Redraw(map, false); Call(tracker, "Reconcile"); Call(tracker, "UpdatePositions");
            Check(pin.m_pos == new Vector3(20, 0, 20) && Redraw(map), "remote movement did not redraw");
            Redraw(map, false); Set(vehicleType, "Heading", 180f, wanted); ((IDictionary)Get(tracker, "_wanted"))[new ZDOID(77, 1)] = wanted;
            Call(tracker, "Reconcile"); Check(Redraw(map), "heading-only update did not redraw");
            Redraw(map, false); Call(tracker, "Reconcile"); Check(!Redraw(map), "unchanged vehicle forced redraw");
        });
        Case("failed verification rolls back only the affected group and preserves vendor registrations", () =>
        {
            Install(); Type vehicle = Ours("Vehicle");
            Call(vehicle, "RedrawReconcile", (object)new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) });
            Set(vehicle, "pending", true); Call(vehicle, "Verify");
            Check(!(bool)Get(vehicle, "enabled") && !Installed().Any(p => p.owner == "DeepNorthCompat.Map.VehicleUpdates"), "failed group hooks retained");
            Check((bool)Get(Ours("Persistence"), "enabled") && Harmony.GetPatchInfo(AccessTools.Method(typeof(Minimap), "UpdateExplore"))!.Prefixes.Any(p => p.owner == "xtav1m.BetterMap"), "unrelated or vendor hooks removed");
        });
        Coverage(true);
    }

    private static void Setup(string profile)
    {
        Cleanup(); errors.Clear(); warnings.Clear(); world = 42; localId = 99;
        Set(typeof(CompatibilityInstaller), "Info", (Action<string>)(_ => { }));
        Set(typeof(CompatibilityInstaller), "Warning", (Action<string>)warnings.Add);
        Set(typeof(CompatibilityInstaller), "Error", (Action<string>)errors.Add);
        var threading = Fake<BepInEx.ThreadingHelper>();
        Set(typeof(BepInEx.ThreadingHelper), "_invokeLock", new object(), threading);
        Set(typeof(BepInEx.ThreadingHelper), "<Instance>k__BackingField", threading);
        config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/bettermap-never-written.cfg"), false) { SaveOnConfigSet = false };
        Type plugin = vendor.GetType("BetterMap.Plugin", true)!;
        foreach (string name in new[] { "autoPin", "autoPinPortals", "showBoats", "showCarts", "rotateBoatIcons", "showVehicleNames", "showCreatures", "showCreatureStars" }) Set(plugin, name, config.Bind("Flags", name, true));
        foreach (string name in new[] { "debugMode", "forgetPinned" }) Set(plugin, name, config.Bind("Flags", name, false));
        foreach (string name in new[] { "explorationRadius", "vehicleRadius", "autoPinRadius" }) Set(plugin, name, config.Bind("Radii", name, 100f));
        Set(plugin, "vehicleRefreshInterval", config.Bind("Timing", "Vehicle", 5f));
        Set(plugin, "autoPinMergeDistance", config.Bind("Radii", "Merge", 5f));
        var fixture = new Harmony(Fixture);
        fixture.Patch(AccessTools.Method(Vendor("Icons"), "For"), prefix: Hook(nameof(Icon)));
        fixture.Patch(AccessTools.Method(typeof(ZNet), "GetWorldUID"), prefix: Hook(nameof(World)));
        fixture.Patch(AccessTools.Method(typeof(ZNet), "IsReferencePositionPublic"), prefix: Hook(nameof(PrivatePosition)));
        fixture.Patch(AccessTools.Method(typeof(ZNet), "SetPublicReferencePosition"), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(Player), "GetPlayerID"), prefix: Hook(nameof(PlayerId)));
        fixture.Patch(AccessTools.Method(typeof(ZDO), "GetPosition"), prefix: Hook(nameof(PortalPosition)));
        fixture.Patch(AccessTools.Method(Vendor("Pins.PortalPins"), "TagOf"), prefix: Hook(nameof(Tag)));
        fixture.Patch(AccessTools.Method(typeof(ZLog), "Log"), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(ZLog), "LogWarning"), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(ZLog), "LogError"), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.PropertyGetter(typeof(Application), "persistentDataPath"), prefix: Hook(nameof(DataPath)));
        fixture.Patch(AccessTools.Method(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) }), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(GameObject), "SetActive", new[] { typeof(bool) }), prefix: Hook(nameof(Skip)));
        fixture.Patch(AccessTools.Method(typeof(Texture2D), "GetPixels", Type.EmptyTypes), prefix: Hook(nameof(GetPixels)));
        fixture.Patch(AccessTools.Method(typeof(Texture2D), "SetPixels", new[] { typeof(Color[]) }), prefix: Hook(nameof(SetPixels)));
        fixture.Patch(AccessTools.Method(typeof(Texture2D), "GetPixel", new[] { typeof(int), typeof(int) }), prefix: Hook(nameof(GetPixel)));
        fixture.Patch(AccessTools.Method(typeof(Texture2D), "SetPixel", new[] { typeof(int), typeof(int), typeof(Color) }), prefix: Hook(nameof(SetPixel)));
        fixture.Patch(AccessTools.Method(typeof(Minimap), "ToggleIconFilter"), prefix: Hook(nameof(Toggle)));
        SetInstance(typeof(ZNet), Fake<ZNet>(), "m_instance");
        var scene = Fake<ZNetScene>(); SetInstance(typeof(ZNetScene), scene);
        Set(typeof(ZNetScene), "m_namedPrefabs", new Dictionary<int, GameObject>(), scene);
        Set(typeof(ZNetScene), "m_prefabs", new List<GameObject>(), scene);
        var platform = Fake<Splatform.PlatformManager>(); SetInstance(typeof(Splatform.PlatformManager), platform);
        object user = new PlatformBoundary(typeof(Splatform.ILocalUser), _ => new Splatform.PlatformUserID("Steam", "76561198000000002")).GetTransparentProxy();
        Set(typeof(Splatform.PlatformManager), "m_distributionPlatform",
            new PlatformBoundary(typeof(Splatform.IDistributionPlatform), _ => user).GetTransparentProxy(), platform);
        for (int i = 0; i <= 10; i++) sprites[i] = Fake<Sprite>();
        NewMap(18); NewPlayer("");
        new Harmony("xtav1m.BetterMap").PatchAll(vendor);
    }
    private static void Install()
    {
        foreach (Type type in groups)
            Call(type, "Prepare", type == Ours("Cartography") ? new object?[] { vendor, cartography } : new object?[] { vendor });
        foreach (Type type in groups) Call(type, "Verify");
        Check(errors.Count == 0, string.Join("\n", errors));
    }
    private static void Cleanup()
    {
        if (vendor != null && (bool)Get(Ours("Session"), "enabled")) Call(Ours("Session"), "BeforeSession");
        foreach (Type type in groups)
        {
            var owner = (string)AccessTools.Field(type, "Owner").GetRawConstantValue(); new Harmony(owner).UnpatchSelf();
            Set(type, "enabled", false); Set(type, "pending", false);
        }
        new Harmony("xtav1m.BetterMap").UnpatchSelf(); new Harmony(Fixture).UnpatchSelf();
        SetInstance(typeof(Minimap), null); SetInstance(typeof(ZNetScene), null); SetInstance(typeof(ZNet), null, "m_instance"); Player.m_localPlayer = null!;
        SetInstance(typeof(Splatform.PlatformManager), null);
        SetInstance(typeof(ZRoutedRpc), null); SetInstance(typeof(ZDOMan), null);
        foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer); pointers.Clear(); sprites.Clear(); pixels.Clear(); failPlacement = false;
        if (vendor == null) return;
        Call(Vendor("Pins.PinRecord"), "Forget");
        ((IDictionary)Get(Vendor("Vehicles.VehicleTracker"), "_tracked")).Clear();
        ((IDictionary)Get(Vendor("Vehicles.VehicleTracker"), "_wanted")).Clear();
    }
    private static IEnumerable<Patch> Installed() => Harmony.GetAllPatchedMethods().SelectMany(m =>
    { Patches p = Harmony.GetPatchInfo(m)!; return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers); })
        .Where(p => p.owner.StartsWith("DeepNorthCompat.Map.", StringComparison.Ordinal));
    private static T Fake<T>() where T : Object
    {
        T value = (T)FormatterServices.GetUninitializedObject(typeof(T)); IntPtr pointer = Marshal.AllocHGlobal(8);
        pointers.Add(pointer); Marshal.WriteInt32(pointer, ++sequence); Set(typeof(Object), "m_CachedPtr", pointer, value); return value;
    }
    private static void SetInstance(Type type, object? value, string field = "s_instance") => Set(type, field, value);
    private static Minimap NewMap(int first)
    {
        var map = Fake<Minimap>(); SetInstance(typeof(Minimap), map);
        Set(typeof(Minimap), "m_pins", new List<Minimap.PinData>(), map); Set(typeof(Minimap), "m_visibleIconTypes", Enumerable.Repeat(true, first + 11).ToArray(), map);
        Set(typeof(Minimap), "m_selectedIcons", new Dictionary<Minimap.PinType, UnityEngine.UI.Image>(), map);
        Set(typeof(Minimap), "m_textureSize", 2, map); Set(typeof(Minimap), "m_explored", new BitArray(4), map); Set(typeof(Minimap), "m_exploredOthers", new BitArray(4), map);
        var texture = Fake<Texture2D>(); pixels[texture] = Enumerable.Repeat(Color.white, 4).ToArray();
        Set(typeof(Minimap), "m_fogTexture", texture, map); Set(typeof(Minimap), "m_sharedMapHint", Fake<GameObject>(), map);
        map.m_icons = Enum.GetValues(typeof(Minimap.PinType)).Cast<Minimap.PinType>().Select(t => new Minimap.SpriteData { m_name = t, m_icon = sprites[0] }).ToList();
        IDictionary types = (IDictionary)Get(Vendor("Pins.PinLegend"), "_types"), categories = (IDictionary)Get(Vendor("Pins.PinLegend"), "_categories"); types.Clear(); categories.Clear();
        for (int i = 0; i < 9; i++)
        {
            object category = Enum.ToObject(Vendor("Pins.PinCategory"), i); var type = (Minimap.PinType)(first + i);
            types.Add(category, type); categories.Add(type, category); map.m_icons.Add(new Minimap.SpriteData { m_name = type, m_icon = sprites[i] });
        }
        return map;
    }
    private static Player NewPlayer(string pinned)
    {
        Player player = Fake<Player>(); player.m_customData = new Dictionary<string, string> { ["BetterMap.pinned"] = pinned }; Player.m_localPlayer = player; return player;
    }
    private static List<Minimap.PinData> Pins(Minimap map) => (List<Minimap.PinData>)Get(typeof(Minimap), "m_pins", map);
    private static bool[] Visible(Minimap map) => (bool[])Get(typeof(Minimap), "m_visibleIconTypes", map);
    private static bool Redraw(Minimap map) => (bool)Get(typeof(Minimap), "m_pinUpdateRequired", map);
    private static void Redraw(Minimap map, bool value) => Set(typeof(Minimap), "m_pinUpdateRequired", value, map);
    private static Minimap.PinData Add(Minimap map, int type, string name, Vector3 pos, bool save = true)
    {
        MethodInfo add = AccessTools.Method(typeof(Minimap), "AddPin"); object author = Activator.CreateInstance(add.GetParameters()[6].ParameterType)!;
        return (Minimap.PinData)add.Invoke(map, new[] { (object)pos, (Minimap.PinType)type, name, save, false, 0L, author });
    }
    private static Minimap.PinData Place(int category, string name, Vector3 pos)
    {
        Type ruleType = Vendor("Pins.PinRules+Rule"); object rule = Activator.CreateInstance(ruleType)!;
        Set(ruleType, "Category", Enum.ToObject(Vendor("Pins.PinCategory"), category), rule); Set(ruleType, "NameToken", name, rule);
        Call(Vendor("Pins.AutoPins"), "Place", rule, null, pos); return Pins(Minimap.instance).Last();
    }
    private static byte[] GetMap(Minimap map) => (byte[])AccessTools.Method(typeof(Minimap), "GetMapData").Invoke(map, null);
    private static void SetMap(Minimap map, byte[] bytes) => AccessTools.Method(typeof(Minimap), "SetMapData").Invoke(map, new object[] { bytes });
    private static bool Has(int category, Vector3 position) => (bool)Call(Vendor("Pins.PinRecord"), "Has", Enum.ToObject(Vendor("Pins.PinCategory"), category), position, 5f)!;
    private static void AddRecord(int category, Vector3 position) => Call(Vendor("Pins.PinRecord"), "Add", Enum.ToObject(Vendor("Pins.PinCategory"), category), position);
    private static void Portal() => Call(Vendor("Pins.AutoPins"), "Portal", FormatterServices.GetUninitializedObject(typeof(ZDO)), Vector3.zero, 10000f, 5f);
    private static byte[] Shared(int type, string name, Vector3 position, long owner, bool check)
    {
        var package = new ZPackage(); package.Write(3); package.Write(4); for (int i = 0; i < 4; i++) package.Write(false);
        package.Write(1); package.Write(owner); package.Write(name); package.Write(position); package.Write(type); package.Write(check); package.Write(""); return package.GetArray();
    }
    private static byte[] Metadata(byte[] native, (int Category, bool Automatic)[] entries)
    {
        Type data = typeof(Plugin).Assembly.GetType("DeepNorthCompat.BetterMapMapData", true)!, identity = data.GetNestedType("Identity", BindingFlags.NonPublic)!;
        Array values = Array.CreateInstance(identity, entries.Length);
        for (int i = 0; i < entries.Length; i++) values.SetValue(Activator.CreateInstance(identity, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { entries[i].Category, entries[i].Automatic }, null), i);
        return (byte[])Call(data, "Append", native, values)!;
    }
    private static void SetFog(Minimap map, bool[] explored, bool[] others)
    {
        Set(typeof(Minimap), "m_explored", new BitArray(explored), map);
        Set(typeof(Minimap), "m_exploredOthers", new BitArray(others), map);
    }
    private static void CheckFog(byte[] saved, bool[] explored, bool[] others)
    {
        var wrapper = new ZPackage(saved); Check(wrapper.ReadInt() == 8, "native map version changed");
        ZPackage native = wrapper.ReadCompressedPackage(); Check(native.ReadInt() == 2, "map dimensions changed");
        Check(explored.Select(_ => native.ReadBool()).SequenceEqual(explored), "personal exploration changed through map loading");
        Check(others.Select(_ => native.ReadBool()).SequenceEqual(others), "shared exploration changed through map loading");
    }
    private static void Deduplicate(bool secondChecked, string secondAuthor)
    {
        Install(); Minimap map = NewMap(18); NewPlayer(""); portalPosition = new Vector3(10, 0, 10); portalTag = "Old";
        var package = new ZPackage(); package.Write(3); package.Write(4); for (int i = 0; i < 4; i++) package.Write(false);
        package.Write(2);
        foreach (var row in new[] { (Checked: true, Author: "Steam_76561198000000001"), (Checked: secondChecked, Author: secondAuthor) })
        {
            package.Write(71L); package.Write("Old"); package.Write(portalPosition); package.Write((int)Minimap.PinType.Icon4);
            package.Write(row.Checked); package.Write(row.Author);
        }
        map.AddSharedMapData(Metadata(package.GetArray(), new[] { (10, true), (10, false) }));
        Check(Pins(map).Count == 1 && Pins(map)[0].m_checked && Pins(map)[0].m_author.ToString() == "Steam_76561198000000001", "native first row changed");
        portalTag = "Accepted"; Portal();
        Check(Pins(map).Count == 1 && Pins(map)[0].m_name == portalTag && Pins(map)[0].m_author.ToString() == "Steam_76561198000000001",
            "deduplicated automatic portal lost its category identity");
    }
    private static object skillConfig = null!;
    private static void SetCartography(string name, object value) => Set(skillConfig.GetType(), name, value, skillConfig);
    private static void ConfigureSkill(Player player, float level)
    {
        Type settings = cartography.GetType("Advize_CartographySkill.ModConfig", true)!;
        skillConfig = FormatterServices.GetUninitializedObject(settings);
        SetCartography("enableSkill", config.Bind("Cartography", "Enabled", true)); SetCartography("baseExploreRadius", config.Bind("Cartography", "Base", 100f));
        SetCartography("exploreRadiusIncrease", config.Bind("Cartography", "Increment", 1f)); SetCartography("enableDebugMessages", config.Bind("Cartography", "Debug", false));
        Type plugin = cartography.GetType("Advize_CartographySkill.CartographySkill", true)!; Set(plugin, "config", skillConfig);
        var skills = Fake<Skills>(); skills.m_skills = new List<Skills.SkillDef> { new Skills.SkillDef { m_skill = (Skills.SkillType)1337 } };
        Set(typeof(Skills), "m_skillData", new Dictionary<Skills.SkillType, Skills.Skill>(), skills); Set(typeof(Skills), "m_player", player, skills); Set(typeof(Player), "m_skills", skills, player);
        var status = (SEMan)FormatterServices.GetUninitializedObject(typeof(SEMan)); Set(typeof(SEMan), "m_statusEffects", new List<StatusEffect>(), status); Set(typeof(Character), "m_seman", status, player);
        new Harmony(Fixture).Patch(AccessTools.Method(typeof(Skills), "IsSkillValid"), postfix: new HarmonyMethod(AccessTools.Method(plugin.GetNestedType("SkillPatches", BindingFlags.NonPublic), "IsSkillValidPostfix")));
        var saved = new ZPackage(); saved.Write(2); saved.Write(1); saved.Write(1337); saved.Write(level); saved.Write(0f); skills.Load(new ZPackage(saved.GetArray()));
    }
    private static void Explore(Minimap map, Player player) => AccessTools.Method(typeof(Minimap), "UpdateExplore").Invoke(map, new object[] { 0f, player });
    private static bool Skip() => false;
    private static bool Icon(object category, ref Sprite __result) { int kind = Convert.ToInt32(category); __result = kind < 9 ? sprites[kind] : null!; return false; }
    private static bool World(ref long __result) { __result = world; return false; }
    private static bool PrivatePosition(ref bool __result) { __result = false; return false; }
    private static bool PlayerId(ref long __result) { __result = localId; return false; }
    private static bool PortalPosition(ref Vector3 __result) { __result = portalPosition; return false; }
    private static bool Tag(ref string __result) { __result = portalTag; return false; }
    private static bool DataPath(ref string __result) { __result = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox"); return false; }
    private static bool Toggle(Minimap __instance, Minimap.PinType type) { Visible(__instance)[(int)type] = !Visible(__instance)[(int)type]; Redraw(__instance, true); return false; }
    private static bool Snapshot(Player player) { snapshot = player.m_customData["BetterMap.pinned"]; return false; }
    private static bool CommitShutdown(Game __instance) { Set(typeof(Game), "m_shuttingDown", true, __instance); return false; }
    private static void VendorFailure() => throw new InvalidOperationException("fixture vendor failure");
    private static bool ClosestPin(Minimap __instance, ref Minimap.PinData __result) { __result = Pins(__instance).FirstOrDefault(); return false; }
    private static void PlacementFailure() { if (failPlacement) throw new InvalidOperationException("fixture placement failure"); }
    // Unity texture I/O is the boundary. Native map loading still owns explored-state changes.
    private static bool GetPixels(Texture2D __instance, ref Color[] __result) { __result = (Color[])pixels[__instance].Clone(); return false; }
    private static bool SetPixels(Texture2D __instance, Color[] colors) { pixels[__instance] = (Color[])colors.Clone(); return false; }
    private static bool GetPixel(Texture2D __instance, int x, int y, ref Color __result) { __result = pixels[__instance][y * 2 + x]; return false; }
    private static bool SetPixel(Texture2D __instance, int x, int y, Color color) { pixels[__instance][y * 2 + x] = color; return false; }

    private sealed class PlatformBoundary : RealProxy
    {
        private readonly Func<string, object> read;
        internal PlatformBoundary(Type type, Func<string, object> read) : base(type) { this.read = read; }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            return new ReturnMessage(read(call.MethodName), null, 0, call.LogicalCallContext, call);
        }
    }
}
