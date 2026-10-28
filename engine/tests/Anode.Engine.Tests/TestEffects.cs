using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Engine.Plugins;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Effects the mixer tests insert (workstream D's built-ins were not on master when these were written): exact, so
///     renders can be compared bit for bit with a reference.
/// </summary>
static class TestEffects {
    public const string Gain = "test.gain";
    public const string Delay = "test.delay";
    public const string Sidechain = "test.sidechain";
    public const string Biquad = "test.biquad";

    /// <summary>A registry with every test effect (and the test instrument).</summary>
    public static PluginRegistry Registry() {
        var registry = new PluginRegistry();
        registry.AddBuiltin(TestInstrument.Info, static () => new TestInstrument());
        registry.AddBuiltinEffect(GainEffect.Info, static () => new GainEffect());
        registry.AddBuiltinEffect(DelayEffect.Info, static () => new DelayEffect());
        registry.AddBuiltinEffect(SidechainEffect.Info, static () => new SidechainEffect());
        registry.AddBuiltinEffect(BiquadEffect.Info, static () => new BiquadEffect());
        return registry;
    }

    public static EngineInsert Insert(
        string id,
        string plugin,
        IReadOnlyDictionary<string, double>? parameters = null,
        bool bypassed = false,
        int sidechainTrack = -1,
        int sidechainBus = 0
    ) =>
        new(id, plugin, bypassed, parameters ?? new Dictionary<string, double>(), null, sidechainTrack, sidechainBus);
}

/// <summary>The common parts: parameters by index, no state, no reading.</summary>
abstract class TestEffect(EffectDescriptor descriptor) : IAudioEffect {
    readonly float[] values = [..descriptor.Parameters.Select(static p => p.Default)];

    public EffectDescriptor Descriptor { get; } = descriptor;

    public bool Disposed { get; private set; }

    public int Resets { get; private set; }

    public virtual int LatencySamples => 0;

    public float Reading => float.NaN;

    public virtual void Prepare(float sampleRate, int maxFrames) { }

    public void SetParameter(int index, float value) => values[index] = value;

    public float GetParameter(int index) => values[index];

    public abstract void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    );

    public virtual void Reset() => Resets++;

    public virtual byte[]? SaveState() => null;

    public virtual bool LoadState(ReadOnlySpan<byte> state) => true;

    public void Dispose() => Disposed = true;

    protected static InstrumentParameter Parameter(int index, string id, float min, float max, float value) =>
        new(index, id, id, min, max, value, string.Empty);
}

/// <summary>Multiplies by its <c>gain</c> parameter (−4…4), at once, no smoothing.</summary>
sealed class GainEffect() : TestEffect(Info) {
    public static readonly EffectDescriptor Info = new(
        TestEffects.Gain,
        "Test Gain",
        "Anode tests",
        "1.0",
        "Utility",
        [Parameter(0, "gain", -4, 4, 1)],
        false
    );

    public override void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var g = GetParameter(0);
        for (var i = 0; i < left.Length; i++) {
            left[i] *= g;
            right[i] *= g;
        }
    }

    public override byte[]? SaveState() => BitConverter.GetBytes(GetParameter(0));

    public override bool LoadState(ReadOnlySpan<byte> state) {
        if (state.Length != 4) {
            return false;
        }

        SetParameter(0, BitConverter.ToSingle(state));
        return true;
    }
}

/// <summary>A pure delay of <c>frames</c> samples (0…4800), reported as its latency.</summary>
sealed class DelayEffect : TestEffect {
    public static readonly EffectDescriptor Info = new(
        TestEffects.Delay,
        "Test Delay",
        "Anode tests",
        "1.0",
        "Delay",
        [Parameter(0, "frames", 0, 4800, 100)],
        false
    );

    readonly float[] left = new float[8192];
    readonly float[] right = new float[8192];
    int at;

    public DelayEffect() : base(Info) { }

    public override int LatencySamples => (int)GetParameter(0);

    public override void Process(
        Span<float> l,
        Span<float> r,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var delay = LatencySamples;
        for (var i = 0; i < l.Length; i++) {
            left[at] = l[i];
            right[at] = r[i];
            var from = (at - delay) & 8191;
            l[i] = left[from];
            r[i] = right[from];
            at = (at + 1) & 8191;
        }
    }

    public override void Reset() {
        base.Reset();
        Array.Clear(left);
        Array.Clear(right);
    }
}

/// <summary>Adds its sidechain to the signal (so a render shows exactly what the sidechain carried).</summary>
sealed class SidechainEffect() : TestEffect(Info) {
    public static readonly EffectDescriptor Info = new(
        TestEffects.Sidechain,
        "Test Sidechain",
        "Anode tests",
        "1.0",
        "Dynamics",
        [],
        true
    );

    public override void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        if (sidechainLeft.IsEmpty) {
            return;
        }

        for (var i = 0; i < left.Length; i++) {
            left[i] += sidechainLeft[i];
            right[i] += sidechainRight[i];
        }
    }
}

/// <summary>Two cascaded biquad peaking filters per channel: a cost like a simple EQ band pair, for the benchmark.</summary>
sealed class BiquadEffect() : TestEffect(Info) {
    public static readonly EffectDescriptor Info = new(
        TestEffects.Biquad,
        "Test Biquad",
        "Anode tests",
        "1.0",
        "EQ",
        [Parameter(0, "frequency", 20, 20_000, 1000)],
        false
    );

    readonly float[] state = new float[8];
    float b0 = 1.02f, b1 = -1.9f, b2 = 0.9f, a1 = -1.9f, a2 = 0.92f;

    public override void Prepare(float sampleRate, int maxFrames) {
        var w = 2 * Math.PI * GetParameter(0) / sampleRate;
        var alpha = Math.Sin(w) / 2;
        var gain = Math.Pow(10, 3 / 40.0);
        var a0 = 1 + alpha / gain;
        b0 = (float)((1 + alpha * gain) / a0);
        b1 = (float)(-2 * Math.Cos(w) / a0);
        b2 = (float)((1 - alpha * gain) / a0);
        a1 = b1;
        a2 = (float)((1 - alpha / gain) / a0);
    }

    public override void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        Filter(left, 0);
        Filter(left, 2);
        Filter(right, 4);
        Filter(right, 6);
    }

    void Filter(Span<float> x, int s) {
        float z1 = state[s], z2 = state[s + 1];
        for (var i = 0; i < x.Length; i++) {
            var input = x[i];
            var y = b0 * input + z1;
            z1 = b1 * input - a1 * y + z2;
            z2 = b2 * input - a2 * y;
            x[i] = y;
        }

        state[s] = z1;
        state[s + 1] = z2;
    }

    public override void Reset() {
        base.Reset();
        Array.Clear(state);
    }
}

/// <summary>
///     A stand-in for workstream E's factory: it hosts <c>test.hosted</c> (a gain with an editor) and records the
///     contexts it was given, so tests can drive the callbacks.
/// </summary>
sealed class TestFactory : IPluginFactory, IAudioCycleObserver {
    public const string Hosted = "test.hosted";

    public List<(PluginContext Context, HostedEffect Effect)> Created { get; } = [];

    public int Scans { get; private set; }

    public long Cycles;

    public IReadOnlyList<EnginePlugin> Catalog { get; } = [
        new(
            Hosted,
            "Hosted Gain",
            "Test Vendor",
            "1.0",
            "vst3",
            false,
            [],
            [new("gain", "Gain", -4, 4, 1, string.Empty, 0)],
            "Utility",
            false,
            true
        )
    ];

    public void Scan() => Scans++;

    public bool Handles(string pluginId) => pluginId == Hosted;

    public IAudioEffect? CreateEffect(string pluginId, PluginContext context) {
        var effect = new HostedEffect();
        Created.Add((context, effect));
        return effect;
    }

    public IInstrument? CreateInstrument(string pluginId, PluginContext context) => null;

    public void BeginCycle(long hostTimeNs, int frames) => Cycles++;

    public void EndCycle() { }

    /// <summary>The hosted gain: a plugin editor that can be opened and closed.</summary>
    public sealed class HostedEffect() : TestEffect(
            new(
                Hosted,
                "Hosted Gain",
                "Test Vendor",
                "1.0",
                "Utility",
                [Parameter(0, "gain", -4, 4, 1)],
                false
            )
        ),
        IPluginEditor {
        public bool EditorOpen { get; private set; }

        public bool SetEditorOpen(bool open) {
            EditorOpen = open;
            return true;
        }

        public override void Process(
            Span<float> left,
            Span<float> right,
            ReadOnlySpan<float> sidechainLeft,
            ReadOnlySpan<float> sidechainRight
        ) {
            var g = GetParameter(0);
            for (var i = 0; i < left.Length; i++) {
                left[i] *= g;
                right[i] *= g;
            }
        }
    }
}
