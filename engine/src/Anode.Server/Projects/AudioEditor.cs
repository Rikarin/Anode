using Anode.Contracts;
using Anode.Dsp.Editing;

namespace Anode.Server.Projects;

/// <summary>
///     The Audio File Editor's algorithms (docs/08-phase-7.md): the seam between the server and
///     <c>Anode.Dsp.Editing</c> (workstream E), so tests can put deterministic ones in their place.
/// </summary>
public interface IAudioEditor {
    /// <summary>
    ///     Applies <paramref name="operation" /> to a file's channels at <paramref name="sampleRate" />: the channels it
    ///     leaves (the same arrays, changed in place, or new ones for a trim or a rate conversion) and their rate.
    /// </summary>
    (float[][] Channels, int SampleRate) Apply(float[][] channels, int sampleRate, AudioEditOperation operation);

    /// <summary><c>LoopSearch.Find</c>: the best loops in a range, best first.</summary>
    IReadOnlyList<LoopCandidate> Loops(float[] mono, int sampleRate, long start, long end, int count, long minFrames);

    /// <summary><c>GrooveFromAudio.Onsets</c>: a file's onsets with their strengths, for a groove template.</summary>
    IReadOnlyList<GrooveOnset> Onsets(float[] mono, int sampleRate);
}

/// <summary>The algorithms of <c>Anode.Dsp.Editing</c>.</summary>
public sealed class DspAudioEditor : IAudioEditor {
    public const double DefaultNormalizeDb = -1;

    public (float[][] Channels, int SampleRate) Apply(
        float[][] channels,
        int sampleRate,
        AudioEditOperation operation
    ) {
        var (start, end) = (operation.StartFrame, operation.EndFrame);
        switch (operation.Kind) {
            case AudioEditKind.Normalize:
                AudioEdits.Normalize(channels, start, end, operation.Value ?? DefaultNormalizeDb);
                break;
            case AudioEditKind.Gain:
                AudioEdits.Gain(channels, start, end, operation.Value ?? 0);
                break;
            case AudioEditKind.FadeIn:
                AudioEdits.FadeIn(channels, start, end, operation.Curve ?? 0);
                break;
            case AudioEditKind.FadeOut:
                AudioEdits.FadeOut(channels, start, end, operation.Curve ?? 0);
                break;
            case AudioEditKind.Reverse:
                AudioEdits.Reverse(channels, start, end);
                break;
            case AudioEditKind.Invert:
                AudioEdits.Invert(channels, start, end);
                break;
            case AudioEditKind.Silence:
                AudioEdits.Silence(channels, start, end);
                break;
            case AudioEditKind.RemoveDc:
                AudioEdits.RemoveDc(channels, start, end);
                break;
            case AudioEditKind.Trim:
                return (AudioEdits.Trim(channels, start, end), sampleRate);
            case AudioEditKind.ConvertRate:
                var rate = operation.TargetRate ?? sampleRate;
                return (AudioEdits.ConvertRate(channels, sampleRate, rate), rate);
            default:
                throw new ArgumentException($"Unknown operation {operation.Kind}.", nameof(operation));
        }

        return (channels, sampleRate);
    }

    public IReadOnlyList<LoopCandidate> Loops(
        float[] mono,
        int sampleRate,
        long start,
        long end,
        int count,
        long minFrames
    ) =>
        LoopSearch.Find(mono, sampleRate, start, end, count, minFrames);

    public IReadOnlyList<GrooveOnset> Onsets(float[] mono, int sampleRate) => GrooveFromAudio.Onsets(mono, sampleRate);
}
