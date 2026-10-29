using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using static DeepNorthCompat.ILMatch;

namespace DeepNorthCompat
{
    internal static class TabAudioPatch
    {
        internal const string Owner = Plugin.Guid + ".UI.TabAudio";
        private static readonly string[] GameHashes =
        {
            "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE",
            ExpectedBuilds.Valheim1020Client,
            GameBuilds.Valheim1021Client
        };
        private static readonly string[] GuiHashes =
        {
            "52846FCAE0C535D733711C13FDB8F214D303603B5DBF23D05B1ED6A8E475C9DD",
            ExpectedBuilds.GuiUtils1021Client
        };
        internal const string VcpOwner = "MidnightsFX.ValheimCommunityPatch";
        private static readonly string[] VcpHashes =
        {
            "C3726D510F6081D5BEAEAC4241E1AC107E2382069688055DECCF9E5418F58B46",
            "640638095848ED8FDF770F7C876B4940D0081E5124D684E3B0B9118660FEB9E4"
        };
        private static readonly FieldInfo timer = AccessTools.Field(typeof(ButtonSfx), "m_sfxTimer");
        private static readonly MethodInfo play = AccessTools.DeclaredMethod(typeof(ButtonSfx), "PlaySfx");
        private static readonly MethodInfo buttonSound = AccessTools.DeclaredMethod(typeof(TabAudioPatch), nameof(CreateButtonSound));
        private static bool pending, enabled;
        private static bool? rewritten;
        [ThreadStatic] private static Scope? scope;
        [ThreadStatic] private static TabAudioTicket? ticket;

        internal static void Prepare()
        {
            if (pending || enabled) return;
            pending = enabled = false;
            rewritten = null;

            if (Guard.KnownServerBuild())
            {
                CompatibilityInstaller.Info("UI.TabAudio: client-only patch; inactive on dedicated server.");
                return;
            }

            Guard.Build(typeof(InventoryGui).Assembly, GameHashes);
            Guard.Build(typeof(ButtonSfx).Assembly, GuiHashes);

            var harmony = new Harmony(Owner);
            foreach (string tab in new[] { "Craft", "Upgrade" })
                harmony.Patch(Guard.Method(typeof(InventoryGui), "OnTab" + tab + "Pressed", typeof(void)),
                    prefix: Guard.Hook(typeof(TabAudioPatch), "Begin" + tab, Priority.First),
                    finalizer: Guard.Hook(typeof(TabAudioPatch), nameof(End)));

            harmony.Patch(Guard.Method(typeof(EffectList), "Create", typeof(GameObject[]),
                typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(float), typeof(int), typeof(ZDOID)),
                postfix: Guard.Hook(typeof(TabAudioPatch), nameof(Created), Priority.Last));

            harmony.Patch(Guard.Method(typeof(ButtonSfx), "PlaySfx", typeof(void), typeof(GameObject), typeof(GameObject)),
                transpiler: Guard.Hook(typeof(TabAudioPatch), nameof(Rewrite)));

            pending = true;
            CompatibilityInstaller.Info("UI.TabAudio: audited builds verified; awaiting transpiler verification.");
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;

            if (rewritten != true)
            {
                new Harmony(Owner).UnpatchSelf();
                scope = null; ticket = null;
                CompatibilityInstaller.Error("UI.TabAudio: NOT APPLIED; button sound IL did not match; original UI behavior retained.");
                return;
            }

            enabled = true;
            try
            {
                InstallVcp();
            }
            catch (Exception error)
            {
                enabled = false;
                new Harmony(Owner).UnpatchSelf();
                scope = null; ticket = null;
                CompatibilityInstaller.Error("UI.TabAudio: NOT APPLIED; VCP click compatibility could not be verified. " + error);
                return;
            }

            CompatibilityInstaller.Info("UI.TabAudio: APPLIED; Craft/Upgrade retain the active-group click and skip only its identical button click; timers and vibration retained.");
        }

        private static void InstallVcp()
        {
            Patch[] matches = Harmony.GetPatchInfo(play)?.Prefixes.Where(p => p.owner == VcpOwner &&
                p.PatchMethod.DeclaringType?.FullName == "ValheimCommunityPatch.Patches.Correctness.DoubleClickSoundPatch" &&
                p.PatchMethod.Name == "PlaySfxPrefix").ToArray() ?? Array.Empty<Patch>();
            if (matches.Length == 0) return;

            if (matches.Length != 1) throw new InvalidOperationException("VCP registered the click prefix more than once.");
            MethodInfo hook = matches[0].PatchMethod;
            Guard.Build(hook.DeclaringType!.Assembly, VcpHashes);
            Guard.Method(hook.DeclaringType, hook.Name, typeof(void), typeof(ButtonSfx), typeof(GameObject).MakeByRefType(),
                typeof(GameObject).MakeByRefType(), typeof(bool));
            // VCP clears both prefabs. A matching ticket lets this group suppress only the duplicate audio.
            MethodInfo replacement = AccessTools.DeclaredMethod(typeof(TabAudioPatch), nameof(SkipVcpDuplicate));
            new Harmony(Owner).Patch(hook, prefix: new HarmonyMethod(replacement));
            if (Harmony.GetPatchInfo(hook)?.Prefixes.Count(p => p.owner == Owner && p.PatchMethod == replacement) != 1)
                throw new InvalidOperationException("The VCP click compatibility prefix did not install.");
        }

        private static bool SkipVcpDuplicate(ButtonSfx __0, ref GameObject __1) =>
            !enabled || ticket == null || !ticket.Matches(__0, __1, Time.frameCount);

        private static void BeginCraft(InventoryGui __instance, out Scope? __state) => Begin(__instance, __instance.m_tabCraft, out __state);
        private static void BeginUpgrade(InventoryGui __instance, out Scope? __state) => Begin(__instance, __instance.m_tabUpgrade, out __state);
        private static void Begin(InventoryGui gui, Button button, out Scope? previous)
        {
            previous = scope;
            scope = enabled ? new Scope(gui, button) : null;
            ticket = null;
        }

        private static Exception? End(Exception? __exception, Scope? __state)
        {
            if (__exception != null) ticket = null;
            scope = __state;
            return __exception;
        }

        private static void Created(EffectList __instance, GameObject[] __result)
        {
            Scope? current = scope;
            if (!enabled || current == null || !current.Button ||
                !ReferenceEquals(__instance, current.Gui.m_setActiveGroupEffects) || __result == null) return;

            ButtonSfx[] components = current.Button.GetComponents<ButtonSfx>();
            if (components.Length != 1) return;

            ButtonSfx button = components[0];
            if (!button) return;

            GameObject prefab = button.m_sfxPrefab;
            // A ticket only describes this tab's click prefab, never its hover/selection sound.
            if (!prefab || prefab.name != "sfx_gui_button" ||
                ReferenceEquals(prefab, button.m_selectSfxPrefab) || ReferenceEquals(prefab, button.m_enterSfxPrefab)) return;

            EffectList.EffectData[] entries = __instance.m_effectPrefabs;

            if (entries == null || entries.Count(e => e != null && e.m_enabled && ReferenceEquals(e.m_prefab, prefab)) != 1) return;
            ZSFX[] sounds = prefab.GetComponents<ZSFX>();
            AudioSource original = prefab.GetComponent<AudioSource>();

            if (sounds.Length != 1 || !original || original.mute || original.loop ||
                sounds[0].m_audioClips == null || sounds[0].m_audioClips.Length != 1 || !sounds[0].m_audioClips[0] ||
                sounds[0].m_audioClips[0].name != "Ui_Click_01" || !sounds[0].m_playOnAwake ||
                sounds[0].m_maxConcurrentSources != 0 || sounds[0].m_minDelay != 0f || sounds[0].m_maxDelay != 0f) return;

            // Observe a successful Create result, rather than assuming the tab callback played a sound.
            int created = __result.Count(go =>
            {
                if (!go || go.name != prefab.name + "(Clone)") return false;
                ZSFX sound = go.GetComponent<ZSFX>();
                AudioSource audio = go.GetComponent<AudioSource>();

                return sound && audio && audio.enabled && !audio.mute && !audio.loop && MatchingSound(sounds[0], sound);
            });

            if (created == 1) ticket = new TabAudioTicket(button, prefab, Time.frameCount);
        }

        private static bool MatchingSound(ZSFX expected, ZSFX actual) => actual.m_audioClips != null &&
            actual.m_audioClips.Length == 1 && ReferenceEquals(actual.m_audioClips[0], expected.m_audioClips[0]) &&
            actual.m_playOnAwake == expected.m_playOnAwake && actual.m_maxConcurrentSources == expected.m_maxConcurrentSources &&
            actual.m_minDelay == expected.m_minDelay && actual.m_maxDelay == expected.m_maxDelay &&
            actual.m_minPitch == expected.m_minPitch && actual.m_maxPitch == expected.m_maxPitch &&
            actual.m_minVol == expected.m_minVol && actual.m_maxVol == expected.m_maxVol &&
            actual.m_fadeInDuration == expected.m_fadeInDuration;

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = instructions.ToList();
            List<CodeInstruction> code = original.Select(i => new CodeInstruction(i)).ToList();
            int[] creates = Enumerable.Range(0, code.Count).Where(i => Instantiate(code[i])).ToArray();
            // The first gated branch is audio; the second is vibration. Both reset their original timers.
            bool match = creates.Length == 2
                && Shape(code, creates[0] - 1, Is(OpCodes.Ldarg_1), Instantiate, Is(OpCodes.Pop), Loads(timer))
                && Shape(code, creates[1] - 1, Is(OpCodes.Ldarg_2), Instantiate);
            rewritten = match;
            if (!match)
            {
                Lost(ref enabled, "UI.TabAudio", "the button sound instantiation no longer matches");
                return original;
            }
            int index = creates[0];
            // Jumps to the instantiation now land on the inserted ButtonSfx argument.
            var instance = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[index]).MoveBlocksFrom(code[index]);
            code[index].opcode = OpCodes.Call;
            code[index].operand = buttonSound;
            code.Insert(index, instance);
            return code;
        }

        // Object.Instantiate<GameObject>(GameObject)
        private static bool Instantiate(CodeInstruction code) => code.operand is MethodInfo method && method.DeclaringType == typeof(Object)
            && method.Name == "Instantiate" && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[] { typeof(GameObject) })
            && method.GetParameters().Length == 1;

        private static GameObject CreateButtonSound(GameObject prefab, ButtonSfx button)
        {
            if (enabled && ticket != null && ticket.Consume(button, prefab, Time.frameCount))
                return null!; // The caller discards this return value, then resets the same sound timer.
            return Object.Instantiate(prefab);
        }

        private sealed class Scope
        {
            internal readonly InventoryGui Gui;
            internal readonly Button Button;
            internal Scope(InventoryGui gui, Button button) { Gui = gui; Button = button; }
        }
    }

    internal sealed class TabAudioTicket
    {
        private readonly ButtonSfx button;
        private readonly GameObject prefab;
        private readonly int frame;
        private bool consumed;
        internal TabAudioTicket(ButtonSfx button, GameObject prefab, int frame)
        { this.button = button; this.prefab = prefab; this.frame = frame; }
        internal bool Matches(ButtonSfx source, GameObject effect, int currentFrame) =>
            !consumed && frame == currentFrame && ReferenceEquals(button, source) && ReferenceEquals(prefab, effect);
        internal bool Consume(ButtonSfx source, GameObject effect, int currentFrame)
        {
            if (!Matches(source, effect, currentFrame)) return false;
            consumed = true;
            return true;
        }
    }
}
