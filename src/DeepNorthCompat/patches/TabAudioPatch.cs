using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DeepNorthCompat
{
    internal static class TabAudioPatch
    {
        internal const string Owner = Plugin.Guid + ".UI.TabAudio";
        private const string GameHash = "25A0A107DCE4D834C44C2B72D0EAFD5CB7793933BDA81816ACCFA1EA9543DACE";
        private const string GuiHash = "52846FCAE0C535D733711C13FDB8F214D303603B5DBF23D05B1ED6A8E475C9DD";
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

            Guard.Build(typeof(InventoryGui).Assembly, GameHash);
            Guard.Build(typeof(ButtonSfx).Assembly, GuiHash);

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
            CompatibilityInstaller.Info("UI.TabAudio: 1.0.17 builds verified; awaiting transpiler verification.");
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
            CompatibilityInstaller.Info("UI.TabAudio: APPLIED; Craft/Upgrade retain the active-group click and skip only its identical button click; timers and vibration retained.");
        }

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
            int[] creates = Enumerable.Range(0, code.Count).Where(i => code[i].operand is MethodInfo method &&
                method.DeclaringType == typeof(Object) && method.Name == "Instantiate" && method.IsGenericMethod &&
                method.GetGenericArguments().SequenceEqual(new[] { typeof(GameObject) }) && method.GetParameters().Length == 1).ToArray();
            // The first gated branch is audio; the second is vibration. Both reset their original timers.
            FieldInfo timer = AccessTools.Field(typeof(ButtonSfx), "m_sfxTimer");
            bool match = creates.Length == 2 && creates[0] >= 1 && creates[1] >= 1 &&
                code[creates[0] - 1].opcode == OpCodes.Ldarg_1 && code[creates[1] - 1].opcode == OpCodes.Ldarg_2 &&
                code.Skip(creates[0] + 1).Take(2).Select(i => i.opcode).SequenceEqual(new[] { OpCodes.Pop, OpCodes.Ldsfld }) &&
                Equals(code[creates[0] + 2].operand, timer);
            rewritten = match;
            if (!match) return original;
            int index = creates[0];
            var instance = new CodeInstruction(OpCodes.Ldarg_0);
            instance.labels.AddRange(code[index].labels); code[index].labels.Clear();
            instance.blocks.AddRange(code[index].blocks); code[index].blocks.Clear();
            code[index].opcode = OpCodes.Call;
            code[index].operand = AccessTools.Method(typeof(TabAudioPatch), nameof(CreateButtonSound));
            code.Insert(index, instance);
            return code;
        }

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
        internal bool Consume(ButtonSfx source, GameObject effect, int currentFrame)
        {
            if (consumed || frame != currentFrame || !ReferenceEquals(button, source) || !ReferenceEquals(prefab, effect)) return false;
            consumed = true;
            return true;
        }
    }
}
