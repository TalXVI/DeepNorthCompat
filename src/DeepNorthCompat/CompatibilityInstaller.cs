using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace DeepNorthCompat
{
    internal static class ExpectedBuilds
    {
        internal const string ImpactfulSkills = "ADE4FD2943A886B9B90C0241CB77EC65A97C1840EF39A906B58BAF3925086706";
        internal const string CraftyBoxes = "432285A08AA0D43BBD89B36330BEDC19CB6EA93AED439E493D0287EDAECEDEB4";
        internal const string AAACrafting = "316C5B47B449B8172627BD48C12830429E38BBD21A704746F86D1053D38D4830";
        internal const string SeaAnimals = "3F57F6AD089D5616A924D5917851A0A7C99715CC0B2062EF23B2FA15D07C721A";
        internal const string AirAnimals = "B29905FFDA5204570D64958CB18D74E08D7CEE8AA361485E1D988B54242F2F85";
        internal const string Valheim = "96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127";
        internal const string AzuEpi = "41ED9378929090C5C46363A319C6A1F63F85DCF18DDE2DA1C0CAB49C39DBECFA";
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
                Guard.Build(impact, ExpectedBuilds.ImpactfulSkills);
                Guard.Build(crafty, ExpectedBuilds.CraftyBoxes);
                Guard.Build(typeof(InventoryGui).Assembly, ExpectedBuilds.Valheim);

                Assembly? aaa = resolve("Azumatt.AzuAntiArthriticCrafting");
                if (aaa != null) Guard.Build(aaa, ExpectedBuilds.AAACrafting);

                QualityPatch.Install(impact, crafty, aaa != null);
            });

            InstallGroup("Preview.AzuEPI", () => AzuEpiPreviewPatch.Prepare(resolve(AzuEpiPreviewPatch.Owner)));
        }

        // Harmony may defer applying patches until after Awake, so confirm transpilers ran later.
        public static void Verify()
        {
            DropRangePatch.Verify();
            AzuEpiPreviewPatch.Verify();
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
        public static void Build(Assembly assembly, string expectedHash)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(assembly.Location))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (actual != expectedHash)
                    throw new NotSupportedException($"{assembly.GetName().Name} build changed; expected "
                        + $"{expectedHash}, found {actual}. Re-audit required.");
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
