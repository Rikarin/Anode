using System.Runtime.InteropServices;

namespace Anode.Plugins.Native;

/// <summary>A MIDI message <see cref="Frame" /> frames into the block, as <c>an_plug_midi</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlugMidi {
    public int Frame;
    public byte Status;
    public byte Data1;
    public byte Data2;
    public byte Reserved;
}

/// <summary>P/Invoke surface of <c>an_plug</c> (engine/native/an_plug/include/an_plug.h).</summary>
internal static unsafe partial class PlugNative {
    public const int Vst3 = 1;
    public const int Au = 2;
    public const int Clap = 3;
    public const int EditorFloating = 1;
    const string Library = "an_plug";

    [LibraryImport(Library, EntryPoint = "an_plug_last_error")]
    private static partial byte* LastErrorRaw();

    public static string LastError => Marshal.PtrToStringUTF8((nint)LastErrorRaw()) ?? "unknown error";

    [LibraryImport(Library, EntryPoint = "an_plug_free")]
    public static partial void Free(void* p);

    [LibraryImport(Library, EntryPoint = "an_plug_format_supported")]
    public static partial int FormatSupported(int format);

    [LibraryImport(Library, EntryPoint = "an_plug_scan", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ScanRaw(int format, string? path, int instantiate, out byte* json);

    [LibraryImport(Library, EntryPoint = "an_plug_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(int format, string? path, string id, out nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_describe")]
    private static partial int DescribeRaw(nint plug, out byte* json);

    [LibraryImport(Library, EntryPoint = "an_plug_param_count")]
    public static partial int ParamCount(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_is_instrument")]
    public static partial int IsInstrument(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_activate")]
    public static partial int Activate(nint plug, double sampleRate, int maxFrames, int sidechain);

    [LibraryImport(Library, EntryPoint = "an_plug_deactivate")]
    public static partial void Deactivate(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_close")]
    public static partial void Close(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_process")]
    public static partial int Process(
        nint plug,
        float** input,
        float** sidechain,
        float** output,
        int frames,
        PlugMidi* midi,
        int midiCount
    );

    [LibraryImport(Library, EntryPoint = "an_plug_reset")]
    public static partial void Reset(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_param_set")]
    [SuppressGCTransition]
    public static partial void ParamSet(nint plug, int index, double value);

    [LibraryImport(Library, EntryPoint = "an_plug_param_get")]
    [SuppressGCTransition]
    public static partial double ParamGet(nint plug, int index);

    [LibraryImport(Library, EntryPoint = "an_plug_latency")]
    public static partial int Latency(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_state_save")]
    private static partial int StateSaveRaw(nint plug, out byte* data, out long size);

    [LibraryImport(Library, EntryPoint = "an_plug_state_load")]
    private static partial int StateLoadRaw(nint plug, byte* data, long size);

    [LibraryImport(Library, EntryPoint = "an_plug_set_event_callback")]
    public static partial void SetEventCallback(
        nint plug,
        delegate* unmanaged<nint, int, int, double, void> callback,
        nint user
    );

    [LibraryImport(Library, EntryPoint = "an_plug_idle")]
    public static partial void Idle(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_has_editor")]
    public static partial int HasEditor(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_editor_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int EditorOpen(nint plug, string title, long parentWindow, int x, int y, int flags);

    [LibraryImport(Library, EntryPoint = "an_plug_editor_close")]
    public static partial void EditorClose(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_editor_window")]
    public static partial int EditorWindow(nint plug);

    [LibraryImport(Library, EntryPoint = "an_plug_window_info")]
    public static partial int WindowInfo(
        long window,
        out int pid,
        out int x,
        out int y,
        out int width,
        out int height,
        out int layer,
        out int onscreen
    );

    [LibraryImport(Library, EntryPoint = "an_plug_app_window")]
    public static partial int AppWindow(int pid);

    [LibraryImport(Library, EntryPoint = "an_plug_window_z")]
    public static partial int WindowZ(long window);

    [LibraryImport(Library, EntryPoint = "an_plug_main_run")]
    public static partial void MainRun();

    [LibraryImport(Library, EntryPoint = "an_plug_main_quit")]
    public static partial void MainQuit();

    [LibraryImport(Library, EntryPoint = "an_plug_main_call")]
    public static partial void MainCall(delegate* unmanaged<nint, void> fn, nint ctx);

    [LibraryImport(Library, EntryPoint = "an_plug_redirect_stdout")]
    public static partial int RedirectStdout();

    [LibraryImport(Library, EntryPoint = "an_plug_flush_denormals")]
    public static partial void FlushDenormals();

    [LibraryImport(Library, EntryPoint = "an_plug_crash")]
    public static partial void Crash();

    /// <summary>an_plug_scan's JSON, or null with <paramref name="error" /> set.</summary>
    public static string? Scan(int format, string? path, bool instantiate, out string? error) {
        if (ScanRaw(format, path, instantiate ? 1 : 0, out var json) != 0) {
            error = LastError;
            return null;
        }

        error = null;
        return TakeString(json);
    }

    public static string Describe(nint plug) {
        DescribeRaw(plug, out var json);
        return TakeString(json);
    }

    public static byte[]? SaveState(nint plug, out string? error) {
        if (StateSaveRaw(plug, out var data, out var size) != 0) {
            error = LastError;
            return null;
        }

        error = null;
        var bytes = new ReadOnlySpan<byte>(data, (int)size).ToArray();
        Free(data);
        return bytes;
    }

    public static bool LoadState(nint plug, ReadOnlySpan<byte> state, out string? error) {
        fixed (byte* p = state) {
            if (StateLoadRaw(plug, p, state.Length) != 0) {
                error = LastError;
                return false;
            }
        }

        error = null;
        return true;
    }

    static string TakeString(byte* p) {
        var s = Marshal.PtrToStringUTF8((nint)p) ?? "";
        Free(p);
        return s;
    }

    /// <summary>True when <c>an_plug</c> can be loaded (native/build.sh has run).</summary>
    public static bool Available {
        get {
            try {
                _ = FormatSupported(Vst3);
                return true;
            } catch (DllNotFoundException) {
                return false;
            } catch (EntryPointNotFoundException) {
                return false;
            }
        }
    }
}
