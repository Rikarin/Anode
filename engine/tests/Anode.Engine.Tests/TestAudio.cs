using Anode.Engine.Recording;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Anode.Engine.Tests;

/// <summary>Test fixtures: WAV files with known samples, segments, and an offline driver for the audio callback.</summary>
static class TestAudio {
    /// <summary>
    ///     The 24-bit sample at <paramref name="frame" /> of a test file seeded <paramref name="seed" /> (spike S2's
    ///     pattern).
    /// </summary>
    public static int Pattern(long frame, int seed) => (int)((frame * 3 + seed * 7919L) & 0x7F_FFFF) - 0x40_0000;

    /// <summary>The decoded float of <see cref="Pattern" />, as the engine decodes 24-bit PCM.</summary>
    public static float Sample(long frame, int seed) => Pattern(frame, seed) * (1f / 8_388_608f);

    /// <summary>Writes a 24-bit WAV whose channel c of frame f is <see cref="Pattern" />(f, seed + c).</summary>
    public static string Write24(
        string directory,
        string name,
        int channels,
        long frames,
        int seed,
        int rate = 48_000
    ) {
        var path = Path.Combine(directory, name);
        using var writer = new WavWriter(path, channels, rate);
        var block = new float[4096 * channels];
        for (long f = 0; f < frames; f += 4096) {
            var n = (int)Math.Min(4096, frames - f);
            for (var i = 0; i < n; i++) {
                for (var c = 0; c < channels; c++) {
                    block[i * channels + c] = Sample(f + i, seed + c);
                }
            }

            writer.Write(block.AsSpan(0, n * channels));
        }

        return path;
    }

    /// <summary>Writes a WAV of raw little-endian samples with a JUNK chunk before the data (as BWF writers do).</summary>
    public static string WriteRaw(
        string directory,
        string name,
        short format,
        int bits,
        int channels,
        byte[] data,
        int rate = 48_000
    ) {
        var path = Path.Combine(directory, name);
        using var stream = File.Create(path);
        Span<byte> h = stackalloc byte[12 + 24 + 8 + 100 + 8];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(h.Length - 8 + data.Length));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], format);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(rate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)bits);
        "JUNK"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 100);
        "data"u8.CopyTo(h[144..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[148..], (uint)data.Length);
        stream.Write(h);
        stream.Write(data);
        return path;
    }

    /// <summary>Reads a 24-bit WAV back as raw integers (interleaved).</summary>
    public static int[] Read24(string path, out int channels) {
        var bytes = File.ReadAllBytes(path);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
        Assert.Equal(bytes.Length - 44, size);
        var samples = new int[size / 3];
        for (var i = 0; i < samples.Length; i++) {
            samples[i] = Streaming.WavFormat.Int24(bytes, 44 + 3 * i);
        }

        return samples;
    }

    public static string TempDirectory() {
        var dir = Path.Combine(Path.GetTempPath(), "anode-tests", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static long segmentIds;

    /// <summary>Writes <paramref name="tracks" /> into a fresh segment (the server's side of a model load).</summary>
    public static SharedSegment Segment(
        IReadOnlyList<TrackModel> tracks,
        long version,
        long length = 48_000 * 60,
        IReadOnlyList<TempoMapPoint>? tempo = null,
        IReadOnlyList<CellModel>? cells = null
    ) =>
        SegmentWriter.Write(
            tracks,
            version,
            length,
            $"an-t{Environment.ProcessId % 100_000}",
            Interlocked.Increment(ref segmentIds),
            true,
            tempo: tempo,
            cells: cells
        );

    /// <summary>
    ///     A tempo point: from <paramref name="sample" /> at quarter <paramref name="quarter" />, <paramref name="bpm" />.
    /// </summary>
    public static TempoMapPoint Tempo(
        long sample,
        double quarter,
        double bpm,
        double slope = 0,
        double barQuarter = 0,
        short numerator = 4,
        short denominator = 4
    ) =>
        new() {
            Sample = sample,
            Quarter = quarter,
            Bpm = bpm,
            Slope = slope,
            BarQuarter = barQuarter,
            Numerator = numerator,
            Denominator = denominator
        };

    public static LoadModelMessage Load(
        SharedSegment segment,
        long version,
        bool replace,
        IReadOnlyList<EngineTrack> tracks,
        IReadOnlyList<EngineFile> files,
        string recordDirectory,
        EngineStrip? master = null
    ) =>
        new(
            new(segment.Name, segment.Length, version),
            replace,
            tracks,
            files,
            master ?? Unity,
            recordDirectory
        );

    public static EngineStrip Unity => new(1f, 0f, false, false);

    public static EngineTrack Track(
        int id,
        bool stereo = false,
        EngineStrip? strip = null,
        int input = -1,
        bool armed = false,
        bool monitoring = false
    ) =>
        new(id, input, stereo, armed, monitoring, strip ?? Unity);

    public static AudioPlaylistEntry Region(
        long start,
        long length,
        int file,
        long offset = 0,
        float gain = 1f,
        int fadeIn = 0,
        int fadeOut = 0
    ) =>
        new() {
            Start = start,
            FileOffset = offset,
            Length = length,
            FileId = file,
            Gain = gain,
            FadeIn = fadeIn,
            FadeOut = fadeOut
        };

    public static PlaybackEvent Click(long position, bool downbeat) =>
        new() {
            SamplePos = position,
            Track = TrackEntry.MetronomeTrack,
            Kind = PlaybackEventKind.Click,
            Data1 = downbeat ? (byte)1 : (byte)0,
            Value = 1f
        };
}

/// <summary>
///     Calls the audio callback from the test's thread, with native planar buffers, pumping the disk streamer before
///     each block as the disk thread would (deterministic: the streams are always full).
/// </summary>
sealed unsafe class OfflineDriver : IDisposable {
    readonly EngineController controller;
    readonly float** inputs;
    readonly float** outputs;
    readonly float* memory;

    public OfflineDriver(EngineController controller, int block, int inputChannels = 2, int outputChannels = 2) {
        this.controller = controller;
        Block = block;
        InputChannels = inputChannels;
        OutputChannels = outputChannels;
        memory = (float*)NativeMemory.AllocZeroed((nuint)((inputChannels + outputChannels) * block), sizeof(float));
        inputs = (float**)NativeMemory.Alloc((nuint)Math.Max(1, inputChannels), (nuint)sizeof(float*));
        outputs = (float**)NativeMemory.Alloc((nuint)outputChannels, (nuint)sizeof(float*));
        for (var c = 0; c < inputChannels; c++) {
            inputs[c] = memory + c * block;
        }

        for (var c = 0; c < outputChannels; c++) {
            outputs[c] = memory + (inputChannels + c) * block;
        }
    }

    public int Block { get; }

    public int InputChannels { get; }

    public int OutputChannels { get; }

    /// <summary>Fills input channel <paramref name="channel" /> for the next block.</summary>
    public Span<float> Input(int channel) => new(inputs[channel], Block);

    public ReadOnlySpan<float> Output(int channel) => new(outputs[channel], Block);

    /// <summary>Runs the disk streamer until every stream is full.</summary>
    public void Pump() {
        while (controller.Streamer.Pass()) { }
    }

    /// <summary>One callback (after pumping the disk).</summary>
    public void Process(bool pump = true) {
        if (pump) {
            Pump();
        }

        for (var c = 0; c < OutputChannels; c++) {
            new Span<float>(outputs[c], Block).Clear();
        }

        controller.Engine.Process(inputs, InputChannels, outputs, OutputChannels, Block);
    }

    /// <summary>Renders <paramref name="frames" /> frames (rounded up to whole blocks) into two arrays.</summary>
    public (float[] Left, float[] Right) Render(int frames) {
        frames = (frames + Block - 1) / Block * Block;
        var left = new float[frames];
        var right = new float[frames];
        for (var at = 0; at < frames; at += Block) {
            Process();
            Output(0).CopyTo(left.AsSpan(at));
            Output(1).CopyTo(right.AsSpan(at));
        }

        return (left, right);
    }

    /// <summary>Locates and starts playing as the controller does, but synchronously: locate, prime, play.</summary>
    public void PlayFrom(long position) {
        controller.Engine.Commands.TryEnqueue(new(EngineCommandKind.Locate, position));
        Process(false); // applies the locate while stopped
        Pump();
        controller.Engine.Commands.TryEnqueue(new(EngineCommandKind.Play));
    }

    public void Send(EngineCommand command) => Assert.True(controller.Engine.Commands.TryEnqueue(command));

    public void Dispose() {
        NativeMemory.Free(memory);
        NativeMemory.Free(inputs);
        NativeMemory.Free(outputs);
    }
}
