using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string Runtime = "mono-2.0-bdwgc.dll";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_set_dirs(string assemblyDirectory, string configDirectory);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_set_assemblies_path(string path);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SpanHashCall(IntPtr span);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float FloatCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float RangeCall(float min, float max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ComponentCall(IntPtr component);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyCall(IntPtr value, float delay);
    private static readonly IntCall Offset = () => 0;
    private static readonly IntCall MainThread = () => 1;
    private static readonly SpanHashCall AnimationHash = _ => 0;
    private static readonly FloatCall RandomValue = () => 0.5f;
    private static readonly RangeCall RandomRange = (min, max) => (min + max) * 0.5f;
    private static readonly ComponentCall ComponentObject = _ => IntPtr.Zero;
    private static readonly DestroyCall DestroyObject = (_, __) => { };
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_config_parse(string? config);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mono_domain_assembly_open(IntPtr domain, string path);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, IntPtr argv);
    [DllImport(Runtime, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_jit_cleanup(IntPtr domain);

    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the offline test executable path.");
        string game = Environment.GetEnvironmentVariable("DEEPNORTHCOMPAT_VALHEIM_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Valheim");
        if (!SetDllDirectory(Path.Combine(game, "MonoBleedingEdge", "EmbedRuntime")))
            throw new InvalidOperationException("Could not select the configured Valheim Mono runtime.");
        mono_set_dirs(Path.Combine(game, @"valheim_Data\Managed"), Path.Combine(game, @"MonoBleedingEdge\etc"));
        mono_set_assemblies_path(Path.Combine(game, @"valheim_Data\Managed"));
        mono_config_parse(null);
        IntPtr domain = mono_jit_init_version("DeepNorthCompatOfflineValidation", "v4.0.30319");
        if (domain == IntPtr.Zero) throw new InvalidOperationException("Game Mono runtime did not initialize.");
        // Offline fixture only. No Unity scene or native game objects are created.
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        mono_add_internal_call("UnityEngine.Object::CurrentThreadIsMainThread", Marshal.GetFunctionPointerForDelegate(MainThread));
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(AnimationHash));
        mono_add_internal_call("UnityEngine.Random::get_value", Marshal.GetFunctionPointerForDelegate(RandomValue));
        mono_add_internal_call("UnityEngine.Random::Range", Marshal.GetFunctionPointerForDelegate(RandomRange));
        mono_add_internal_call("UnityEngine.Component::get_gameObject_Injected", Marshal.GetFunctionPointerForDelegate(ComponentObject));
        mono_add_internal_call("UnityEngine.Time::get_frameCount", Marshal.GetFunctionPointerForDelegate(MainThread));
        mono_add_internal_call("UnityEngine.Object::Destroy_Injected", Marshal.GetFunctionPointerForDelegate(DestroyObject));
        IntPtr argument = IntPtr.Zero, argv = IntPtr.Zero;
        try
        {
            string test = Path.GetFullPath(args[0]);
            IntPtr assembly = mono_domain_assembly_open(domain, test);
            if (assembly == IntPtr.Zero) throw new InvalidOperationException("Mono could not open offline tests.");
            argument = Marshal.StringToHGlobalAnsi(test);
            argv = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(argv, argument);
            return mono_jit_exec(domain, assembly, 1, argv);
        }
        finally
        {
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
            if (argument != IntPtr.Zero) Marshal.FreeHGlobal(argument);
            mono_jit_cleanup(domain);
        }
    }
}
