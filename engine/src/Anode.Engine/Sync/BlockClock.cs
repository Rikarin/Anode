namespace Anode.Engine.Sync;

/// <summary>
///     The host time of each audio callback, smoothed (a delay-locked loop over the callbacks' wake-up times, after
///     Adriaensen's "Using a DLL to filter time"), so sync messages stamped from it are as even as the device's sample
///     clock rather than as jittery as the thread's wake-ups. A callback that comes more than two periods off the
///     prediction (a dropout, a new buffer size) restarts it. Audio thread only; no allocation.
/// </summary>
public sealed class BlockClock {
    /// <summary>The loop's bandwidth, in hertz.</summary>
    const double Bandwidth = 0.5;

    double predicted; // the next callback's time, ns
    double period; // ns per callback
    double b;
    double c;
    int frames;
    bool started;

    /// <summary>
    ///     The smoothed time of the callback that started at <paramref name="nowNs" /> with
    ///     <paramref name="callbackFrames" />.
    /// </summary>
    public long Update(long nowNs, int callbackFrames, int sampleRate) {
        var nominal = callbackFrames * 1e9 / sampleRate;
        if (!started || callbackFrames != frames || Math.Abs(nowNs - predicted) > 2 * nominal) {
            started = true;
            frames = callbackFrames;
            period = nominal;
            predicted = nowNs + nominal;
            var omega = 2 * Math.PI * Bandwidth * nominal / 1e9;
            b = Math.Sqrt(2) * omega;
            c = omega * omega;
            return nowNs;
        }

        var error = nowNs - predicted;
        var current = predicted;
        predicted += b * error + period;
        period += c * error;
        return (long)current;
    }

    /// <summary>Starts over at the next callback.</summary>
    public void Reset() => started = false;
}
