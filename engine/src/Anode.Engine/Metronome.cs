using Anode.Ipc.Segments;

namespace Anode.Engine;

/// <summary>
///     The click: two short synthesized sounds (a higher one on a bar's downbeat), started at the sample positions of
///     the model's click events (compiled by the server, <see cref="PlaybackEventKind.Click" />) and, before a
///     recording, at a one-bar count-in the engine times itself. One voice: a new click cuts the previous one.
///     Audio-thread code apart from the constructor and <see cref="CountInFor" />.
/// </summary>
public sealed unsafe class Metronome {
    /// <summary>Most clicks one block can start (a 4096-frame block at a 1-sample click spacing is not a use case).</summary>
    const int MaxClicks = 64;

    readonly float[] accent;
    readonly float[] beat;
    readonly Click[] clicks = new Click[MaxClicks];
    int clickCount;
    float[]? voice;
    int voicePosition;
    float voiceLevel;

    public Metronome(int sampleRate) {
        SampleRate = sampleRate;
        accent = Synthesize(sampleRate, 1_600, 0.030);
        beat = Synthesize(sampleRate, 1_000, 0.030);
    }

    public int SampleRate { get; }

    /// <summary>The downbeat sound (for tests).</summary>
    public ReadOnlySpan<float> Accent => accent;

    /// <summary>The other beats' sound (for tests).</summary>
    public ReadOnlySpan<float> Beat => beat;

    /// <summary>A sine burst with a short attack and a quadratic decay, starting and ending at zero.</summary>
    static float[] Synthesize(int sampleRate, double frequency, double seconds) {
        var length = (int)(sampleRate * seconds);
        var attack = Math.Max(1, sampleRate / 2_000);
        var wave = new float[length];
        for (var i = 0; i < length; i++) {
            var decay = 1.0 - (double)i / length;
            var envelope = Math.Min(1.0, (double)i / attack) * decay * decay;
            wave[i] = (float)(0.5 * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        }

        return wave;
    }

    /// <summary>Starts collecting the clicks of a block.</summary>
    public void Begin() => clickCount = 0;

    /// <summary>Queues a click at <paramref name="offset" /> frames into the block (in increasing order).</summary>
    public void Add(int offset, bool downbeat, float level) {
        if (clickCount < MaxClicks) {
            clicks[clickCount++] = new(offset, downbeat, level);
        }
    }

    /// <summary>
    ///     Queues the clicks of <paramref name="view" /> at positions [<paramref name="from" />, + <paramref name="count" />)
    ///     at block offset <paramref name="offset" />.
    /// </summary>
    public void AddEvents(in TrackView view, long from, int count, int offset) {
        var events = view.Events;
        var end = from + count;
        for (var i = LowerBound(events, view.EventCount, from); i < view.EventCount && events[i].SamplePos < end; i++) {
            ref readonly var e = ref events[i];
            if (e.Kind == PlaybackEventKind.Click) {
                Add(offset + (int)(e.SamplePos - from), e.Data1 == 1, e.Value > 0 ? e.Value : 1f);
            }
        }
    }

    /// <summary>
    ///     Queues the count-in clicks that fall in frames [<paramref name="elapsed" />, + <paramref name="count" />) of
    ///     a count-in of <paramref name="beats" /> beats over <paramref name="total" /> frames.
    /// </summary>
    public void AddCountIn(long total, int beats, long elapsed, int count) {
        for (var k = 0; k < beats; k++) {
            var at = total * k / beats;
            if (at >= elapsed && at < elapsed + count) {
                Add((int)(at - elapsed), k == 0, 1f);
            }
        }
    }

    /// <summary>
    ///     Adds the voice's samples for <paramref name="output" />.Length frames at <paramref name="gain" />, starting
    ///     the queued clicks at their offsets (silent at gain 0, but the voice still advances).
    /// </summary>
    public void Render(Span<float> output, float gain) {
        var cursor = 0;
        for (var c = 0; c < clickCount; c++) {
            var click = clicks[c];
            var at = Math.Clamp(click.Offset, cursor, output.Length);
            Play(output[cursor..at], gain);
            voice = click.Downbeat ? accent : beat;
            voicePosition = 0;
            voiceLevel = click.Level;
            cursor = at;
        }

        Play(output[cursor..], gain);
        clickCount = 0;
    }

    void Play(Span<float> output, float gain) {
        if (voice is null) {
            return;
        }

        var count = Math.Min(output.Length, voice.Length - voicePosition);
        var level = gain * voiceLevel;
        for (var i = 0; i < count; i++) {
            output[i] += voice[voicePosition + i] * level;
        }

        voicePosition += count;
        if (voicePosition >= voice.Length) {
            voice = null;
        }
    }

    /// <summary>
    ///     The count-in before recording at <paramref name="position" />: one bar, timed from the model's clicks (the
    ///     bar's length between two downbeats and its beats are the clicks between them), from the bar at or before
    ///     <paramref name="position" />, else the first one in the model. Without two downbeats to measure it is 4/4 at
    ///     120 bpm.
    /// </summary>
    public static (long Frames, int Beats) CountInFor(in TrackView clicks, long position, int sampleRate) {
        var events = clicks.Events;
        var count = clicks.EventCount;
        var downbeat = LastDownbeat(events, Math.Min(LowerBound(events, count, position + 1), count));
        if (downbeat < 0) {
            downbeat = NextDownbeat(events, count, 0);
        }

        if (downbeat >= 0 && NextDownbeat(events, count, downbeat + 1) is var next and >= 0) {
            var frames = events[next].SamplePos - events[downbeat].SamplePos;
            var beats = 0;
            for (var i = downbeat; i < next; i++) {
                beats += events[i].Kind == PlaybackEventKind.Click ? 1 : 0;
            }

            if (frames > 0 && beats <= 32) {
                return (frames, beats);
            }
        }

        return (2L * sampleRate, 4); // 4/4 at 120 bpm
    }

    static bool IsDownbeat(in PlaybackEvent e) => e is { Kind: PlaybackEventKind.Click, Data1: 1 };

    /// <summary>The index of the last downbeat before index <paramref name="end" />, or −1.</summary>
    static long LastDownbeat(PlaybackEvent* events, long end) {
        for (var i = end - 1; i >= 0; i--) {
            if (IsDownbeat(events[i])) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index of the first downbeat at or after index <paramref name="from" />, or −1.</summary>
    static long NextDownbeat(PlaybackEvent* events, long count, long from) {
        for (var i = from; i < count; i++) {
            if (IsDownbeat(events[i])) {
                return i;
            }
        }

        return -1;
    }

    static long LowerBound(PlaybackEvent* events, long count, long position) {
        long lo = 0, hi = count;
        while (lo < hi) {
            var mid = lo + ((hi - lo) >> 1);
            if (events[mid].SamplePos < position) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }

        return lo;
    }

    readonly record struct Click(int Offset, bool Downbeat, float Level);
}
