using System.Runtime.InteropServices;

namespace Anode.Audio.Native;

/// <summary>
///     Ableton Link through the <c>an_link</c> shim (engine/native/an_link/include/an_link.h; the Link SDK's
///     <c>abl_link</c>, GPL-2.0-or-later). One instance is one peer: it joins the session on the local network when
///     enabled. The audio thread captures and commits <see cref="AudioState" /> (real-time safe); every other thread uses
///     <see cref="AppState" />, under <see cref="AppGate" />. Times are microseconds on Link's clock
///     (<see cref="ClockMicros" />), which is the <see cref="Realtime.NowNanoseconds" /> clock divided by 1000 on macOS.
/// </summary>
public sealed unsafe partial class NativeLink : IDisposable {
    const string Library = "an_link";
    nint link;

    /// <summary>A peer at <paramref name="bpm" />, not yet enabled.</summary>
    public NativeLink(double bpm) {
        link = Create(Math.Clamp(bpm, 20, 999));
        if (link == 0) {
            throw new InvalidOperationException("an_link could not create a Link instance.");
        }

        AudioState = CreateState();
        AppState = CreateState();
    }

    /// <summary>True when the native library was built and can be loaded (see native/build.sh).</summary>
    public static bool IsAvailable {
        get {
            try {
                _ = Version;
                return true;
            } catch (DllNotFoundException) {
                return false;
            } catch (EntryPointNotFoundException) {
                return false;
            }
        }
    }

    /// <summary>The pinned upstream ("Link 4.1+…").</summary>
    public static string Version => Marshal.PtrToStringUTF8((nint)VersionNative()) ?? string.Empty;

    /// <summary>The audio thread's session state.</summary>
    public nint AudioState { get; }

    /// <summary>The other threads' session state (hold <see cref="AppGate" />).</summary>
    public nint AppState { get; }

    /// <summary>Guards <see cref="AppState" />.</summary>
    public Lock AppGate { get; } = new();

    public bool Enabled {
        get => IsEnabled(link);
        set => Enable(link, value);
    }

    public bool StartStopSync {
        get => IsStartStopSyncEnabled(link);
        set => EnableStartStopSync(link, value);
    }

    /// <summary>The other peers in the session.</summary>
    public int Peers => (int)Math.Min(int.MaxValue, NumPeers(link));

    public long ClockMicros => ClockMicrosNative(link);

    /// <summary>Audio thread: refreshes <see cref="AudioState" />.</summary>
    public void CaptureAudio() => CaptureAudioNative(link, AudioState);

    /// <summary>Audio thread: publishes what changed in <see cref="AudioState" />.</summary>
    public void CommitAudio() => CommitAudioNative(link, AudioState);

    /// <summary>Refreshes <see cref="AppState" /> (hold <see cref="AppGate" />).</summary>
    public void CaptureApp() => CaptureAppNative(link, AppState);

    /// <summary>Publishes <see cref="AppState" /> (hold <see cref="AppGate" />).</summary>
    public void CommitApp() => CommitAppNative(link, AppState);

    public static double Tempo(nint state) => TempoNative(state);

    public static void SetTempo(nint state, double bpm, long time) => SetTempoNative(state, bpm, time);

    public static double BeatAtTime(nint state, long time, double quantum) => BeatAtTimeNative(state, time, quantum);

    public static double PhaseAtTime(nint state, long time, double quantum) => PhaseAtTimeNative(state, time, quantum);

    public static long TimeAtBeat(nint state, double beat, double quantum) => TimeAtBeatNative(state, beat, quantum);

    public static void RequestBeatAtTime(nint state, double beat, long time, double quantum) =>
        RequestBeatAtTimeNative(state, beat, time, quantum);

    public static bool IsPlaying(nint state) => IsPlayingNative(state);

    public static void SetIsPlaying(nint state, bool playing, long time) => SetIsPlayingNative(state, playing, time);

    public static void SetIsPlayingAndRequestBeatAtTime(nint state, bool playing, long time, double beat, double quantum) =>
        SetIsPlayingAndRequestBeatNative(state, playing, time, beat, quantum);

    public void Dispose() {
        if (link == 0) {
            return;
        }

        Enable(link, false);
        Destroy(link);
        link = 0;
        DestroyState(AudioState);
        DestroyState(AppState);
    }

    [LibraryImport(Library, EntryPoint = "an_link_create")]
    private static partial nint Create(double bpm);

    [LibraryImport(Library, EntryPoint = "an_link_destroy")]
    private static partial void Destroy(nint link);

    [LibraryImport(Library, EntryPoint = "an_link_enable")]
    private static partial void Enable(nint link, [MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(Library, EntryPoint = "an_link_is_enabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsEnabled(nint link);

    [LibraryImport(Library, EntryPoint = "an_link_enable_start_stop_sync")]
    private static partial void EnableStartStopSync(nint link, [MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(Library, EntryPoint = "an_link_is_start_stop_sync_enabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsStartStopSyncEnabled(nint link);

    [LibraryImport(Library, EntryPoint = "an_link_num_peers")]
    private static partial ulong NumPeers(nint link);

    [LibraryImport(Library, EntryPoint = "an_link_clock_micros")]
    private static partial long ClockMicrosNative(nint link);

    [LibraryImport(Library, EntryPoint = "an_link_create_session_state")]
    private static partial nint CreateState();

    [LibraryImport(Library, EntryPoint = "an_link_destroy_session_state")]
    private static partial void DestroyState(nint state);

    [LibraryImport(Library, EntryPoint = "an_link_capture_audio_session_state")]
    private static partial void CaptureAudioNative(nint link, nint state);

    [LibraryImport(Library, EntryPoint = "an_link_commit_audio_session_state")]
    private static partial void CommitAudioNative(nint link, nint state);

    [LibraryImport(Library, EntryPoint = "an_link_capture_app_session_state")]
    private static partial void CaptureAppNative(nint link, nint state);

    [LibraryImport(Library, EntryPoint = "an_link_commit_app_session_state")]
    private static partial void CommitAppNative(nint link, nint state);

    [LibraryImport(Library, EntryPoint = "an_link_tempo")]
    private static partial double TempoNative(nint state);

    [LibraryImport(Library, EntryPoint = "an_link_set_tempo")]
    private static partial void SetTempoNative(nint state, double bpm, long time);

    [LibraryImport(Library, EntryPoint = "an_link_beat_at_time")]
    private static partial double BeatAtTimeNative(nint state, long time, double quantum);

    [LibraryImport(Library, EntryPoint = "an_link_phase_at_time")]
    private static partial double PhaseAtTimeNative(nint state, long time, double quantum);

    [LibraryImport(Library, EntryPoint = "an_link_time_at_beat")]
    private static partial long TimeAtBeatNative(nint state, double beat, double quantum);

    [LibraryImport(Library, EntryPoint = "an_link_request_beat_at_time")]
    private static partial void RequestBeatAtTimeNative(nint state, double beat, long time, double quantum);

    [LibraryImport(Library, EntryPoint = "an_link_is_playing")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsPlayingNative(nint state);

    [LibraryImport(Library, EntryPoint = "an_link_set_is_playing")]
    private static partial void SetIsPlayingNative(nint state, [MarshalAs(UnmanagedType.U1)] bool playing, long time);

    [LibraryImport(Library, EntryPoint = "an_link_set_is_playing_and_request_beat_at_time")]
    private static partial void SetIsPlayingAndRequestBeatNative(
        nint state,
        [MarshalAs(UnmanagedType.U1)] bool playing,
        long time,
        double beat,
        double quantum
    );

    [LibraryImport(Library, EntryPoint = "an_link_version")]
    private static partial byte* VersionNative();
}
