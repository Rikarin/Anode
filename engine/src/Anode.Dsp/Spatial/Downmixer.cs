namespace Anode.Dsp.Spatial;

/// <summary>
///     Down-mix from one layout to a smaller one (docs/11-phase-10.md, workstream D): ITU-R BS.775 for 5.x to stereo
///     (centre and surrounds at −3 dB, the LFE dropped), and the standard extensions for 7.x and height layouts (upper
///     layer folded into the middle at −3 dB, sides and rears as surrounds), as a matrix of gains. Used for monitoring
///     (never in bounces) and for the listen-in tap's stereo. Real-time safe after construction.
/// </summary>
public sealed class Downmixer {
    /// <summary>Off the audio thread: the matrix from <paramref name="from" /> to <paramref name="to" />.</summary>
    public Downmixer(SpeakerLayout from, SpeakerLayout to) {
        throw Pending.WorkstreamD();
    }

    /// <summary>
    ///     The gain of input channel <paramref name="input" /> in output channel <paramref name="output" /> (linear; the
    ///     matrix the tests check against BS.775 within 0.1 dB).
    /// </summary>
    public float Gain(int output, int input) => throw Pending.WorkstreamD();

    /// <summary>Writes the down-mix of <paramref name="input" /> into <paramref name="output" /> (replacing it).</summary>
    public void Process(PlanarBlock input, PlanarBlock output) => throw Pending.WorkstreamD();
}
