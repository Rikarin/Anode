using System.Runtime.InteropServices;

namespace Anode.Dsp.MidiEffects.Scripter;

/// <summary>
///     P/Invoke surface of <c>an_script</c> (engine/native/an_script/include/an_script.h): QuickJS-ng with an arena per
///     instance, a time budget per call and a trace ring (docs/adr/0009-scripter.md). Strings are NUL-terminated UTF-8.
/// </summary>
internal static unsafe partial class ScriptNative {
    public const int Ok = 0;
    public const int Error = -1;
    public const int Interrupted = -2;
    public const int OutOfMemory = -3;
    public const int NoFunction = -4;
    public const int Invalid = -5;

    public const int EmitWidth = 8;
    public const int TraceLineMax = 256;
    public const nuint DefaultArenaBytes = 4 * 1024 * 1024;
    const string Library = "an_script";

    static readonly Lazy<bool> available = new(Probe);

    /// <summary>Whether the library loads (it is built by engine/native/build.sh and copied next to executables).</summary>
    public static bool Available => available.Value;

    [LibraryImport(Library, EntryPoint = "an_script_create")]
    public static partial int Create(
        nuint arenaBytes,
        ulong seed,
        int emitCapacity,
        int traceCapacity,
        out nint script
    );

    [LibraryImport(Library, EntryPoint = "an_script_destroy")]
    public static partial void Destroy(nint script);

    [LibraryImport(Library, EntryPoint = "an_script_compile")]
    public static partial int Compile(nint script, byte* source, int sourceLength, byte* name);

    [LibraryImport(Library, EntryPoint = "an_script_call")]
    public static partial int Call(nint script, byte* name, double* args, int argc, long budgetUs, double* result);

    [LibraryImport(Library, EntryPoint = "an_script_set_global")]
    public static partial int SetGlobal(nint script, byte* name, double value);

    [LibraryImport(Library, EntryPoint = "an_script_get_global")]
    public static partial int GetGlobal(nint script, byte* name, double* value);

    [LibraryImport(Library, EntryPoint = "an_script_eval_json")]
    public static partial int EvalJson(nint script, byte* expression, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "an_script_read_emitted")]
    public static partial int ReadEmitted(nint script, double* values, int capacity, int* dropped);

    [LibraryImport(Library, EntryPoint = "an_script_read_trace")]
    public static partial int ReadTrace(nint script, byte* buffer, int capacity, int* dropped);

    [LibraryImport(Library, EntryPoint = "an_script_last_error")]
    public static partial int LastError(nint script, byte* buffer, int capacity, int* line);

    [LibraryImport(Library, EntryPoint = "an_script_collect")]
    public static partial void Collect(nint script);

    [LibraryImport(Library, EntryPoint = "an_script_memory_used")]
    public static partial nuint MemoryUsed(nint script);

    [LibraryImport(Library, EntryPoint = "an_script_arena_bytes")]
    public static partial nuint ArenaBytes(nint script);

    [LibraryImport(Library, EntryPoint = "an_script_reset")]
    public static partial int Reset(nint script);

    [LibraryImport(Library, EntryPoint = "an_script_set_budget")]
    public static partial void SetBudget(nint script, long budgetUs);

    [LibraryImport(Library, EntryPoint = "an_script_check")]
    public static partial int Check(nint script);

    [LibraryImport(Library, EntryPoint = "an_script_version")]
    public static partial byte* Version();

    /// <summary>The pinned upstream ("quickjs-ng 0.17.0+6d46d07"), or null when the library does not load.</summary>
    public static string? VersionText => Available ? Marshal.PtrToStringUTF8((nint)Version()) : null;

    static bool Probe() {
        try {
            return Version() != null;
        } catch (DllNotFoundException) {
            return false;
        } catch (EntryPointNotFoundException) {
            return false;
        }
    }
}
