using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class ExpectedBuilds
    {
        internal static readonly string[] ImpactfulSkills =
        {
            "D6E0844F3DADE42B23C33A678EB6E3D6DAA288DCC4187F497B8BB2A1F3CF47BA",
            "52DB5951ADDA57426706A58C9F7C530DFD7519A3E987C73516992111588EF656"
        };
        internal const string CraftyBoxes = "432285A08AA0D43BBD89B36330BEDC19CB6EA93AED439E493D0287EDAECEDEB4";
        internal const string AAACrafting = "316C5B47B449B8172627BD48C12830429E38BBD21A704746F86D1053D38D4830";
        internal const string SeaAnimals = "3F57F6AD089D5616A924D5917851A0A7C99715CC0B2062EF23B2FA15D07C721A";
        internal const string AirAnimals = "B29905FFDA5204570D64958CB18D74E08D7CEE8AA361485E1D988B54242F2F85";
        // 1.0.16 and 1.0.17 clients; 1.0.17 changes no method these patches hook.
        internal static readonly string[] Valheim =
        {
            "96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127",
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE"
        };
        internal const string AzuEpi = "0C8702F9F9A3F4AE2A0E9977801F40E1691E42D1B5CE60863C1B01FF5697C52E";
        internal const string OdinShip = "5D5817BC169677DB49CAE55986B3F6A178AB3BF0B1D681A2ABA4ECC5DAC067D0";
        // OdinShip integration was inspected against these 1.0.17 roles only.
        internal static readonly string[] OdinShipGame =
        {
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA"
        };
        internal static readonly string[] Server =
        {
            "7CAB9B49D31EC064591CA80402DD35C566E03B7297CFB7BF4696C38DA4E24D8B",
            "50035055F9B158A025CACD25E038B603943F7C2A465DA3021707B5F1E44E39FD",
            "0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD",
            "E7220DC5D9CF9D38270E751352D94918308E59CCA86E56C4BDA0210016C847FA"
        };
    }

    public static class CompatibilityInstaller
    {
        internal static Action<string> Info = _ => { };
        internal static Action<string> Warning = _ => { };
        internal static Action<string> Error = _ => { };

        public static void Install(Func<string, Assembly?> resolve, Action<string> info,
            Action<string> warning, Action<string> error)
        {
            Info = info; Warning = warning; Error = error;
            Assembly? impact = resolve("MidnightsFX.ImpactfulSkills");
            Assembly? crafty = resolve("Azumatt.AzuCraftyBoxes");
            InstallGroup("Bow", () =>
            {
                if (impact == null) { Info("Bow: ImpactfulSkills absent; inactive."); return; }
                if (Guard.KnownServerBuild()) { Info("Bow: client-only patch; inactive on dedicated server."); return; }
                Guard.Build(impact, ExpectedBuilds.ImpactfulSkills);
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

                Assembly? aaa = resolve("Azumatt.AzuAntiArthriticCrafting");
                if (aaa != null) Guard.Build(aaa, ExpectedBuilds.AAACrafting);

                QualityPatch.Install(impact, crafty, aaa != null);
            });

            InstallGroup("Preview.AzuEPI", () =>
            {
                if (Guard.KnownServerBuild()) { Info("Preview.AzuEPI: client-only patch; inactive on dedicated server."); return; }
                AzuEpiPreviewPatch.Prepare(resolve(AzuEpiPreviewPatch.Owner));
            });
            InstallGroup("VPO.Burst", () => VpoBurstPatch.Prepare(resolve("dev.ontrigger.vpo")));
            InstallGroup("UI.TabAudio", TabAudioPatch.Prepare);
            InstallGroup("Tune.Client", () => TuneClientPatch.Prepare(resolve("akoozie.valheimtune")));
            Assembly? odin = resolve("marlthon.OdinShip");
            InstallGroup("OdinShip.Core", () => OdinShipPatch.Prepare(odin));
            InstallGroup("OdinShip.Input", () => OdinShipInputPatch.Prepare(odin));
            InstallGroup("OdinShip.FishPress", () => OdinShipFishPatch.Prepare(odin));
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
            AzuEpiPreviewPatch.Verify();
            VpoBurstPatch.Verify();
            TabAudioPatch.Verify();
            TuneClientPatch.Verify();
            OdinShipPatch.Verify();
            OdinShipInputPatch.Verify();
            OdinShipFishPatch.Verify();
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
        internal static bool KnownServerBuild()
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(typeof(ZNet).Assembly.Location))
                return ExpectedBuilds.Server.Contains(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""));
        }
        public static void Build(Assembly assembly, params string[] expectedHashes)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(assembly.Location))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (!expectedHashes.Contains(actual))
                    throw new NotSupportedException($"{assembly.GetName().Name} build changed; expected "
                        + $"{string.Join(" or ", expectedHashes)}, found {actual}. Re-audit required.");
            }
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

        internal static string Name(MethodBase target)
        {
            return $"{target.DeclaringType!.Assembly.GetName().Name}:{target.DeclaringType.FullName}.{target.Name}";
        }
    }
}
