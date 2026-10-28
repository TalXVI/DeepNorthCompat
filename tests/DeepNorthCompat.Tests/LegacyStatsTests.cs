using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;
using DeepNorthCompat.Preloader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilAttributes = Mono.Cecil.TypeAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;

internal static class LegacyStatsTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("legacy stats restores binary calls and preserves difficulty and raw statistics", () =>
        {
            using (var fixture = new Fixture())
            using (var game = AssemblyDefinition.ReadAssembly(typeof(PlayerProfile).Assembly.Location))
            {
                TypeDefinition profile = game.MainModule.Types.Single(type => type.Name == "PlayerProfile");
                if (profile.Methods.Any(method => method.Name == "GetStat" && method.Parameters.Count == 1)) return;

                using (var consumer = fixture.Consumer())
                {
                    consumer.Write(Path.Combine(fixture.Plugins, "consumer.dll"));
                    Check(Patcher.TargetDLLs.SequenceEqual(new[] { "assembly_valheim.dll" }), "legacy consumer must activate the bridge");
                    Patcher.Patch(game);

                    game.Name.Name = "LegacyStatsGame" + Guid.NewGuid().ToString("N");
                    consumer.MainModule.AssemblyReferences.Single(reference => reference.Name == "assembly_valheim").Name = game.Name.Name;
                    var inputs = new TypeDefinition("", "StatsInputs", CecilAttributes.Public, game.MainModule.TypeSystem.Object);
                    game.MainModule.Types.Add(inputs);
                    var eligible = new FieldDefinition("Eligible", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, game.MainModule.TypeSystem.Boolean);
                    var difficulty = new FieldDefinition("Difficulty", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, game.MainModule.TypeSystem.Int32);
                    inputs.Fields.Add(eligible);
                    inputs.Fields.Add(difficulty);
                    TypeDefinition achievements = game.MainModule.Types.Single(type => type.Name == "Achievements");
                    MethodDefinition allowed = achievements.Methods.Single(method => method.Name == "CanGetAchievements");
                    allowed.Body = new Mono.Cecil.Cil.MethodBody(allowed);
                    allowed.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, eligible));
                    allowed.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

                    MethodDefinition current = achievements.Methods.Single(method => method.Name == "GetCurrentAchievementDifficultyIndex");
                    current.Body = new Mono.Cecil.Cil.MethodBody(current);
                    Instruction readDifficulty = Instruction.Create(OpCodes.Ldsfld, difficulty);
                    ILProcessor il = current.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldsfld, eligible);
                    il.Emit(OpCodes.Brtrue, readDifficulty);
                    il.Emit(OpCodes.Newobj, game.MainModule.ImportReference(typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)));
                    il.Emit(OpCodes.Throw);
                    il.Append(readDifficulty);
                    il.Emit(OpCodes.Ret);

                    Assembly loaded = Load(game);
                    Type state = loaded.GetType("StatsInputs", true)!;
                    Type playerProfile = loaded.GetType("PlayerProfile", true)!;
                    Type statType = loaded.GetType("PlayerStatType", true)!;
                    object stat = Enum.ToObject(statType, 0);
                    object instance = FormatterServices.GetUninitializedObject(playerProfile);
                    FieldInfo statsField = playerProfile.GetField("m_playerStats", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
                    Type statsType = statsField.FieldType.GetElementType()!;
                    Array stats = Array.CreateInstance(statsType, 3);

                    for (int index = 0; index < stats.Length; index++)
                    {
                        object values = Activator.CreateInstance(statsType)!;
                        statsType.GetProperty("Item")!.SetValue(values, (index + 1) * 11f, new[] { stat });
                        stats.SetValue(values, index);
                    }

                    statsField.SetValue(instance, stats);
                    MethodInfo legacy = Load(consumer).GetType("Consumer", true)!.GetMethod("Read")!;
                    MethodInfo modern = playerProfile.GetMethods().Single(method => method.Name == "GetStat" && method.GetParameters().Length == 2);
                    object explicitDifficulty = Enum.ToObject(modern.GetParameters()[1].ParameterType, 1);
                    state.GetField("Eligible")!.SetValue(null, true);
                    state.GetField("Difficulty")!.SetValue(null, 2);
                    Check((float)legacy.Invoke(null, new[] { instance, stat })! == 33f, "old binary call must read the current difficulty");
                    Check((float)modern.Invoke(instance, new[] { stat, explicitDifficulty })! == 22f, "new API must retain explicit difficulty selection");
                    state.GetField("Eligible")!.SetValue(null, false);
                    Check((float)legacy.Invoke(null, new[] { instance, stat })! == 11f, "ineligible profiles must read raw statistics without querying difficulty");
                }
            }
        });

        test("legacy stats has no targets without a legacy consumer", () =>
        {
            using (var fixture = new Fixture())
            {
                Check(!Patcher.TargetDLLs.Any(), "empty plugin and patcher directories must be inactive");
                File.WriteAllBytes(Path.Combine(fixture.Plugins, "native.dll"), new byte[] { 1, 2, 3 });

                using (var consumer = fixture.Consumer(modern: true))
                    consumer.Write(Path.Combine(fixture.Plugins, "modern.dll"));

                Check(!Patcher.TargetDLLs.Any(), "native libraries and new-API consumers must not activate the bridge");
            }
        });

        test("legacy stats rejects unknown missing-API builds and skips an existing overload", () =>
        {
            using (var fixture = new Fixture())
            using (var game = AssemblyDefinition.ReadAssembly(typeof(PlayerProfile).Assembly.Location))
            using (var consumer = fixture.Consumer())
            {
                TypeDefinition profile = game.MainModule.Types.Single(type => type.Name == "PlayerProfile");
                if (profile.Methods.Any(method => method.Name == "GetStat" && method.Parameters.Count == 1)) return;

                consumer.Write(Path.Combine(fixture.Plugins, "consumer.dll"));
                game.MainModule.Mvid = Guid.NewGuid();
                string path = Path.Combine(fixture.Plugins, "assembly_valheim.dll");
                game.Write(path);
                typeof(Paths).GetProperty("ManagedPath")!.SetValue(null, fixture.Plugins);
                Check(!Patcher.TargetDLLs.Any(), "unknown game bytes must not be targeted");

                using (var unknown = AssemblyDefinition.ReadAssembly(path))
                {
                    try { Patcher.Patch(unknown); throw new Exception("unknown game was rewritten"); }
                    catch (NotSupportedException) { }
                    Check(!unknown.MainModule.Types.Single(type => type.Name == "PlayerProfile").Methods.Any(method => method.Name == "GetStat" && method.Parameters.Count == 1),
                        "rejection must leave the assembly unchanged");
                }

                var existing = new MethodDefinition("GetStat", CecilMethodAttributes.Public, game.MainModule.TypeSystem.Single);
                existing.Parameters.Add(new ParameterDefinition(profile.Methods.First(method => method.Name == "GetStat").Parameters[0].ParameterType));
                existing.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_R4, 77f));
                existing.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                profile.Methods.Add(existing);
                game.Write(path);
                Check(!Patcher.TargetDLLs.Any(), "an existing API must not be targeted even on an unknown build");
                Patcher.Patch(game);
                Check(profile.Methods.Count(method => method.Name == "GetStat" && method.Parameters.Count == 1) == 1
                    && (float)existing.Body.Instructions[0].Operand == 77f, "an existing overload must remain unchanged");
            }
        });
    }

    private static Assembly Load(AssemblyDefinition assembly)
    {
        using (var stream = new MemoryStream())
        {
            assembly.Write(stream);
            return Assembly.Load(stream.ToArray());
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "dnc-stats-" + Guid.NewGuid().ToString("N"));
        private readonly string[] names = { "ManagedPath", "PluginPath", "PatcherPluginPath" };
        private readonly object[] previous;
        internal string Plugins => Path.Combine(root, "plugins");

        internal Fixture()
        {
            previous = names.Select(name => typeof(Paths).GetProperty(name)!.GetValue(null)!).ToArray();
            string managed = Path.GetDirectoryName(typeof(PlayerProfile).Assembly.Location)!;
            string[] paths = { managed, Plugins, Path.Combine(root, "patchers") };
            Directory.CreateDirectory(paths[1]);
            Directory.CreateDirectory(paths[2]);

            for (int index = 0; index < names.Length; index++)
                typeof(Paths).GetProperty(names[index])!.SetValue(null, paths[index]);
        }

        internal AssemblyDefinition Consumer(bool modern = false)
        {
            var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("StatsConsumer" + Guid.NewGuid().ToString("N"), new System.Version(1, 0)), "consumer", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            var reference = new AssemblyNameReference("assembly_valheim", new System.Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(reference);
            var profile = new TypeReference("", "PlayerProfile", module, reference);
            var stat = new TypeReference("", "PlayerStatType", module, reference, true);
            var getStat = new MethodReference("GetStat", module.TypeSystem.Single, profile) { HasThis = true };
            getStat.Parameters.Add(new ParameterDefinition(stat));
            if (modern) getStat.Parameters.Add(new ParameterDefinition(new TypeReference("", "DifficultyRequirement", module, reference, true)));
            var type = new TypeDefinition("", "Consumer", CecilAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static, module.TypeSystem.Single);
            method.Parameters.Add(new ParameterDefinition(profile));
            method.Parameters.Add(new ParameterDefinition(stat));
            type.Methods.Add(method);
            ILProcessor il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            if (modern) il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, getStat);
            il.Emit(OpCodes.Ret);
            return assembly;
        }

        public void Dispose()
        {
            for (int index = 0; index < names.Length; index++)
                typeof(Paths).GetProperty(names[index])!.SetValue(null, previous[index]);

            Directory.Delete(root, true);
        }
    }
}
