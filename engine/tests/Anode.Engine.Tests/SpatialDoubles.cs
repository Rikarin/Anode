using Anode.Dsp.Effects;
using Anode.Dsp.Spatial;
using Anode.Engine.Mixing;
using Anode.Engine.Spatial;

namespace Anode.Engine.Tests;

/// <summary>
///     Doubles of workstream D's spatial DSP behind the engine's interfaces (docs/11-phase-10.md: "develop against the
///     stub shapes with test doubles"): simple, exact rules the multichannel tests compute references from.
///     <list type="bullet">
///         <item>
///             Panner: input channel i goes to output channel <c>(int)Angle + i</c> (mod the layout) at gain
///             <c>Diversity</c>, plus <c>LfeGain</c> into the LFE; each <c>Process</c> ramps linearly from the previous
///             call's gains to the settings' (the first call, and after <c>Reset</c>, jumps).
///         </item>
///         <item>Object renderer: the same rule with <c>Azimuth</c> as the channel and <c>Distance</c> as the gain.</item>
///         <item>Down-mix: input channel i to output <c>i mod outputs</c> at 0.5 (a matrix the tests read back).</item>
///         <item>Binaural: even channels to the left ear, odd to the right, <see cref="BinauralLatency" /> frames late.</item>
///     </list>
/// </summary>
sealed class TestSpatial : ISpatialFactory {
    public int BinauralLatency { get; init; }

    public List<TestPanner> Panners { get; } = [];

    public TestObjects? Objects { get; private set; }

    public ISurroundPanner CreateSurroundPanner() {
        var panner = new TestPanner();
        lock (Panners) {
            Panners.Add(panner);
        }

        return panner;
    }

    public IObjectRenderer CreateObjectRenderer() => Objects = new();

    public IDownmix CreateDownmix(SpeakerLayout from, SpeakerLayout to) => new TestDownmix(from, to);

    public IBinaural CreateBinaural(string hrtfPath, SpeakerLayout layout, float sampleRate, int maxFrames) =>
        new TestBinaural(layout.ChannelCount, BinauralLatency);
}

/// <summary>Gains from input channel to output channel, ramped across each call (see <see cref="TestSpatial" />).</summary>
class RampedGains {
    float[] current = [];
    float[] target = [];
    bool jump = true;

    protected int Inputs { get; private set; }

    protected int Outputs { get; private set; }

    protected void Size(int inputs, int outputs) {
        Inputs = inputs;
        Outputs = outputs;
        current = new float[inputs * outputs];
        target = new float[inputs * outputs];
        jump = true;
    }

    protected void Target(int channel, float gain, float lfe, int lfeIndex) {
        Array.Clear(target);
        for (var i = 0; i < Inputs; i++) {
            target[i * Outputs + (((channel + i) % Outputs) + Outputs) % Outputs] += gain;
            if (lfeIndex >= 0) {
                target[i * Outputs + lfeIndex] += lfe;
            }
        }
    }

    public void Jump() => jump = true;

    protected void Apply(PlanarBlock input, PlanarBlock output) {
        if (jump) {
            target.CopyTo(current, 0);
            jump = false;
        }

        var n = input.Frames;
        for (var i = 0; i < Math.Min(Inputs, input.Channels); i++) {
            var x = input.Channel(i);
            for (var o = 0; o < Outputs; o++) {
                var from = current[i * Outputs + o];
                var to = target[i * Outputs + o];
                if (from == 0 && to == 0) {
                    continue;
                }

                var y = output.Channel(o);
                var step = (to - from) / n;
                for (var f = 0; f < n; f++) {
                    y[f] += x[f] * (from == to ? to : from + step * (f + 1));
                }
            }
        }

        target.CopyTo(current, 0);
    }
}

sealed class TestPanner : RampedGains, ISurroundPanner {
    int lfe = -1;

    /// <summary>Every <c>Set</c>, with the frames of the <c>Process</c> that followed it.</summary>
    public List<(SurroundPannerSettings Settings, int Frames)> Calls { get; } = [];

    public SpeakerLayout? Layout { get; private set; }

    SurroundPannerSettings pending;

    public void Prepare(SpeakerLayout output, int inputChannels, float sampleRate, int maxFrames) {
        Layout = output;
        lfe = output.LfeIndex;
        Size(inputChannels, output.ChannelCount);
    }

    public void Set(in SurroundPannerSettings settings) {
        pending = settings;
        Target((int)settings.Angle, settings.Diversity, settings.LfeGain, lfe);
    }

    public void Process(PlanarBlock input, PlanarBlock output) {
        if (Calls.Count < 100_000) {
            Calls.Add((pending, input.Frames));
        }

        Apply(input, output);
    }

    public void Reset() => Jump();
}

sealed class TestObjects : IObjectRenderer {
    ObjectSlot[] slots = [];

    /// <summary>Every <c>SetPosition</c> by object, with the frames of the render that followed it.</summary>
    public List<(int Index, ObjectPosition Position, int Frames)> Calls { get; } = [];

    public int Prepared { get; private set; }

    public void Prepare(SpeakerLayout layout, int maxObjects, float sampleRate, int maxFrames) {
        Prepared = maxObjects;
        slots = [..Enumerable.Range(0, maxObjects).Select(_ => new ObjectSlot(layout.ChannelCount))];
    }

    public void SetPosition(int index, in ObjectPosition position, int frame = 0) => slots[index].Set(position);

    public void Render(int index, PlanarBlock input, PlanarBlock output) {
        if (Calls.Count < 100_000) {
            Calls.Add((index, slots[index].Position, input.Frames));
        }

        slots[index].Render(input, output);
    }

    public void Reset(int index) => slots[index].Jump();

    sealed class ObjectSlot : RampedGains {
        readonly int outputs;

        public ObjectSlot(int outputs) {
            this.outputs = outputs;
        }

        public ObjectPosition Position { get; private set; }

        public void Set(in ObjectPosition position) {
            Position = position;
        }

        public void Render(PlanarBlock input, PlanarBlock output) {
            if (Inputs != input.Channels) {
                Size(input.Channels, outputs);
            }

            Target((int)Position.Azimuth, Position.Distance, 0, -1);
            Apply(input, output);
        }
    }
}

sealed class TestDownmix(SpeakerLayout from, SpeakerLayout to) : IDownmix {
    public float Gain(int output, int input) => input % to.ChannelCount == output ? 0.5f : 0f;

    public void Process(PlanarBlock input, PlanarBlock output) {
        output.Clear();
        for (var i = 0; i < Math.Min(from.ChannelCount, input.Channels); i++) {
            var x = input.Channel(i);
            var y = output.Channel(i % to.ChannelCount);
            for (var f = 0; f < x.Length; f++) {
                y[f] += 0.5f * x[f];
            }
        }
    }
}

sealed class TestBinaural(int channels, int latency) : IBinaural {
    readonly float[] left = new float[latency + 8192];
    readonly float[] right = new float[latency + 8192];

    public int LatencySamples => latency;

    public void Process(PlanarBlock speakers, Span<float> l, Span<float> r) {
        var n = speakers.Frames;
        Array.Copy(left, n, left, 0, latency);
        Array.Copy(right, n, right, 0, latency);
        Array.Clear(left, latency, n);
        Array.Clear(right, latency, n);
        for (var c = 0; c < Math.Min(channels, speakers.Channels); c++) {
            var x = speakers.Channel(c);
            var ear = (c & 1) == 0 ? left : right;
            for (var f = 0; f < n; f++) {
                ear[latency + f] += x[f];
            }
        }

        left.AsSpan(0, n).CopyTo(l);
        right.AsSpan(0, n).CopyTo(r);
    }

    public void Reset() {
        Array.Clear(left);
        Array.Clear(right);
    }
}

/// <summary>
///     A gain dynamics double for the linked policy: each frame whose louder input exceeds 0.5 is multiplied by 0.25,
///     after <c>frames</c> of latency (a look-ahead limiter's shape, exactly).
/// </summary>
sealed class DuckEffect : TestEffect {
    public const string Id = "test.duck";

    public static readonly EffectDescriptor Info = new(
        Id,
        "Test Duck",
        "Anode tests",
        "1.0",
        "Dynamics",
        [Parameter(0, "frames", 0, 1000, 0)],
        false
    );

    readonly float[] left = new float[8192];
    readonly float[] right = new float[8192];
    readonly float[] gains = new float[8192];
    int at;

    public DuckEffect() : base(Info) { }

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
            gains[at] = Math.Max(Math.Abs(l[i]), Math.Abs(r[i])) > 0.5f ? 0.25f : 1f;
            var from = (at - delay) & 8191;
            l[i] = left[from] * gains[at];
            r[i] = right[from] * gains[at];
            at = (at + 1) & 8191;
        }
    }

    public override void Reset() {
        base.Reset();
        Array.Clear(left);
        Array.Clear(right);
    }
}

static class SpatialFixtures {
    static SpatialFixtures() {
        MultichannelEffects.Register(TestEffects.Gain, MultichannelPolicy.PerChannel);
        MultichannelEffects.Register(DuckEffect.Id, MultichannelPolicy.Linked);
    }

    /// <summary>The test effects (with the linked double), their multichannel policies registered.</summary>
    public static Plugins.PluginRegistry Registry() {
        var registry = TestEffects.Registry();
        registry.AddBuiltinEffect(DuckEffect.Info, static () => new DuckEffect());
        return registry;
    }
}
