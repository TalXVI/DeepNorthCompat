using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class ExpectedBuilds
    {
        // Exact audited Windows clients. Older supported builds remain in each group's guard.
        internal const string Valheim1020Client = "94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A";
        internal const string BetterMap = "BB0A0A2E4E9EA2B388CD55F486A61057BF57F982D5647AA649375D4B1E7BAE04";
        internal const string Cartography = "2379F6884BEC32843DAFA1306F693CAD8FC2FB1208E9D2BEE3D3745EFB2DC529";
        internal static readonly string[] TeleportEverything =
        {
            "7E4B67FAEC0A475312745B3ED06FD7754C14AAC4D6BCECFE56FD93E439DE605B",
            "502411B7A4EB99868ADC13CFB48A784FCEB89C814F8D48AFD3B4C75922798A46",
            "584DF4B5C285D7AA6B19DABC9E8486AD145EC2975A8678906062E183FEBAE586"
        };
        internal static readonly string[] PlantEverything =
        {
            "313D57D5D9DF2A27BFF04AB184487D544A0749BBBE2C10CB1C41A0AE964CAC62",
            "AE6F90220AC138E9CC90DEEFA530E934007DE4BF899B4AC51712525E268F6147"
        };
        internal static readonly string[] PlantEasily =
        {
            "D5AD697931C97E86DBC72AFA3B9BCAC90F3AE249C3A50FE7AD94302C4C2B8416",
            "9D56DAE0117798E504B0BAE000E4C848AEAF8642648E19CE99E36D59FA173ECB"
        };
        internal const string NoCultivatorDust = "D3761A25915D573207BF529C7DEF9372E2CABFEBEBFDC932D799837126F4EEB3";
        internal const string NoBuildDust = "80329CE6E96341F59D2CE9C77E4F1826E19B8D171473332A007B68AEA0963604";
        // BetterMap integration was audited against these 1.0.17, 1.0.20, and 1.0.21 client/server builds.
        internal static readonly string[] BetterMapGame =
        {
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA",
            Valheim1020Client,
            "EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3",
            "C36B81C1733C48D00DD567E41010E6DC95F8DAC877E0701CDB6B8C213ADE7812",
            GameBuilds.Valheim1021Client,
            GameBuilds.Valheim1021WindowsServer,
            GameBuilds.Valheim1021LinuxServer
        };
        // Builds 0.21.1 through 0.24.0 apply the skill factor twice to the bow draw drain; BowPatch corrects it.
        internal static readonly string[] ImpactfulSkillsBowDefect =
        {
            "D6E0844F3DADE42B23C33A678EB6E3D6DAA288DCC4187F497B8BB2A1F3CF47BA",
            "52DB5951ADDA57426706A58C9F7C530DFD7519A3E987C73516992111588EF656",
            "E74CDE9D2F5EC13DE46A301F4FCE2FB0F66932C234A5E8E6F685FB18D454E6D9",
            "79345A447506C6AE8D13A36DA8FD1FCDCBD965C5DA110B3953B8EA9517F7889D",
            "5B9CD6A20531E33BCE52E4EFD37631A82B0F0AA40D66860D8E1CDDC597736F66",
            "C87ACCAF7E710BD2A13E4F766E4EBFACE9495EFDBC7CADB7C847C3707581ADB4"
        };
        // 0.25.0 and 0.26.0 divide their bow reduction by the skill factor, so vanilla applies it once.
        internal static readonly string[] ImpactfulSkillsBowFixed =
        {
            "928DF592E04F489826DD823619FEDED6A04195CA38103855ED0C3F01C9EFC050",
            "19B9E4268C68AF645A2812574797EA9579DFBF5E1B3D8AD9FCA3A23F07DF8164"
        };
        internal static readonly string[] ImpactfulSkills =
            ImpactfulSkillsBowDefect.Concat(ImpactfulSkillsBowFixed).ToArray();
        internal static readonly string[] CraftyBoxes =
        {
            "432285A08AA0D43BBD89B36330BEDC19CB6EA93AED439E493D0287EDAECEDEB4",
            "381883FE4625D1DD119BB425A2F3C7522457FBC08AE1DB60159F8256DE940D6E"
        };
        internal static readonly string[] AAACrafting =
        {
            "316C5B47B449B8172627BD48C12830429E38BBD21A704746F86D1053D38D4830",
            "D511490759B3EE7A1041CBEF9BAF27BAEF9530C6F225323D824628638B62E6AF"
        };
        internal const string SeaAnimals = "3F57F6AD089D5616A924D5917851A0A7C99715CC0B2062EF23B2FA15D07C721A";
        internal const string AirAnimals = "B29905FFDA5204570D64958CB18D74E08D7CEE8AA361485E1D988B54242F2F85";
        // Audited 1.0.16, 1.0.17, 1.0.20, and 1.0.21 clients.
        internal static readonly string[] Valheim =
        {
            "96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127",
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            Valheim1020Client,
            GameBuilds.Valheim1021Client
        };
        internal static readonly string[] AzuEpi =
        {
            "0C8702F9F9A3F4AE2A0E9977801F40E1691E42D1B5CE60863C1B01FF5697C52E",
            "BA743704019F1C693A53A445162C95965854B55EDDC83C5EA45C8F66D938FA5F"
        };
        internal const string OdinShip = "5D5817BC169677DB49CAE55986B3F6A178AB3BF0B1D681A2ABA4ECC5DAC067D0";
        // OdinShip integration was inspected against these 1.0.17, 1.0.20, and 1.0.21 roles.
        internal static readonly string[] OdinShipGame =
        {
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA",
            Valheim1020Client,
            "EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3",
            "C36B81C1733C48D00DD567E41010E6DC95F8DAC877E0701CDB6B8C213ADE7812",
            GameBuilds.Valheim1021Client,
            GameBuilds.Valheim1021WindowsServer,
            GameBuilds.Valheim1021LinuxServer
        };
        internal static readonly string[] Server =
        {
            "7CAB9B49D31EC064591CA80402DD35C566E03B7297CFB7BF4696C38DA4E24D8B",
            "50035055F9B158A025CACD25E038B603943F7C2A465DA3021707B5F1E44E39FD",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA",
            "EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3",
            "C36B81C1733C48D00DD567E41010E6DC95F8DAC877E0701CDB6B8C213ADE7812",
            GameBuilds.Valheim1021WindowsServer,
            GameBuilds.Valheim1021LinuxServer
        };
    }

    public static class CompatibilityInstaller
    {
        internal const string ImpactGuid = "MidnightsFX.ImpactfulSkills";
        internal const string CraftyGuid = "Azumatt.AzuCraftyBoxes";
        internal static Action<string> Info = _ => { };
        internal static Action<string> Warning = _ => { };
        internal static Action<string> Error = _ => { };

        public static void Install(Func<string, Assembly?> resolve, Action<string> info,
            Action<string> warning, Action<string> error)
        {
            Info = info; Warning = warning; Error = error;
            Assembly? impact = resolve(ImpactGuid);
            Assembly? crafty = resolve(CraftyGuid);
            InstallGroup("Bow", () =>
            {
                if (impact == null) { Info("Bow: ImpactfulSkills absent; inactive."); return; }
                if (Guard.KnownServerBuild()) { Info("Bow: client-only patch; inactive on dedicated server."); return; }
                Guard.Build(impact, ExpectedBuilds.ImpactfulSkills);
                if (ExpectedBuilds.ImpactfulSkillsBowFixed.Contains(Guard.Sha256(impact.Location)))
                {
                    Info("Bow: fixed upstream in this ImpactfulSkills build; inactive.");
                    return;
                }
                Guard.Build(typeof(InventoryGui).Assembly, ExpectedBuilds.Valheim);
                BowPatch.Install(impact);
            });

            InstallGroup("Drops.SeaAnimals", () => InstallDrops(resolve("marlthon.SeaAnimals"),
                ExpectedBuilds.SeaAnimals, "SeaAnimals"));
            InstallGroup("Drops.AirAnimals", () => InstallDrops(resolve("marlthon.AirAnimals"),
                ExpectedBuilds.AirAnimals, "AirAnimals"));
            InstallGroup("Quality", () =>
            {
                if (impact == null || crafty == null) { Info("Quality: optional mod absent; inactive."); return; }
                if (Guard.KnownServerBuild()) { Info("Quality: client-only patch; inactive on dedicated server."); return; }
                Guard.Build(impact, ExpectedBuilds.ImpactfulSkills);
                Guard.Build(crafty, ExpectedBuilds.CraftyBoxes);
                Guard.Build(typeof(InventoryGui).Assembly, ExpectedBuilds.Valheim);

                Assembly? aaa = resolve(ForgeCraftingPatch.Guid);
                if (aaa != null) Guard.Build(aaa, ExpectedBuilds.AAACrafting);

                QualityPatch.Install(impact, crafty, aaa != null);
            });

            InstallGroup("Crafting.ForgeOfPotential", () => ForgeCraftingPatch.Prepare(resolve(ForgeCraftingPatch.Guid)));
            InstallGroup("UI.AAARecipeRows", () => AaaRecipeRowsPatch.Prepare(resolve(ForgeCraftingPatch.Guid)));
            InstallGroup("Crafting.ReforgedPotential", () => ReforgedPotentialPatch.Prepare(resolve(ReforgedPotentialPatch.Guid)));
            InstallGroup("Preview.AzuEPI", () =>
            {
                if (Guard.KnownServerBuild()) { Info("Preview.AzuEPI: client-only patch; inactive on dedicated server."); return; }
                AzuEpiPreviewPatch.Prepare(resolve(AzuEpiPreviewPatch.Owner));
            });
            InstallGroup("VPO.Burst", () => VpoBurstPatch.Prepare(resolve("dev.ontrigger.vpo")));
            InstallGroup("Performance.IdleSync", IdleSyncPatch.Prepare);
            InstallGroup("RuneMagic.Ice", () => RuneIcePatch.Prepare(resolve(HearthTerrainPatch.RuneGuid)));
            InstallGroup("RuneMagic.SpriteMaterials", () => RuneSpritePatch.Prepare(resolve(HearthTerrainPatch.RuneGuid)));
            InstallGroup("UI.TabAudio", TabAudioPatch.Prepare);
            InstallGroup("UI.TeleportEverything", () => TeleportEverythingPatch.Prepare(resolve(TeleportEverythingPatch.Guid)));
            InstallGroup("MyLittleUI.AmmoCount", () => MyLittleUiAmmoPatch.Prepare(resolve(MyLittleUiAmmoPatch.Guid)));
            InstallGroup("PlantEasily.GridCost", () => PlantEasilyCostPatch.Prepare(resolve(PlantEasilyCostPatch.Guid)));
            InstallGroup("NoSmokeStayLit.Controls", () => NoSmokeStayLitPatch.Prepare(resolve(NoSmokeStayLitPatch.Guid)));
            InstallGroup("PlantEverything.Dust", () => PlantEverythingDustPatch.Prepare(resolve(PlantEverythingDustPatch.Guid),
                resolve(PlantEverythingDustPatch.CultivatorDust), resolve(PlantEverythingDustPatch.BuildDust)));
            InstallGroup("Tune.Client", () => TuneClientPatch.Prepare(resolve("akoozie.valheimtune")));
            Assembly? hearth = resolve(HearthTerrainPatch.Guid);
            InstallGroup("Terrain.HearthBelow.Replay", () => HearthTerrainPatch.PrepareReplay(hearth));
            InstallGroup("Terrain.HearthBelow", () => HearthTerrainPatch.Prepare(hearth, resolve(HearthTerrainPatch.RuneGuid)));
            Assembly? odin = resolve("marlthon.OdinShip");
            InstallGroup("OdinShip.Core", () => OdinShipPatch.Prepare(odin));
            InstallGroup("OdinShip.Input", () => OdinShipInputPatch.Prepare(odin));
            InstallGroup("OdinShip.FishPress", () => OdinShipFishPatch.Prepare(odin));
            InstallGroup("OdinShip.Construction", () => OdinShipConstructionPatch.Prepare(odin));
            Assembly? betterMap = resolve(BetterMapAccess.Guid);
            InstallGroup("Map.Cartography", () => BetterMapCartographyPatch.Prepare(betterMap, resolve("advize.CartographySkill")));
            InstallGroup("Map.Session", () => BetterMapSessionPatch.Prepare(betterMap));
            InstallGroup("Map.Persistence", () => BetterMapPersistencePatch.Prepare(betterMap));
            InstallGroup("Map.Portals", () => BetterMapPortalPatch.Prepare(betterMap));
            InstallGroup("Map.VehicleUpdates", () => BetterMapVehiclePatch.Prepare(betterMap));
            // The registry also answers crafting handoffs, so every peer serves other peers'
            // requests even when its own crafting hooks or MultiUserChest guards fail.
            InstallGroup("Chests.Registry", () => ChestRegistry.Install(crafty));
            InstallGroup("ChestCraft", () => ChestCraftPatch.Install(crafty));
            InstallGroup("Chests", () => ChestSyncPatch.Install(resolve(ChestSyncPatch.MucGuid), resolve(ChestSyncPatch.QuickGuid)));
            InstallGroup("Diagnostics", DiagnosticsPatch.Install);
        }

        // Harmony may defer applying patches until after Awake, so confirm transpilers ran later.
        public static void Verify()
        {
            DropRangePatch.Verify();
            ForgeCraftingPatch.Verify();
            AaaRecipeRowsPatch.Verify();
            ReforgedPotentialPatch.Verify();
            AzuEpiPreviewPatch.Verify();
            VpoBurstPatch.Verify();
            IdleSyncPatch.Verify();
            RuneIcePatch.Verify();
            RuneSpritePatch.Verify();
            TabAudioPatch.Verify();
            TeleportEverythingPatch.Verify();
            MyLittleUiAmmoPatch.Verify();
            PlantEasilyCostPatch.Verify();
            NoSmokeStayLitPatch.Verify();
            PlantEverythingDustPatch.Verify();
            TuneClientPatch.Verify();
            HearthTerrainPatch.Verify();
            OdinShipPatch.Verify();
            OdinShipInputPatch.Verify();
            OdinShipFishPatch.Verify();
            OdinShipConstructionPatch.Verify();
            BetterMapCartographyPatch.Verify();
            BetterMapSessionPatch.Verify();
            BetterMapPersistencePatch.Verify();
            BetterMapPortalPatch.Verify();
            BetterMapVehiclePatch.Verify();
            DiagnosticsPatch.Verify();
        }

        private static void InstallDrops(Assembly? assembly, string hash, string name)
        {
            if (assembly == null)
            {
                Info($"Drops.{name}: mod absent; inactive.");
                return;
            }

            Guard.Build(assembly, hash);
            DropRangePatch.Install(assembly, name);
        }

        private static void InstallGroup(string name, Action install)
        {
            try
            {
                install();
            }
            catch (Exception exception)
            {
                try
                {
                    new Harmony(Plugin.Guid + "." + name).UnpatchSelf();
                }
                catch (Exception rollback)
                {
                    Error($"{name}: Harmony rollback failed; some hooks may remain active. {rollback}");
                }
                Error($"{name}: NOT APPLIED; expected installed implementation changed or patch failed. "
                    + $"Vendor behavior retained. {exception}");
            }
        }
    }

    public static class Guard
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> hashes = new Dictionary<string, (long, DateTime, string)>(StringComparer.Ordinal);

        internal static bool KnownServerBuild() => ExpectedBuilds.Server.Contains(Sha256(typeof(ZNet).Assembly.Location));

        public static void Build(Assembly assembly, params string[] expectedHashes)
        {
            string actual = Sha256(assembly.Location);
            if (!expectedHashes.Contains(actual))
                throw new NotSupportedException($"{assembly.GetName().Name} build changed; expected "
                    + $"{string.Join(" or ", expectedHashes)}, found {actual}. Re-audit required.");
        }

        // A file's SHA-256 in uppercase hexadecimal. Startup checks the game assembly about 30 times, and Mono
        // hashes it in about 15 ms, so a hash is reused until the file's size or write time changes.
        internal static string Sha256(string path)
        {
            var info = new FileInfo(path);
            long length = info.Length;
            DateTime written = info.LastWriteTimeUtc;
            lock (gate)
                if (hashes.TryGetValue(path, out var known) && known.Length == length && known.Written == written) return known.Hash;
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            lock (gate) hashes[path] = (length, written, hash);
            return hash;
        }

        internal static Type Type(Assembly assembly, string name) =>
            assembly.GetType(name, throwOnError: true)!;

        internal static MethodInfo Method(Type type, string name, Type returnType, params Type[] parameters)
        {
            MethodInfo? method = type.GetMethod(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameters, null);

            if (method == null || method.ReturnType != returnType)
            {
                throw new MissingMethodException(type.FullName, name);
            }

            return method;
        }

        internal static HarmonyMethod Hook(Type type, string name, int priority = Priority.Normal)
        {
            return new HarmonyMethod(AccessTools.DeclaredMethod(type, name)) { priority = priority };
        }

        internal static void Registered(MethodBase target)
        {
            CompatibilityInstaller.Info($"Registered: {Name(target)}");
        }

        // HarmonyX unpatches a method from every patch kind and owner, so a vendor hook that DNC
        // replaces must be the only registration of that method on the target.
        internal static Patch Sole(MethodBase target, MethodInfo hook, string owner, HarmonyPatchType kind)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            Patch[] matches = info == null ? Array.Empty<Patch>() : All(info).Where(p => p.PatchMethod == hook).ToArray();
            if (matches.Length != 1 || matches[0].owner != owner || !Of(info!, kind).Contains(matches[0]))
                throw new NotSupportedException($"Expected one {owner} {kind.ToString().ToLowerInvariant()} {Name(hook)} on {Name(target)}.");
            return matches[0];
        }

        // Re-registers a vendor hook that a failed replacement removed, with its original owner and ordering.
        internal static void Restore(MethodBase target, Patch patch, HarmonyPatchType kind)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            if (info != null && All(info).Any(p => p.PatchMethod == patch.PatchMethod)) return;
            if (kind != HarmonyPatchType.Prefix && kind != HarmonyPatchType.Postfix) throw new ArgumentOutOfRangeException(nameof(kind));
            var hook = new HarmonyMethod(patch.PatchMethod, patch.priority, patch.before, patch.after, patch.debug);
            new Harmony(patch.owner).Patch(target, prefix: kind == HarmonyPatchType.Prefix ? hook : null,
                postfix: kind == HarmonyPatchType.Postfix ? hook : null);
        }

        private static IEnumerable<Patch> All(Patches info) =>
            info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators);

        private static IEnumerable<Patch> Of(Patches info, HarmonyPatchType kind) => kind switch
        {
            HarmonyPatchType.Prefix => info.Prefixes,
            HarmonyPatchType.Postfix => info.Postfixes,
            HarmonyPatchType.Transpiler => info.Transpilers,
            HarmonyPatchType.Finalizer => info.Finalizers,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        internal static string Name(MethodBase target)
        {
            return $"{target.DeclaringType!.Assembly.GetName().Name}:{target.DeclaringType.FullName}.{target.Name}";
        }
    }
}
