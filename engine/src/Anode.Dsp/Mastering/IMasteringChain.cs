namespace Anode.Dsp.Mastering;

/// <summary>
///     What the engine needs of a Mastering Assistant besides its <see cref="Effects.IAudioEffect" /> side
///     (docs/12-phase-11.md, workstream A): the chain its analysis set, and the gain reductions its compressor and
///     limiter apply now, which the host reports through its telemetry (<c>MasteringEntry</c>).
/// </summary>
public interface IMasteringChain {
    /// <summary>On the control thread: the chain from the analysis (null: none yet, the limiter alone).</summary>
    void SetChain(MasteringChainSettings? chain);

    /// <summary>The compressor's gain reduction now (dB, 0 or less), readable from any thread.</summary>
    float CompressorReductionDb { get; }

    /// <summary>The limiter's gain reduction now (dB, 0 or less), readable from any thread.</summary>
    float LimiterReductionDb { get; }
}
