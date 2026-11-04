namespace Anode.Dsp.Metering;

/// <summary>
///     The last frames of a stereo signal for a display: the audio thread appends, the control thread reads. A read that
///     overlaps a write can tear a frame, which a display does not mind. Allocation-free after <see cref="Allocate" />.
/// </summary>
public sealed class StereoHistory {
    float[] left = [];
    float[] right = [];
    long written;

    /// <summary>How many frames it holds.</summary>
    public int Capacity => left.Length;

    /// <summary>How many frames have been appended since <see cref="Reset" />.</summary>
    public long Written => Volatile.Read(ref written);

    /// <summary>Control thread: holds at least <paramref name="frames" />.</summary>
    public void Allocate(int frames) {
        var size = 1;
        while (size < frames) {
            size <<= 1;
        }

        left = new float[size];
        right = new float[size];
        written = 0;
    }

    public void Reset() {
        Array.Clear(left);
        Array.Clear(right);
        Volatile.Write(ref written, 0);
    }

    /// <summary>Audio thread.</summary>
    public void Write(ReadOnlySpan<float> leftFrames, ReadOnlySpan<float> rightFrames) {
        var at = written;
        var mask = left.Length - 1;
        for (var i = 0; i < leftFrames.Length; i++) {
            left[(int)(at & mask)] = leftFrames[i];
            right[(int)(at & mask)] = rightFrames[i];
            at++;
        }

        Volatile.Write(ref written, at);
    }

    /// <summary>The left sample of frame <paramref name="at" /> (counted from the first ever written).</summary>
    public float Left(long at) => Read(left, at);

    /// <summary>The right sample of frame <paramref name="at" />.</summary>
    public float Right(long at) => Read(right, at);

    /// <summary>The mask comes from the array read, so a display that races a re-<see cref="Allocate" /> stays in bounds.</summary>
    static float Read(float[] ring, long at) => ring[(int)(at & (ring.Length - 1))];
}

/// <summary>
///     The stereo image over an exponential window: the correlation <c>E[LR] / √(E[L²] E[R²])</c> (+1 mono, 0 unrelated,
///     −1 out of phase; 0 in silence) and the balance (dB, right over left; 0 unless both channels have signal).
/// </summary>
public struct StereoImage {
    const double Silent = 1e-12;

    double product;
    double squareLeft;
    double squareRight;

    public void Reset() {
        product = 0;
        squareLeft = 0;
        squareRight = 0;
    }

    /// <summary>One frame through the window, <paramref name="coefficient" /> the one-pole's feedback.</summary>
    public void Tick(double coefficient, double left, double right) {
        product = coefficient * product + (1 - coefficient) * left * right;
        squareLeft = coefficient * squareLeft + (1 - coefficient) * left * left;
        squareRight = coefficient * squareRight + (1 - coefficient) * right * right;
    }

    public readonly double Correlation {
        get {
            var energy = Math.Sqrt(squareLeft * squareRight);
            return energy > Silent ? Math.Clamp(product / energy, -1, 1) : 0;
        }
    }

    public readonly double BalanceDb =>
        squareLeft > Silent && squareRight > Silent ? 10 * Math.Log10(squareRight / squareLeft) : 0;

    /// <summary>Zeroes state that has decayed into the subnormal range.</summary>
    public void FlushDenormals() {
        if (Math.Sqrt(squareLeft * squareRight) < 1e-30) {
            Reset();
        }
    }
}
