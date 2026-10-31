namespace Anode.Engine.Sync;

/// <summary>
///     The external tempo from MIDI Clock ticks (docs/09-phase-8.md, workstream A): a second-order loop over the ticks'
///     intervals, within 0.5 bpm after two beats and through a step change, ignoring jitter. No allocation.
/// </summary>
/// <remarks>
///     <para>
///         Locking: the first two beats (48 ticks) are averaged (the span from the first tick to the last over the
///         intervals between them, so a tick's jitter counts once, not twice); then a delay-locked loop (Adriaensen's,
///         critically damped) predicts each tick and corrects its period by the error, with a bandwidth of 1/192 of
///         the tick rate (0.25 Hz at 120 bpm), which leaves a millisecond of jitter well under 0.1 bpm.
///     </para>
///     <para>
///         A step: four ticks in a row off the prediction by more than a tenth of a period, the same way, restart the
///         average from the last of them. It reports as soon as twelve intervals are in (half a beat) and hands over to
///         the loop at two beats, so a new tempo is followed within a beat. A single late or early tick (an outlier)
///         moves the loop by at most half a period's error.
///     </para>
/// </remarks>
public sealed class TempoEstimator {
    const int LockTicks = 48;
    const int StepReportTicks = 12;
    const int StepRun = 4;
    const double StepThreshold = 0.1;

    double tempo;
    bool locked;
    bool looping; // the loop runs (else the average)
    bool relocking; // averaging after a step: reports early
    long firstNs;
    long lastNs;
    int ticks; // since firstNs, the first counted
    double period; // ns per tick
    double predicted; // the next tick's time, ns
    double b;
    double c;
    int run; // consecutive same-sign large errors
    int runSign;

    /// <summary>The estimated tempo in bpm; 0 until two beats of ticks have arrived.</summary>
    public double Tempo => tempo;

    /// <summary>Whether ticks arrive steadily enough to follow (they stop for more than 3 ticks' time: not locked).</summary>
    public bool Locked => locked;

    /// <summary>A clock tick at <paramref name="timeNs" />; returns the new estimate.</summary>
    public double Tick(long timeNs) {
        if (ticks > 0 && period > 0 && timeNs - lastNs > 3 * period && tempo > 0) {
            Restart(timeNs, false); // they stopped and came back: lock again, keeping the last tempo meanwhile
            return tempo;
        }

        if (ticks == 0) {
            Restart(timeNs, false);
            return tempo;
        }

        if (!looping) {
            Average(timeNs);
            return tempo;
        }

        var error = timeNs - predicted;
        var sign = Math.Sign(error);
        if (Math.Abs(error) > StepThreshold * period) {
            if (sign == runSign && run > 0) {
                run++;
            } else {
                run = 1;
                runSign = sign;
            }

            if (run >= StepRun) {
                Restart(timeNs, true);
                return tempo;
            }
        } else {
            run = 0;
        }

        var bounded = Math.Clamp(error, -0.5 * period, 0.5 * period);
        predicted += b * bounded + period;
        period += c * bounded;
        lastNs = timeNs;
        ticks++;
        locked = true;
        tempo = 60e9 / (24 * period);
        return tempo;
    }

    /// <summary>Whether ticks stopped arriving by <paramref name="nowNs" /> (updates <see cref="Locked" />).</summary>
    public bool Check(long nowNs) {
        if (locked && period > 0 && nowNs - lastNs > 3 * period) {
            locked = false;
        }

        return !locked;
    }

    /// <summary>Forgets the ticks (on Start, or when the source changes).</summary>
    public void Reset() {
        tempo = 0;
        locked = false;
        looping = false;
        relocking = false;
        ticks = 0;
        period = 0;
        run = 0;
    }

    void Restart(long timeNs, bool step) {
        firstNs = timeNs;
        lastNs = timeNs;
        ticks = 1;
        looping = false;
        relocking = step || tempo > 0;
        run = 0;
    }

    void Average(long timeNs) {
        lastNs = timeNs;
        ticks++;
        var intervals = ticks - 1;
        period = (double)(lastNs - firstNs) / intervals;
        if (intervals >= LockTicks) {
            StartLoop(timeNs);
        }

        if (intervals >= LockTicks || (relocking && intervals >= StepReportTicks)) {
            tempo = 60e9 / (24 * period);
            locked = true;
        }
    }

    void StartLoop(long timeNs) {
        looping = true;
        relocking = false;
        predicted = timeNs + period;
        // ω = 2π·B·T with B·T = 1/192: the bandwidth as a fraction of the tick rate.
        var omega = 2 * Math.PI / 192;
        b = Math.Sqrt(2) * omega;
        c = omega * omega;
    }
}
