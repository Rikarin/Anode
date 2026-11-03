using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Filter;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Pitch;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Effects.Specialized;
using Anode.Dsp.Effects.Utility;
using Anode.Dsp.Spatial;
using Anode.Engine.Spatial;

namespace Anode.Engine.Mixing;

/// <summary>How a stereo insert covers a multichannel strip (docs/11-phase-10.md, "Effects").</summary>
public enum MultichannelPolicy {
    /// <summary>
    ///     Linear and channel-independent effects (EQ, filters, delays, reverbs, modulation, saturation, the band
    ///     dynamics): one instance per left/right pair, and per single channel (fed to both of its inputs, the mean of
    ///     its outputs kept), the parameters mirrored.
    /// </summary>
    PerChannel,

    /// <summary>
    ///     Gain dynamics (compressor, gate, limiters, enveloper), linked across every channel: the effect hears a key,
    ///     each frame's loudest sample of any channel, on both inputs, and the gain it applies to the key (its output over
    ///     its input, its latency later) is applied to every channel, delayed as the effect delays.
    /// </summary>
    Linked,

    /// <summary>
    ///     Effects that only make sense in stereo (meters, multi effects, pitch correction, the rotor) and hosted plugins
    ///     (CLAP, VST3 and AU get the stereo layout): the front left and right only; the other channels pass, delayed by
    ///     the effect's latency.
    /// </summary>
    FrontPair
}

/// <summary>
///     The policy of each built-in effect; hosted plugins and unknown ids are
///     <see cref="MultichannelPolicy.FrontPair" />.
/// </summary>
public static class MultichannelEffects {
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MultichannelPolicy> Registered =
        new(StringComparer.Ordinal);

    /// <summary>Gives effect <paramref name="pluginId" /> a policy (registered effects: tests, future built-ins).</summary>
    public static void Register(string pluginId, MultichannelPolicy policy) => Registered[pluginId] = policy;

    public static MultichannelPolicy PolicyOf(string pluginId) =>
        Registered.TryGetValue(pluginId, out var registered) ? registered : BuiltinPolicy(pluginId);

    static MultichannelPolicy BuiltinPolicy(string pluginId) =>
        pluginId switch {
            ChannelEq.Id
                or LinearPhaseEq.Id
                or Gain.Id
                or StereoDelay.Id
                or TapeDelay.Id
                or Reverb.Id
                or ConvolutionReverb.Id
                or Chorus.Id
                or Flanger.Id
                or Phaser.Id
                or Tremolo.Id
                or Ensemble.Id
                or AutoFilter.Id
                or Overdrive.Id
                or Distortion.Id
                or Bitcrusher.Id
                or Exciter.Id
                or SubBass.Id
                or PitchShifter.Id
                or DeEsser.Id
                or Multipressor.Id
                or Saturation.Id => MultichannelPolicy.PerChannel,
            Compressor.Id or NoiseGate.Id or Limiter.Id or AdaptiveLimiter.Id or Enveloper.Id =>
                MultichannelPolicy.Linked,
            _ => MultichannelPolicy.FrontPair
        };
}

/// <summary>
///     What an insert on a multichannel strip (an aux on a surround bus, a surround master) needs besides its own
///     instance: the extra instances of a per-channel effect (one per pair after the front one), and the delay that
///     keeps the channels it does not process (or, linked, all of them) in step with its latency. Built on the control
///     thread for one format, immutable in shape; the audio thread owns the delay state.
/// </summary>
public sealed class MultiInsert : IDisposable {
    /// <summary>Delay a linked or front-pair insert can follow (latency beyond it is clamped).</summary>
    public const int MaxLatency = 16_384;

    readonly float[][] ring;
    readonly int mask;
    int write;

    public MultiInsert(SpeakerFormat format, MultichannelPolicy policy, IAudioEffect?[] extras) {
        var layout = SpeakerLayout.Of(format);
        Format = format;
        Channels = layout.ChannelCount;
        Policy = policy;
        Pairs = Formats.Pairs(layout);
        Extras = extras;
        var size = 1;
        while (size < MaxLatency + AudioEngine.MaxFrames) {
            size <<= 1;
        }

        // Linked: every channel and the key; front pair: the channels after the first two.
        var lines = policy switch {
            MultichannelPolicy.Linked => Channels + 1,
            MultichannelPolicy.FrontPair => Channels,
            _ => 0
        };
        ring = new float[lines][];
        for (var c = 0; c < lines; c++) {
            ring[c] = GC.AllocateArray<float>(size, true);
        }

        mask = size - 1;
        Key = GC.AllocateArray<float>(AudioEngine.MaxFrames, true);
        Probe = GC.AllocateArray<float>(AudioEngine.MaxFrames, true);
        ProbeRight = GC.AllocateArray<float>(AudioEngine.MaxFrames, true);
        Ratio = GC.AllocateArray<float>(AudioEngine.MaxFrames, true);
    }

    public SpeakerFormat Format { get; }

    public int Channels { get; }

    public MultichannelPolicy Policy { get; }

    /// <summary>The pairs (or single channels, <c>B</c> −1) each instance processes; pair 0 is the insert's own.</summary>
    public (int A, int B)[] Pairs { get; }

    /// <summary>The instances of pairs 1…: per-channel effects only (null where one could not be built).</summary>
    public IAudioEffect?[] Extras { get; }

    /// <summary>Audio thread scratch: the linked key, the effect's output of it, and the gain per frame.</summary>
    internal float[] Key { get; }

    internal float[] Probe { get; }

    internal float[] ProbeRight { get; }

    internal float[] Ratio { get; }

    /// <summary>Audio thread: the last gain a linked insert applied (held while the key is silent).</summary>
    internal float LastRatio = 1f;

    /// <summary>Audio thread: appends a block of line <paramref name="line" /> (all lines, then <see cref="Advance" />).</summary>
    internal void Write(int line, ReadOnlySpan<float> block) {
        var r = ring[line];
        for (var i = 0; i < block.Length; i++) {
            r[(write + i) & mask] = block[i];
        }
    }

    internal void Advance(int frames) => write = (write + frames) & mask;

    /// <summary>Audio thread, after <see cref="Advance" />: the block just written, <paramref name="delay" /> late.</summary>
    internal void Read(int line, int delay, Span<float> block) {
        delay = Math.Clamp(delay, 0, MaxLatency);
        var r = ring[line];
        var start = write - block.Length - delay;
        for (var i = 0; i < block.Length; i++) {
            block[i] = r[(start + i) & mask];
        }
    }

    /// <summary>Audio thread: silence (a render's start).</summary>
    internal void Clear() {
        foreach (var r in ring) {
            Array.Clear(r);
        }

        LastRatio = 1f;
        foreach (var extra in Extras) {
            extra?.Reset();
        }
    }

    public void Dispose() {
        foreach (var extra in Extras) {
            extra?.Dispose();
        }
    }
}
