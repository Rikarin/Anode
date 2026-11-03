using Anode.Dsp.Spatial;

namespace Anode.Engine.Spatial;

/// <summary>
///     A strip's Surround Panner as the engine drives it (workstream D's <see cref="Dsp.Spatial.SurroundPanner" />,
///     through <see cref="DspSpatial" />; tests use doubles). Prepared off the audio thread; the rest on it.
/// </summary>
public interface ISurroundPanner {
    void Prepare(SpeakerLayout output, int inputChannels, float sampleRate, int maxFrames);

    /// <summary>New settings, reached over the next <see cref="Process" /> call.</summary>
    void Set(in SurroundPannerSettings settings);

    /// <summary>Adds the panned input into the output (the layout's channels).</summary>
    void Process(PlanarBlock input, PlanarBlock output);

    void Reset();
}

/// <summary>The master's object renderer (workstream D's <see cref="Dsp.Spatial.ObjectRenderer" />).</summary>
public interface IObjectRenderer {
    void Prepare(SpeakerLayout layout, int maxObjects, float sampleRate, int maxFrames);

    /// <summary>The object's position, reached over the next <see cref="Render" /> call of it.</summary>
    void SetPosition(int index, in ObjectPosition position, int frame = 0);

    /// <summary>Adds object <paramref name="index" />'s one or two channels, rendered, into the layout's channels.</summary>
    void Render(int index, PlanarBlock input, PlanarBlock output);

    void Reset(int index);
}

/// <summary>A down-mix from one layout to a smaller one (workstream D's <see cref="Dsp.Spatial.Downmixer" />).</summary>
public interface IDownmix {
    /// <summary>The linear gain of input channel <paramref name="input" /> in output channel <paramref name="output" />.</summary>
    float Gain(int output, int input);

    /// <summary>Writes the down-mix (replacing the output).</summary>
    void Process(PlanarBlock input, PlanarBlock output);
}

/// <summary>Binaural monitoring (workstream D's <see cref="BinauralRenderer" /> over an <see cref="HrtfSet" />).</summary>
public interface IBinaural {
    /// <summary>The delay the monitoring path adds (never reported to delay compensation or bounces).</summary>
    int LatencySamples { get; }

    void Process(PlanarBlock speakers, Span<float> left, Span<float> right);

    void Reset();
}

/// <summary>
///     Where the engine gets its spatial DSP (docs/11-phase-10.md): <see cref="DspSpatial" />, workstream D's classes,
///     unless a test hands it doubles. Every method runs on the control thread.
/// </summary>
public interface ISpatialFactory {
    ISurroundPanner CreateSurroundPanner();

    IObjectRenderer CreateObjectRenderer();

    /// <summary>A down-mix from <paramref name="from" /> to <paramref name="to" /> (fewer channels).</summary>
    IDownmix CreateDownmix(SpeakerLayout from, SpeakerLayout to);

    /// <summary>The binaural renderer of <paramref name="layout" />'s speakers through the HRTF file at <paramref name="hrtfPath" />.</summary>
    IBinaural CreateBinaural(string hrtfPath, SpeakerLayout layout, float sampleRate, int maxFrames);
}

/// <summary>
///     Workstream D's spatial DSP behind the engine's interfaces: the members each adapter calls are exactly the
///     contract's (<c>Anode.Dsp/Spatial</c>), so D's implementations drop in unchanged.
/// </summary>
public sealed class DspSpatial : ISpatialFactory {
    public static readonly DspSpatial Instance = new();

    public ISurroundPanner CreateSurroundPanner() => new Panner();

    public IObjectRenderer CreateObjectRenderer() => new Objects();

    public IDownmix CreateDownmix(SpeakerLayout from, SpeakerLayout to) => new Downmix(new(from, to));

    public IBinaural CreateBinaural(string hrtfPath, SpeakerLayout layout, float sampleRate, int maxFrames) {
        var renderer = new BinauralRenderer();
        renderer.Prepare(HrtfSet.Load(hrtfPath), layout, sampleRate, maxFrames);
        return new Binaural(renderer);
    }

    sealed class Panner : ISurroundPanner {
        readonly SurroundPanner panner = new();

        public void Prepare(SpeakerLayout output, int inputChannels, float sampleRate, int maxFrames) =>
            panner.Prepare(output, inputChannels, sampleRate, maxFrames);

        public void Set(in SurroundPannerSettings settings) => panner.Set(settings);

        public void Process(PlanarBlock input, PlanarBlock output) => panner.Process(input, output);

        public void Reset() => panner.Reset();
    }

    sealed class Objects : IObjectRenderer {
        readonly ObjectRenderer renderer = new();

        public void Prepare(SpeakerLayout layout, int maxObjects, float sampleRate, int maxFrames) =>
            renderer.Prepare(layout, maxObjects, sampleRate, maxFrames);

        public void SetPosition(int index, in ObjectPosition position, int frame = 0) =>
            renderer.SetPosition(index, position, frame);

        public void Render(int index, PlanarBlock input, PlanarBlock output) => renderer.Render(index, input, output);

        public void Reset(int index) => renderer.Reset(index);
    }

    sealed class Downmix(Downmixer downmixer) : IDownmix {
        public float Gain(int output, int input) => downmixer.Gain(output, input);

        public void Process(PlanarBlock input, PlanarBlock output) => downmixer.Process(input, output);
    }

    sealed class Binaural(BinauralRenderer renderer) : IBinaural {
        public int LatencySamples => renderer.LatencySamples;

        public void Process(PlanarBlock speakers, Span<float> left, Span<float> right) =>
            renderer.Process(speakers, left, right);

        public void Reset() => renderer.Reset();
    }
}
