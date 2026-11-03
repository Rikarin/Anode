using Anode.Dsp.Effects;
using Anode.Dsp.Mastering;
using Anode.Engine.Recording;
using Anode.Ipc;
using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.Engine.Tests;

/// <summary>
///     A Mastering Assistant as workstream E's will be seen by the engine (its id, <see cref="IMasteringChain" />): a
///     gain of its chain's <c>ChainGainDb</c> (none without a chain), and fixed gain reductions to report.
/// </summary>
sealed class TestAssistant : IAudioEffect, IMasteringChain {
    public static readonly EffectDescriptor Info = new(
        MasteringAssistant.Id,
        "Test Mastering Assistant",
        "Anode tests",
        "1.0",
        EffectCategories.Dynamics,
        [],
        false
    );

    float gain = 1f;

    public List<MasteringChainSettings?> Chains { get; } = [];

    public EffectDescriptor Descriptor => Info;

    public int LatencySamples => 0;

    public float Reading => float.NaN;

    public float CompressorReductionDb => -3f;

    public float LimiterReductionDb => -1.5f;

    public void SetChain(MasteringChainSettings? chain) {
        Chains.Add(chain);
        Volatile.Write(ref gain, chain is null ? 1f : (float)Math.Pow(10, chain.ChainGainDb / 20));
    }

    public void Prepare(float sampleRate, int maxFrames) { }

    public void SetParameter(int index, float value) { }

    public float GetParameter(int index) => 0;

    public void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var g = Volatile.Read(ref gain);
        for (var i = 0; i < left.Length; i++) {
            left[i] *= g;
            right[i] *= g;
        }
    }

    public void Reset() { }

    public byte[]? SaveState() => null;

    public bool LoadState(ReadOnlySpan<byte> state) => true;

    public void Dispose() { }
}

/// <summary>
///     The Mastering Assistant as a master insert (docs/12-phase-11.md, workstream A): its chain from the analysis
///     handed to it (once per change), and its live loudness, true peak and gain reductions in the telemetry.
/// </summary>
public sealed unsafe class MasteringInsertTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly string sine;
    TestAssistant? assistant;

    public MasteringInsertTests() {
        // 997 Hz at 0.1 (−20 dBFS) in both channels: −20 LUFS (BS.1770's 997 Hz reference, −3.01 LUFS a channel at
        // full scale), its true peak −20 dBTP.
        sine = Path.Combine(dir, "sine.wav");
        using var writer = new WavWriter(sine, 2, Rate);
        var block = new float[2 * 4_096];
        for (var f = 0; f < 4 * Rate; f += 4_096) {
            for (var i = 0; i < 4_096; i++) {
                block[2 * i] = block[2 * i + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 997 * (f + i) / Rate));
            }

            writer.Write(block);
        }
    }

    public void Dispose() => Directory.Delete(dir, true);

    static EngineMasteringChain Chain(float gainDb) =>
        new([..Enumerable.Repeat(0f, MasteringBands.CentresHz.Count)], -18, 2, -20, gainDb);

    static EngineStrip Master(EngineMasteringChain? chain) =>
        new(
            1f,
            0f,
            false,
            false,
            [new("ma", MasteringAssistant.Id, false, new Dictionary<string, double>(), Mastering: chain)]
        );

    [Fact]
    public void The_assistant_gets_its_chain_once_per_change_and_reports_its_output_loudness_and_reductions() {
        var registry = TestEffects.Registry();
        registry.AddBuiltinEffect(TestAssistant.Info, () => assistant = new());
        using var controller = new EngineController(new(Rate, Block), registry);
        using var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, 4 * Rate, 0)])], 1);
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                [TestAudio.Track(1, true)],
                [new(0, sine, 2, Rate, 4 * Rate)],
                dir,
                Master(Chain(-6))
            )
        );
        Assert.NotNull(assistant);
        Assert.Equal(-6f, Assert.Single(assistant.Chains)!.ChainGainDb);
        Assert.Equal(30, assistant.Chains[0]!.EqGainsDb.Length);
        Assert.Equal(1, controller.Engine.MasteringReading().Active);

        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(0);
        for (var i = 0; i < 3 * Rate / Block; i++) {
            driver.Process();
        }

        var region = (byte*)NativeMemory.AllocZeroed((nuint)TelemetryLayout.Size);
        try {
            using var writer = new TelemetryWriter(controller.Engine, region, static () => 0);
            writer.Write();
            Assert.True(TelemetryWriter.TryRead(region, [], [], out var m));
            Assert.Equal(1, m.Active);
            Assert.InRange(m.MomentaryLufs, -26.3f, -25.7f); // −20 LUFS through the chain's −6 dB
            Assert.InRange(m.ShortTermLufs, -26.3f, -25.7f);
            Assert.InRange(m.IntegratedLufs, -26.3f, -25.7f);
            Assert.InRange(m.TruePeakDbtp, -26.1f, -25.9f);
            Assert.Equal((-3f, -1.5f), (m.CompressorReductionDb, m.LimiterReductionDb));
        } finally {
            NativeMemory.Free(region);
        }

        // The same chain again changes nothing; another is handed over; none takes it away.
        Assert.True(controller.SetChannel(-1, Master(Chain(-6))));
        Assert.Single(assistant.Chains);
        Assert.True(controller.SetChannel(-1, Master(Chain(-3))));
        Assert.True(controller.SetChannel(-1, Master(null)));
        Assert.Equal([-6f, -3f], assistant.Chains.Take(2).Select(static c => c!.ChainGainDb));
        Assert.Null(assistant.Chains[2]);

        // A master without an assistant has no entry.
        Assert.True(controller.SetChannel(-1, new(1f, 0f, false, false)));
        Assert.Equal(0, controller.Engine.MasteringReading().Active);
    }
}
