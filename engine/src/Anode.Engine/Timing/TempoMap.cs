using Anode.Dsp.Transport;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Timing;

/// <summary>
///     The compiled tempo map of the published model (format 4, docs/07-phase-6.md, "Tempo in the engine"): a view of
///     the segment's <see cref="TempoMapPoint" />s, which the audio thread reads every block for the
///     <see cref="TransportContext" /> and the Live Loops launcher reads for its quantise boundaries. Positions are
///     exact over each piece (constant tempo, or a tempo linear in quarter notes: an exponential in time). A map with no
///     points (a model without tempo, tests) is 120 bpm in 4/4 from the project start. Allocation-free; the segment it
///     points into is kept mapped by the model that holds it.
/// </summary>
public readonly unsafe struct TempoMap {
    const double Epsilon = 1e-9;

    static readonly TempoMapPoint Default = new() {
        Sample = 0,
        Quarter = 0,
        Bpm = 120,
        Slope = 0,
        BarQuarter = 0,
        Numerator = 4,
        Denominator = 4
    };

    readonly TempoMapPoint* points;
    readonly int count;

    /// <summary>The default map (120 bpm in 4/4) at <paramref name="sampleRate" />.</summary>
    public TempoMap(double sampleRate) : this(null, 0, sampleRate) { }

    public TempoMap(TempoMapPoint* points, long count, double sampleRate) {
        this.points = points;
        this.count = points is null ? 0 : (int)Math.Clamp(count, 0, int.MaxValue);
        SampleRate = sampleRate;
    }

    public double SampleRate { get; }

    /// <summary>The number of points (0: the default map).</summary>
    public int Count => Math.Max(1, count);

    public TempoMapPoint this[int index] => count == 0 ? Default : points[index];

    /// <summary>The index of the last point at or before <paramref name="sample" /> (0 before the first).</summary>
    public int IndexAt(long sample) {
        if (count <= 1) {
            return 0;
        }

        int lo = 0, hi = count - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) >> 1;
            if (points[mid].Sample <= sample) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>The index of the last point at or before quarter note <paramref name="quarter" /> (0 before the first).</summary>
    public int IndexAtQuarter(double quarter) {
        if (count <= 1) {
            return 0;
        }

        int lo = 0, hi = count - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) >> 1;
            if (points[mid].Quarter <= quarter) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>The quarter-note position and the tempo at project sample <paramref name="sample" />.</summary>
    public (double Quarter, double Bpm) At(long sample) {
        var p = this[IndexAt(sample)];
        var seconds = (sample - p.Sample) / SampleRate;
        if (p.Slope == 0) {
            return (p.Quarter + p.Bpm * seconds / 60, p.Bpm);
        }

        var growth = Math.Exp(p.Slope * seconds / 60);
        return (p.Quarter + p.Bpm / p.Slope * (growth - 1), p.Bpm * growth);
    }

    public double QuarterAt(long sample) => At(sample).Quarter;

    /// <summary>The project sample (fractional) at which quarter note <paramref name="quarter" /> falls.</summary>
    public double SampleAt(double quarter) {
        var p = this[IndexAtQuarter(quarter)];
        var beats = quarter - p.Quarter;
        double seconds;
        if (p.Slope == 0) {
            seconds = beats * 60 / p.Bpm;
        } else {
            // B·exp(S·t/60) = B + S·beats: a slowing ramp never reaches a tempo of zero inside its piece.
            var ratio = Math.Max(1e-12, 1 + p.Slope * beats / p.Bpm);
            seconds = 60 / p.Slope * Math.Log(ratio);
        }

        return p.Sample + seconds * SampleRate;
    }

    /// <summary>
    ///     The quarter note of the bar line at or before <paramref name="quarter" /> under point
    ///     <paramref name="index" />'s signature.
    /// </summary>
    public double BarStart(double quarter, int index) {
        var p = this[index];
        var bar = Bar(p);
        return p.BarQuarter + Math.Floor((quarter - p.BarQuarter) / bar + Epsilon) * bar;
    }

    /// <summary>
    ///     The transport for a block of <paramref name="frames" /> from <paramref name="sample" />: its position, tempo,
    ///     signature and bar start there.
    /// </summary>
    public TransportContext Context(bool playing, long sample, int frames) {
        var index = IndexAt(sample);
        var p = this[index];
        var (quarter, bpm) = At(sample);
        return new(
            playing,
            sample,
            quarter,
            bpm,
            p.Numerator,
            p.Denominator,
            BarStart(quarter, index),
            SampleRate,
            frames
        );
    }

    /// <summary>
    ///     The first project sample at or after <paramref name="from" /> on a <paramref name="quantize" /> boundary:
    ///     a sixteenth, eighth, quarter or half note from the bar line (a bar's end is always one), or every one, two or
    ///     four bars counted from where the signature took effect. <see cref="EngineQuantize.Off" /> is
    ///     <paramref name="from" /> itself. A boundary's sample is the first whose position is at or past it.
    /// </summary>
    public long NextBoundary(long from, EngineQuantize quantize) {
        if (quantize == EngineQuantize.Off) {
            return from;
        }

        var quarter = QuarterAt(from) - 1e-7;
        for (var guard = 0; guard < 64; guard++) {
            var boundary = BoundaryQuarter(quarter, quantize);
            var sample = (long)Math.Ceiling(SampleAt(boundary) - 1e-6);
            if (sample >= from) {
                return sample;
            }

            quarter = boundary + 1e-6; // rounding put it before `from`: the next one
        }

        return from;
    }

    /// <summary>The first boundary quarter at or after <paramref name="quarter" />, across signature changes.</summary>
    double BoundaryQuarter(double quarter, EngineQuantize quantize) {
        var index = IndexAtQuarter(quarter);
        while (true) {
            var p = this[index];
            var candidate = BoundaryIn(p, Math.Max(quarter, p.Quarter), quantize);
            if (index + 1 >= Count) {
                return candidate;
            }

            var next = this[index + 1];
            if (candidate < next.Quarter - Epsilon || SameGrid(p, next)) {
                return candidate;
            }

            index++; // the signature changes before the boundary: count from the change
            quarter = Math.Max(quarter, next.Quarter);
        }
    }

    static bool SameGrid(in TempoMapPoint a, in TempoMapPoint b) =>
        a.BarQuarter == b.BarQuarter && a.Numerator == b.Numerator && a.Denominator == b.Denominator;

    static double Bar(in TempoMapPoint p) => p.Numerator * 4.0 / Math.Max(1, (int)p.Denominator);

    static double BoundaryIn(in TempoMapPoint p, double quarter, EngineQuantize quantize) {
        var bar = Bar(p);
        var bars = quantize switch {
            EngineQuantize.Bar => 1,
            EngineQuantize.TwoBars => 2,
            EngineQuantize.FourBars => 4,
            _ => 0
        };
        if (bars > 0) {
            var span = bar * bars;
            return p.BarQuarter + Math.Ceiling((quarter - p.BarQuarter) / span - Epsilon) * span;
        }

        var step = quantize switch {
            EngineQuantize.Sixteenth => 0.25,
            EngineQuantize.Eighth => 0.5,
            EngineQuantize.Quarter => 1.0,
            _ => 2.0
        };
        var barStart = p.BarQuarter + Math.Floor((quarter - p.BarQuarter) / bar + Epsilon) * bar;
        var candidate = barStart + Math.Ceiling((quarter - barStart) / step - Epsilon) * step;
        return Math.Min(candidate, barStart + bar);
    }
}
