using Anode.Dsp.Instruments;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Effects;

/// <summary>
///     What an effect is: its stable id (<c>anode.channel-eq</c>), names, category (EQ, Dynamics, Delay, Reverb,
///     Modulation, Distortion, Filter, Utility, Metering), parameters (with their macro hints, Phase 6), and whether it
///     takes a sidechain.
/// </summary>
public sealed record EffectDescriptor(
    string Id,
    string Name,
    string Vendor,
    string Version,
    string Category,
    IReadOnlyList<InstrumentParameter> Parameters,
    bool HasSidechain
);

/// <summary>
///     An insert effect: stereo in, stereo out, processed in place, with an optional stereo sidechain. Built-in effects
///     implement it directly; the engine wraps hosted plugins (CLAP, VST3, AU) in it too.
///     <para>
///         Threading as <see cref="IInstrument" />: <see cref="Prepare" />, <see cref="LoadState" />,
///         <see cref="SaveState" /> and disposal on the engine's control thread while the instance is not processing;
///         <see cref="SetParameter" />, <see cref="Process" /> and <see cref="Reset" /> on the audio thread, with no
///         allocation, lock or blocking.
///     </para>
/// </summary>
public interface IAudioEffect : IDisposable {
    EffectDescriptor Descriptor { get; }

    /// <summary>Allocates everything processing needs, for buffers of up to <paramref name="maxFrames" />.</summary>
    void Prepare(float sampleRate, int maxFrames);

    void SetParameter(int index, float value);

    float GetParameter(int index);

    /// <summary>The delay the effect adds, in samples, at the current settings (for delay compensation).</summary>
    int LatencySamples { get; }

    /// <summary>
    ///     Processes <c>left.Length</c> frames in place. The sidechain spans are empty when the effect has no sidechain
    ///     source; otherwise as long as the buffers.
    /// </summary>
    void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    );

    /// <summary>
    ///     The transport for the block about to be processed (docs/07-phase-6.md), on the audio thread once per block
    ///     before <see cref="Process" />. Tempo-synced effects keep what they need of it; the default ignores it.
    /// </summary>
    void SetTransport(in TransportContext transport) { }

    /// <summary>Clears tails and history (stop, locate) at once.</summary>
    void Reset();

    /// <summary>State beyond the parameters (null when there is none), opaque to the engine.</summary>
    byte[]? SaveState();

    bool LoadState(ReadOnlySpan<byte> state);

    /// <summary>
    ///     A level the editor may show for this effect (a compressor's gain reduction in dB, a meter's reading), or
    ///     NaN when it has none. Read from the control thread; written by <see cref="Process" />.
    /// </summary>
    float Reading { get; }
}

/// <summary>
///     An effect that shows the editor more than its one <see cref="IAudioEffect.Reading" />: a Multipressor's band gains,
///     an EQ's spectrum, a scope's trace. What the values mean is the effect's own; each effect documents their layout.
///     The engine finds it with a type test on an <see cref="IAudioEffect" />, as <see cref="IFileEffect" />. Read on the
///     control thread while the audio thread writes: a frame may tear, which a display does not mind. One reader at a
///     time.
/// </summary>
public interface IEffectDisplay {
    /// <summary>The most values <see cref="ReadDisplay" /> writes.</summary>
    int DisplaySize { get; }

    /// <summary>Writes the current values (never NaN or infinite) and returns how many; 0 when there are none yet.</summary>
    int ReadDisplay(Span<float> values);
}
