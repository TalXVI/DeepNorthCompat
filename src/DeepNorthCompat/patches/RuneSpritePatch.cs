using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    // SpriteFadeInOut.Awake reads sprite.material, which gives every floating rune sprite its own material
    // instance. The vendor destroys the sprite but never that instance, so Ocean Currents and Decumberance
    // leave about a thousand materials per second loaded. This hands each instance to a companion on the
    // sprite, which destroys it with the sprite.
    internal static class RuneSpritePatch
    {
        internal const string Owner = Plugin.Guid + ".RuneMagic.SpriteMaterials";
        private const string Group = "RuneMagic.SpriteMaterials";
        private static readonly MethodInfo getMaterial = AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.material));
        private static readonly MethodInfo rewrite = AccessTools.DeclaredMethod(typeof(RuneSpritePatch), nameof(Rewrite));
        private static readonly MethodInfo hook = AccessTools.DeclaredMethod(typeof(RuneSpritePatch), nameof(Instance));
        private static MethodInfo awake = null!;
        private static FieldInfo sprite = null!;
        private static bool pending, applied;
        private static string? failure;

        internal static void Prepare(Assembly? rune)
        {
            pending = applied = false;
            new Harmony(Owner).UnpatchSelf();
            if (rune == null) { CompatibilityInstaller.Info($"{Group}: optional mod absent; inactive."); return; }
            Guard.Build(rune, HearthTerrainPatch.RuneBuild);
            Guard.Build(typeof(ZNet).Assembly,
                ExpectedBuilds.Valheim1020Client,
                "EF8C8D3A1F45694EF7A701E8E2A25BF8A3874C951A2639C81DFE614CE3AA5FA3",
                "C36B81C1733C48D00DD567E41010E6DC95F8DAC877E0701CDB6B8C213ADE7812",
                ExpectedBuilds.Valheim1021Client,
                ExpectedBuilds.Valheim1021WindowsServer,
                ExpectedBuilds.Valheim1021LinuxServer);
            Type fade = Guard.Type(rune, "ValheimMod.Monobehaviours.SpriteFadeInOut");
            awake = Guard.Method(fade, "Awake", typeof(void));
            sprite = AccessTools.DeclaredField(fade, "sprite");
            if (sprite == null || sprite.IsStatic || sprite.FieldType != typeof(SpriteRenderer))
                throw new MissingFieldException(fade.FullName, "sprite");
            failure = "the transpiler did not run";
            new Harmony(Owner).Patch(awake, transpiler: new HarmonyMethod(rewrite));
            Guard.Registered(awake);
            pending = true;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            if (failure == null && Harmony.GetPatchInfo(awake)?.Transpilers.Count(p => p.owner == Owner && p.PatchMethod == rewrite) != 1)
                failure = "the transpiler registration did not match";
            if (failure != null)
            {
                new Harmony(Owner).UnpatchSelf();
                CompatibilityInstaller.Error($"{Group}: NOT APPLIED; {failure}; vendor behavior retained.");
                return;
            }
            applied = true;
            CompatibilityInstaller.Info($"{Group}: APPLIED; each rune sprite destroys the material instance it creates; shared and prefab materials, colors, and fading remain vendor-owned.");
        }

        // HarmonyX reruns this whenever another mod patches Awake. A miss keeps the incoming IL and the vendor's material lifetime.
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            // this.sprite.material
            int site = Site(code, i => code[i].Calls(getMaterial));
            if (site < 0 || !Shape(code, site - 2, Is(OpCodes.Ldarg_0), Loads(sprite), Calls(getMaterial)))
            {
                failure = "SpriteFadeInOut.Awake does not read sprite.material exactly once";
                Lost(ref applied, Group, failure);
                return original;
            }
            failure = null;
            code[site].opcode = OpCodes.Call;
            code[site].operand = hook;
            code.Insert(site, new CodeInstruction(OpCodes.Ldarg_0));
            return code;
        }

        // Replaces Awake's renderer.material read. The getter returns a new instance only when the renderer
        // did not already own one, so ownership is taken only when the shared material changed.
        private static Material? Instance(Renderer renderer, MonoBehaviour owner)
        {
            Material shared = renderer.sharedMaterial;
            Material material = renderer.material;
            if (material != null && material != shared) owner.gameObject.AddComponent<RuneSpriteMaterial>().Own(material);
            return material;
        }
    }

    // Lives on the object the vendor destroys, which is active while Awake runs, so OnDestroy is called.
    // The field is not serialized, so a copy of the sprite does not inherit the instance.
    internal sealed class RuneSpriteMaterial : MonoBehaviour
    {
        private Material? material;

        internal void Own(Material instance) => material = instance;

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
            material = null;
        }
    }
}
