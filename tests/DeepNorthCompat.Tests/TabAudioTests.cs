using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using DeepNorthCompat;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class TabAudioTests
{
    private static readonly Type Patch = typeof(Plugin).Assembly.GetType("DeepNorthCompat.TabAudioPatch", true)!;
    private static readonly Type Ticket = typeof(Plugin).Assembly.GetType("DeepNorthCompat.TabAudioTicket", true)!;
    private const string Ours = "DeepNorthCompat.UI.TabAudio";
    private const string Fixture = "DeepNorthCompat.Tests.TabAudio";
    private static readonly List<GameObject> created = new List<GameObject>();
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly MethodInfo play = AccessTools.Method(typeof(ButtonSfx), "PlaySfx");
    private static readonly MethodInfo instantiate = typeof(Object).GetMethods().Single(m => m.Name == "Instantiate" &&
        m.IsGenericMethodDefinition && m.GetParameters().Length == 1).MakeGenericMethod(typeof(GameObject));

    internal static void Run(Action<string, Action> test)
    {
        void Case(string name, Action action) => test("tab audio " + name, () =>
        {
            Cleanup();
            try { action(); } finally { Cleanup(); }
        });
        Case("rewrites only sound instantiation and preserves timer and vibration instructions", () =>
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(play);
            List<CodeInstruction> result = Rewrite(original).ToList();
            Check(result.Count == original.Count + 1, "only one extra instance argument");
            int index = original.FindIndex(i => i.Calls(instantiate));
            Check(index >= 0 && result[index].opcode == OpCodes.Ldarg_0, "instance supplied to scoped helper");
            Check(((MethodInfo)result[index + 1].operand).DeclaringType == Patch, "only audio call replaced");
            result.RemoveAt(index); result[index] = original[index];
            Check(Same(original, result), "all branches, timer resets, vibration and labels preserved");
        });
        Case("rejects missing or ambiguous audio calls without changing the source IL", () =>
        {
            foreach (bool extra in new[] { false, true })
            {
                List<CodeInstruction> source = PatchProcessor.GetOriginalInstructions(play);
                int index = source.FindIndex(i => i.Calls(instantiate));
                if (extra) source.Insert(index, new CodeInstruction(source[index]));
                else source.RemoveAt(index);
                Check(Same(source, Rewrite(source).ToList()), "mismatch retains original instructions");
            }
        });
        Case("ticket requires the same component, prefab and frame and consumes only once", () =>
        {
            var a = Fake<ButtonSfx>(1); var b = Fake<ButtonSfx>(2);
            var sound = Fake<GameObject>(3); var other = Fake<GameObject>(4);
            var token = NewTicket(a, sound, 10);
            Check(!Consume(token, b, sound, 10) && !Consume(token, a, other, 10) && !Consume(token, a, sound, 11), "unrelated playback retained");
            Check(Consume(token, a, sound, 10) && !Consume(token, a, sound, 10), "one redundant sound skipped");
        });
        Case("actual patched button path keeps both timer gates and vibration", () =>
        {
            Prepare(); Verify();
            new Harmony(Fixture).Patch(instantiate, prefix: Hook(nameof(Instantiate)));
            var button = Fake<ButtonSfx>(1); var sound = Fake<GameObject>(2); var vibration = Fake<GameObject>(3);
            SetTimer("m_sfxTimer", -100); SetTimer("m_vibrationTimer", -100);
            AccessTools.Field(Patch, "ticket").SetValue(null, NewTicket(button, sound, Time.frameCount));
            play.Invoke(button, new object[] { sound, vibration });
            Check(created.SequenceEqual(new[] { vibration }), "duplicate audio skipped; vibration instantiated normally");
            Check(LastFrame("m_sfxTimer") == Time.frameCount && LastFrame("m_vibrationTimer") == Time.frameCount, "both original timers reset");
            play.Invoke(button, new object[] { sound, vibration });
            Check(created.Count == 1, "same-frame audio and vibration gates still reject");
            SetTimer("m_sfxTimer", -100); SetTimer("m_vibrationTimer", -100);
            play.Invoke(button, new object[] { sound, vibration });
            Check(created.SequenceEqual(new[] { vibration, sound, vibration }), "consumed ticket leaves subsequent calls unchanged");
        });
        Case("missing ticket and other components keep ordinary sound and vibration", () =>
        {
            Prepare(); Verify();
            new Harmony(Fixture).Patch(instantiate, prefix: Hook(nameof(Instantiate)));
            var a = Fake<ButtonSfx>(1); var b = Fake<ButtonSfx>(2); var sound = Fake<GameObject>(3); var vibration = Fake<GameObject>(4);
            AccessTools.Field(Patch, "ticket").SetValue(null, NewTicket(a, sound, Time.frameCount));
            SetTimer("m_sfxTimer", -100); SetTimer("m_vibrationTimer", -100);
            play.Invoke(b, new object[] { sound, vibration });
            Check(created.SequenceEqual(new[] { sound, vibration }), "unrelated component unaffected");
            AccessTools.Field(Patch, "ticket").SetValue(null, null);
            SetTimer("m_sfxTimer", -100); SetTimer("m_vibrationTimer", -100);
            play.Invoke(a, new object[] { sound, vibration });
            Check(created.SequenceEqual(new[] { sound, vibration, sound, vibration }), "ordinary feedback retained");
        });
        Case("retains foreign registrations and removes only its own group on failed verification", () =>
        {
            new Harmony(Fixture).Patch(play, prefix: Hook(nameof(OtherPrefix)));
            Patch foreign = Harmony.GetPatchInfo(play)!.Prefixes.Single(p => p.owner == Fixture);
            Prepare();
            Check(Harmony.GetPatchInfo(play)!.Prefixes.Contains(foreign), "foreign registration retained");
            Rewrite(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray();
            Verify();
            Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Ours)), "entire group removed on mismatch");
            Check(Harmony.GetPatchInfo(play)!.Prefixes.Contains(foreign), "foreign hook survives rollback");
        });
        Case("spawned sound overrides cannot qualify as the identical retained click", () =>
        {
            var expected = Fake<ZSFX>(1); var actual = Fake<ZSFX>(2); var clip = Fake<AudioClip>(3);
            foreach (var sound in new[] { expected, actual })
            {
                sound.m_audioClips = new[] { clip }; sound.m_playOnAwake = true;
                sound.m_minPitch = sound.m_maxPitch = 1f; sound.m_minVol = sound.m_maxVol = 0.6f;
            }
            bool Match() => (bool)AccessTools.Method(Patch, "MatchingSound").Invoke(null, new object[] { expected, actual });
            Check(Match(), "same sound accepted");
            foreach (string field in new[] { "m_playOnAwake", "m_maxConcurrentSources", "m_minDelay", "m_maxDelay", "m_minPitch", "m_maxPitch", "m_minVol", "m_maxVol", "m_fadeInDuration" })
            {
                FieldInfo f = AccessTools.Field(typeof(ZSFX), field); object previous = f.GetValue(actual);
                f.SetValue(actual, f.FieldType == typeof(bool) ? (object)false : f.FieldType == typeof(int) ? (object)1 : 0.5f);
                Check(!Match(), field + " override retained"); f.SetValue(actual, previous);
            }
            actual.m_audioClips = new[] { Fake<AudioClip>(4) }; Check(!Match(), "different clip retained");
            actual.m_audioClips = null!; Check(!Match(), "missing clip retained");
        });
        Case("repeated installation does not duplicate its transpiler or registrations", () =>
        {
            Prepare(); Verify();
            string[] Snapshot() => Harmony.GetAllPatchedMethods().SelectMany(m =>
            {
                Patches p = Harmony.GetPatchInfo(m)!;
                return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers)
                    .Where(h => h.owner == Ours).Select(h => m.Name + ":" + h.PatchMethod.Name + ":" + h.index);
            }).OrderBy(s => s).ToArray();
            string[] before = Snapshot(); Prepare(); Verify();
            Check(before.Length == 6 && before.SequenceEqual(Snapshot()), "same six registrations retained");
        });
        Case("finalizer restores nested scope and preserves the original exception", () =>
        {
            FieldInfo scope = AccessTools.Field(Patch, "scope");
            Type type = Patch.GetNestedType("Scope", BindingFlags.NonPublic)!;
            object previous = FormatterServices.GetUninitializedObject(type);
            scope.SetValue(null, FormatterServices.GetUninitializedObject(type));
            var error = new InvalidOperationException("fixture");
            object? result = AccessTools.Method(Patch, "End").Invoke(null, new[] { error, previous });
            Check(ReferenceEquals(result, error) && ReferenceEquals(scope.GetValue(null), previous), "exception and nesting retained");
        });
    }

    internal static void RunServer(Action<string, Action> test) => test("tab audio dedicated role remains inactive", () =>
    {
        Cleanup(); Prepare(); Verify();
        Check(!(bool)AccessTools.Field(Patch, "enabled").GetValue(null), "server never enables UI behavior");
        Check(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Ours)), "server has no UI hooks");
        Cleanup();
    });

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> code) =>
        (IEnumerable<CodeInstruction>)AccessTools.Method(Patch,"Rewrite").Invoke(null,new object[]{code});
    private static void Prepare() => AccessTools.Method(Patch,"Prepare").Invoke(null,null);
    private static void Verify() => AccessTools.Method(Patch,"Verify").Invoke(null,null);
    private static object NewTicket(ButtonSfx source,GameObject sound,int frame) =>
        Activator.CreateInstance(Ticket,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{source,sound,frame},null)!;
    private static bool Consume(object ticket,ButtonSfx source,GameObject sound,int frame) =>
        (bool)AccessTools.Method(Ticket,"Consume").Invoke(ticket,new object[]{source,sound,frame});
    private static bool Same(IList<CodeInstruction> a, IList<CodeInstruction> b) => a.Count == b.Count && a.Zip(b,
        (x, y) => x.opcode == y.opcode && Equals(x.operand, y.operand) && x.labels.SequenceEqual(y.labels) && x.blocks.SequenceEqual(y.blocks)).All(v => v);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(TabAudioTests), name));
    private static bool Instantiate(GameObject __0, ref GameObject __result) { created.Add(__0); __result = __0; return false; }
    private static void OtherPrefix() { }
    private static void SetTimer(string name, int last)
    {
        var timer = new SfxTimer(); AccessTools.Field(typeof(SfxTimer), "m_lastTriggerFrame").SetValue(timer, last);
        AccessTools.Field(typeof(ButtonSfx), name).SetValue(null, timer);
    }
    private static int LastFrame(string name) => (int)AccessTools.Field(typeof(SfxTimer), "m_lastTriggerFrame").GetValue(AccessTools.Field(typeof(ButtonSfx), name).GetValue(null));
    private static T Fake<T>(int id) where T : Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt32(pointer, id);
        AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer); return value;
    }
    private static void Cleanup()
    {
        new Harmony(Fixture).UnpatchSelf(); new Harmony(Ours).UnpatchSelf();
        foreach (string field in new[] { "scope", "ticket" }) AccessTools.Field(Patch, field).SetValue(null, null);
        AccessTools.Field(Patch, "enabled").SetValue(null, false);
        AccessTools.Field(Patch, "pending").SetValue(null, false);
        foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer); pointers.Clear(); created.Clear();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
