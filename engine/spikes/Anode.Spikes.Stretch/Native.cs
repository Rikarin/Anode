using System.Runtime.InteropServices;

namespace Anode.Spikes.Stretch;

/// <summary>P/Invoke surface of <c>an_stretch</c> (engine/native/an_stretch/include/an_stretch.h).</summary>
static unsafe partial class Native {
    const string Library = "an_stretch";

    [LibraryImport(Library, EntryPoint = "an_stretch_create")]
    internal static partial int Create(int channels, double sampleRate, int preset, int split, out nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_create_custom")]
    internal static partial int CreateCustom(
        int channels,
        double sampleRate,
        int blockFrames,
        int intervalFrames,
        int split,
        out nint stretch
    );

    [LibraryImport(Library, EntryPoint = "an_stretch_destroy")]
    internal static partial void Destroy(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_reset")]
    internal static partial void Reset(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_set_transpose_semitones")]
    internal static partial void SetTransposeSemitones(nint stretch, double semitones, double tonalityLimitHz);

    [LibraryImport(Library, EntryPoint = "an_stretch_set_formant_factor")]
    internal static partial void SetFormantFactor(nint stretch, double factor, int compensatePitch);

    [LibraryImport(Library, EntryPoint = "an_stretch_set_formant_base")]
    internal static partial void SetFormantBase(nint stretch, double baseHz);

    [LibraryImport(Library, EntryPoint = "an_stretch_process")]
    internal static partial void Process(
        nint stretch,
        float** input,
        int inputFrames,
        float** output,
        int outputFrames
    );

    [LibraryImport(Library, EntryPoint = "an_stretch_output_seek")]
    internal static partial void OutputSeek(nint stretch, float** input, int inputFrames);

    [LibraryImport(Library, EntryPoint = "an_stretch_output_seek_length")]
    internal static partial int OutputSeekLength(nint stretch, double playbackRate);

    [LibraryImport(Library, EntryPoint = "an_stretch_seek_length")]
    internal static partial int SeekLength(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_flush")]
    internal static partial void Flush(nint stretch, float** output, int outputFrames, double playbackRate);

    [LibraryImport(Library, EntryPoint = "an_stretch_input_latency")]
    internal static partial int InputLatency(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_output_latency")]
    internal static partial int OutputLatency(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_block_frames")]
    internal static partial int BlockFrames(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_interval_frames")]
    internal static partial int IntervalFrames(nint stretch);

    [LibraryImport(Library, EntryPoint = "an_stretch_version")]
    internal static partial byte* Version();

    [LibraryImport(Library, EntryPoint = "an_stretch_last_error")]
    internal static partial byte* LastError();

    /// <summary>
    ///     Loads <paramref name="variant" /> (e.g. <c>an_stretch_noaccel</c>, the build without Apple's Accelerate) in
    ///     place of <c>an_stretch</c>. Call before the first native call.
    /// </summary>
    public static void UseVariant(string variant) =>
        NativeLibrary.SetDllImportResolver(
            typeof(Native).Assembly,
            (name, assembly, path) => name == Library ? NativeLibrary.Load(variant, assembly, path) : 0
        );

    public static string VersionText => Marshal.PtrToStringUTF8((nint)Version()) ?? "?";

    internal static string LastErrorText => Marshal.PtrToStringUTF8((nint)LastError()) ?? "?";
}

enum Preset {
    Default = 0,
    Cheaper = 1
}

/// <summary>
///     A stretcher configuration: an upstream preset, or explicit block and interval sizes (frames) when
///     <paramref name="Block" /> is set, with or without split computation.
/// </summary>
sealed record StretchConfig(Preset Preset, bool Split, int Block = 0, int Interval = 0) {
    public string Name =>
        (Block > 0 ? $"custom-{Block}-{Interval}" : Preset == Preset.Default ? "default" : "cheaper")
        + (Split ? "+split" : string.Empty);
}

/// <summary>Planar float audio in pinned arrays, so pointers can be handed to the shim.</summary>
sealed unsafe class Planar {
    public Planar(int channels, int frames) {
        Data = new float[channels][];
        for (var c = 0; c < channels; c++) {
            Data[c] = GC.AllocateArray<float>(Math.Max(frames, 1), true);
        }

        Frames = frames;
    }

    public float[][] Data { get; }
    public int Channels => Data.Length;
    public int Frames { get; }

    public float* At(int channel, long frame) =>
        (float*)Marshal.UnsafeAddrOfPinnedArrayElement(Data[channel], 0) + frame;

    /// <summary>A copy padded with <paramref name="padFrames" /> of silence.</summary>
    public static Planar From(float[][] audio, int padFrames) {
        var result = new Planar(audio.Length, audio[0].Length + padFrames);
        for (var c = 0; c < audio.Length; c++) {
            audio[c].CopyTo(result.Data[c], 0);
        }

        return result;
    }
}

sealed unsafe class Stretcher : IDisposable {
    readonly nint handle;

    public Stretcher(int channels, double sampleRate, StretchConfig config) {
        var split = config.Split ? 1 : 0;
        var status = config.Block > 0
            ? Native.CreateCustom(channels, sampleRate, config.Block, config.Interval, split, out handle)
            : Native.Create(channels, sampleRate, (int)config.Preset, split, out handle);
        if (status != 0) {
            throw new InvalidOperationException(Native.LastErrorText);
        }

        Channels = channels;
        SampleRate = sampleRate;
    }

    public int Channels { get; }
    public double SampleRate { get; }
    public int InputLatency => Native.InputLatency(handle);
    public int OutputLatency => Native.OutputLatency(handle);
    public int BlockFrames => Native.BlockFrames(handle);
    public int IntervalFrames => Native.IntervalFrames(handle);
    public int SeekLength => Native.SeekLength(handle);

    public void Dispose() => Native.Destroy(handle);

    public void Reset() => Native.Reset(handle);

    public void SetTranspose(double semitones) => Native.SetTransposeSemitones(handle, semitones, 0);

    /// <summary>Formant factor 1 with compensation on keeps the formants in place under a transposition.</summary>
    public void SetFormant(double factor, bool compensatePitch, double baseHz) {
        Native.SetFormantFactor(handle, factor, compensatePitch ? 1 : 0);
        Native.SetFormantBase(handle, baseHz);
    }

    public int OutputSeekLength(double playbackRate) => Native.OutputSeekLength(handle, playbackRate);

    public void OutputSeek(Planar input, int frames) {
        var ins = stackalloc float*[Channels];
        Fill(ins, input, 0);
        Native.OutputSeek(handle, ins, frames);
    }

    /// <summary>Processes one call and returns its duration in <see cref="System.Diagnostics.Stopwatch" /> ticks.</summary>
    public long Process(
        Planar input,
        long inputOffset,
        int inputFrames,
        Planar output,
        long outputOffset,
        int outputFrames
    ) {
        var ins = stackalloc float*[Channels];
        var outs = stackalloc float*[Channels];
        Fill(ins, input, inputOffset);
        Fill(outs, output, outputOffset);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Native.Process(handle, ins, inputFrames, outs, outputFrames);
        return System.Diagnostics.Stopwatch.GetTimestamp() - start;
    }

    public long Flush(Planar output, long outputOffset, int outputFrames, double playbackRate) {
        var outs = stackalloc float*[Channels];
        Fill(outs, output, outputOffset);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Native.Flush(handle, outs, outputFrames, playbackRate);
        return System.Diagnostics.Stopwatch.GetTimestamp() - start;
    }

    static void Fill(float** pointers, Planar audio, long offset) {
        for (var c = 0; c < audio.Channels; c++) {
            pointers[c] = audio.At(c, offset);
        }
    }
}
