using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class GameApiTests
{
    internal static void Run(string profile, Action<string, Action> test)
    {
        test("selected pack needs no legacy stats API bridge", () =>
        {
            using (var game = AssemblyDefinition.ReadAssembly(typeof(PlayerProfile).Assembly.Location))
                if (game.MainModule.Types.Single(type => type.Name == "PlayerProfile").Methods
                    .Any(method => method.Name == "GetStat" && method.Parameters.Count == 1)) return;

            var consumers = new List<string>();

            foreach (string directory in new[] { "plugins", "patchers", "core" }
                .Select(name => Path.Combine(profile, "BepInEx", name)).Where(Directory.Exists))
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                AssemblyDefinition assembly;

                try { assembly = AssemblyDefinition.ReadAssembly(path); }
                catch (BadImageFormatException) { continue; }

                using (assembly)
                    if (assembly.Modules.SelectMany(module => module.GetMemberReferences()).OfType<MethodReference>()
                        .Any(method => method.HasThis && method.FullName == "System.Single PlayerProfile::GetStat(PlayerStatType)"
                            && method.DeclaringType.Scope is AssemblyNameReference reference && reference.Name == "assembly_valheim"))
                        consumers.Add(path);
            }

            if (consumers.Count != 0)
                throw new Exception("Update mods that still require the removed stats API: " + string.Join(", ", consumers));
        });
    }
}
