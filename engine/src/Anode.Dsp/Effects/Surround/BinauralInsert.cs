using Anode.Dsp.Instruments;
using Anode.Dsp.Spatial;

namespace Anode.Dsp.Effects.Surround;

/// <summary>
///     <c>anode.binaural</c>, Binaural Post-Processing as an insert (docs/12-phase-11.md, workstream E): a surround strip
///     to binaural stereo through ADR-0012's renderer (Phase 10's <see cref="BinauralRenderer" />, the HRTF set the
///     monitoring uses). Its output is always stereo.
/// </summary>
/// <remarks>
///     Each speaker of the strip's layout is a virtual speaker at its nominal position, convolved with its HRTF pair
///     (the LFE to both ears unfiltered), with no added latency. Without an HRTF set (<see cref="UseHrtfs" /> not called
///     before <see cref="Prepare" />) it down-mixes to stereo by BS.775 instead, so the strip is never silent. It has no
///     parameters. Not in <see cref="BuiltinEffects" /> (it is not an <see cref="IAudioEffect" />): the engine creates it
///     by id.
/// </remarks>
public sealed class BinauralInsert : IFormatEffect {
    public const string Id = "anode.binaural";

    public static EffectDescriptor Info { get; } = new(
        Id,
        "Binaural Post-Processing",
        EffectParameters.Vendor,
        EffectParameters.Version,
        EffectCategories.Utility,
        [],
        false
    );

    static readonly ParameterBank NoParameters = new([]);

    readonly BinauralRenderer renderer = new();
    HrtfSet? hrtfs;
    Downmixer? fallback;
    bool rendering;
    bool prepared;

    public EffectDescriptor Descriptor => Info;

    /// <summary>On the control thread, before <see cref="Prepare" />: the HRTF set it renders with.</summary>
    public void UseHrtfs(HrtfSet hrtfs) => this.hrtfs = hrtfs;

    public SpeakerFormat OutputFormat(SpeakerFormat input) => SpeakerFormat.Stereo;

    public void Prepare(SpeakerLayout input, SpeakerLayout output, float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        rendering = hrtfs is not null;
        if (rendering) {
            renderer.Prepare(hrtfs!, input, sampleRate, maxFrames);
            fallback = null;
        } else {
            fallback = new(input, SpeakerLayout.Of(SpeakerFormat.Stereo));
        }

        prepared = true;
    }

    public void SetParameter(int index, float value) { }

    public float GetParameter(int index) => 0;

    public int LatencySamples => rendering ? renderer.LatencySamples : 0;

    public void Process(PlanarBlock input, PlanarBlock output) {
        var frames = Math.Min(input.Frames, output.Frames);
        if (!prepared || output.Channels < 2) {
            output.Clear();
            return;
        }

        if (rendering) {
            renderer.Process(input, output.Channel(0)[..frames], output.Channel(1)[..frames]);
        } else {
            fallback!.Process(input, output);
        }

        for (var c = 2; c < output.Channels; c++) {
            output.Channel(c)[..frames].Clear();
        }
    }

    public void Reset() {
        if (rendering) {
            renderer.Reset();
        }
    }

    public byte[]? SaveState() => InstrumentState.Write(Id, null, Info.Parameters, NoParameters);

    public bool LoadState(ReadOnlySpan<byte> state) => InstrumentState.TryRead(state, Id, out _, out _);

    public void Dispose() { }
}
