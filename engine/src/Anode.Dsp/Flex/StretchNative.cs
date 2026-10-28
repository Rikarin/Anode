using System.Runtime.InteropServices;

namespace Anode.Dsp.Flex;

/// <summary>
///     P/Invoke surface of <c>an_stretch</c> (engine/native/an_stretch/include/an_stretch.h), Signalsmith Stretch behind
///     a C ABI (spike S5, docs/spikes/S5-time-stretch.md). Audio is planar: one pointer per channel.
/// </summary>
internal static unsafe partial class StretchNative {
    public const int PresetDefault = 0;
    const string Library = "an_stretch";

    static readonly Lazy<bool> available = new(Probe);

    /// <summary>Whether the library loads (it is built by engine/native/build.sh and copied next to executables).</summary>
    public static bool Available => available.Value;

    [LibraryImport(Library, EntryPoint = "an_stretch_create")]
    public static partial int Create(int channels, double sampleRate, int preset, int split, out nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_create_custom")]
    public static partial int CreateCustom(
        int channels,
        double sampleRate,
        int blockFrames,
        int intervalFrames,
        int split,
        out nint stretch
    );

    [LibraryImport(Library, EntryPoint = "an_stretch_destroy")]
    public static partial void Destroy(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_set_transpose_semitones")]
    public static partial void SetTransposeSemitones(nint stretch, double semitones, double tonalityLimitHz);

    [LibraryImport(Library, EntryPoint = "an_stretch_set_formant_factor")]
    public static partial void SetFormantFactor(nint stretch, double factor, int compensatePitch);

    [LibraryImport(Library, EntryPoint = "an_stretch_process")]
    public static partial void Process(nint stretch, float** input, int inputFrames, float** output, int outputFrames);

    [LibraryImport(Library, EntryPoint = "an_stretch_seek")]
    public static partial void Seek(nint stretch, float** input, int inputFrames, double playbackRate);

    [LibraryImport(Library, EntryPoint = "an_stretch_seek_length")]
    public static partial int SeekLength(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_output_seek")]
    public static partial void OutputSeek(nint stretch, float** input, int inputFrames);

    [LibraryImport(Library, EntryPoint = "an_stretch_output_seek_length")]
    public static partial int OutputSeekLength(nint stretch, double playbackRate);

    [LibraryImport(Library, EntryPoint = "an_stretch_input_latency")]
    public static partial int InputLatency(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_output_latency")]
    public static partial int OutputLatency(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_interval_frames")]
    public static partial int IntervalFrames(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_version")]
    public static partial byte* Version();

    [LibraryImport(Library, EntryPoint = "an_stretch_last_error")]
    public static partial byte* LastError();

    public static string LastErrorText => Marshal.PtrToStringUTF8((nint)LastError()) ?? "unknown error";

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
