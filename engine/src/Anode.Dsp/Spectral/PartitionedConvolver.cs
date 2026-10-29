using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anode.Dsp.Spectral;

/// <summary>
///     Zero-latency convolution of one channel with a long filter, one sample at a time: uniformly partitioned FFT
///     convolution (overlap-save with a frequency-domain delay line, Wefers, "Partitioned convolution algorithms for
///     real-time auralization", 2015) behind a direct-form head.
///     <list type="bullet">
///         <item>
///             The filter is cut into partitions of <see cref="BlockSize" /> taps. The first, the head, runs as a direct
///             FIR on every sample, so the output has no latency.
///         </item>
///         <item>
///             The others run in the frequency domain: every <see cref="BlockSize" /> samples the last two blocks of input
///             go through a real FFT of twice the block into the delay line of spectra, and the next block's output from
///             partitions 1 … P − 1 is one sum of products and one inverse FFT. That block only needs input up to the
///             block just finished, which is why the head can cover the rest.
///         </item>
///     </list>
///     The work happens on a grid of samples counted from <see cref="Reset" />, so the output does not depend on how the
///     caller divides time into buffers. Each partition's contribution can be scaled (<see cref="SetPartitionGain" />),
///     which is how a reverb shortens its impulse response without recomputing it. Single precision; allocation-free
///     after <see cref="Allocate" />.
/// </summary>
public sealed class PartitionedConvolver {
    int block;
    int stride;
    int maxPartitions;
    int partitions;
    RealFft fft = new(4);
    float[] head = [];
    float[] history = [];
    float[] filterRe = [];
    float[] filterIm = [];
    float[] lineRe = [];
    float[] lineIm = [];
    float[] gains = [];
    float[] input = [];
    float[] tail = [];
    float[] sumRe = [];
    float[] sumIm = [];
    float[] time = [];
    int at;
    int historyAt;
    int lineAt;

    /// <summary>The partition size in taps (and the period of the FFT work, in samples).</summary>
    public int BlockSize => block;

    /// <summary>The number of partitions of the filter set, the head included.</summary>
    public int Partitions => partitions;

    /// <summary>The longest filter <see cref="SetFilter" /> takes.</summary>
    public int MaxTaps => maxPartitions * block;

    /// <summary>
    ///     Control thread: room for filters of up to <paramref name="maxTaps" /> taps in partitions of
    ///     <paramref name="blockSize" />.
    /// </summary>
    public void Allocate(int blockSize, int maxTaps) {
        if (blockSize < 4 || (blockSize & (blockSize - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "A power of two of at least 4.");
        }

        block = blockSize;
        fft = new(2 * block);
        var vector = Vector<float>.Count;
        stride = (fft.Bins + vector - 1) / vector * vector;
        maxPartitions = Math.Max(1, (maxTaps + block - 1) / block);
        head = new float[block];
        history = new float[2 * block];
        filterRe = new float[maxPartitions * stride];
        filterIm = new float[maxPartitions * stride];
        lineRe = new float[maxPartitions * stride];
        lineIm = new float[maxPartitions * stride];
        gains = new float[maxPartitions];
        gains.AsSpan().Fill(1);
        input = new float[2 * block];
        tail = new float[block];
        sumRe = new float[stride];
        sumIm = new float[stride];
        time = new float[2 * block];
        partitions = 0;
        Reset();
    }

    /// <summary>
    ///     The filter (at most <see cref="MaxTaps" /> taps; more are ignored). No allocation, so a filter can change on
    ///     the audio thread; the history stays, so the change is a jump in the response, not a restart.
    /// </summary>
    public void SetFilter(ReadOnlySpan<float> taps) {
        var count = Math.Min(taps.Length, MaxTaps);
        partitions = Math.Max(1, (count + block - 1) / block);
        // The head reversed, so its sum runs over the history in time order.
        for (var k = 0; k < block; k++) {
            head[block - 1 - k] = k < count ? taps[k] : 0;
        }

        // Partition p's spectrum (the inverse transform scales).
        for (var p = 1; p < maxPartitions; p++) {
            var re = filterRe.AsSpan(p * stride, stride);
            var im = filterIm.AsSpan(p * stride, stride);
            if (p >= partitions) {
                re.Clear();
                im.Clear();
                continue;
            }

            var from = p * block;
            var length = Math.Min(block, count - from);
            time.AsSpan().Clear();
            taps.Slice(from, length).CopyTo(time);
            fft.Forward(time, re, im);
        }
    }

    /// <summary>
    ///     Scales the contribution of partition <paramref name="partition" /> (1 or more; the head is always whole).
    ///     Takes effect from the next block.
    /// </summary>
    public void SetPartitionGain(int partition, float gain) {
        if (partition >= 1 && partition < maxPartitions) {
            gains[partition] = gain;
        }
    }

    /// <summary>Clears the history, so the next sample starts the grid.</summary>
    public void Reset() {
        Array.Clear(history);
        Array.Clear(lineRe);
        Array.Clear(lineIm);
        Array.Clear(input);
        Array.Clear(tail);
        at = 0;
        historyAt = 0;
        lineAt = 0;
    }

    /// <summary>One sample in, one out.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Tick(float x) {
        // The history is a ring of one block written twice, so the newest block is always contiguous.
        historyAt = historyAt + 1 == block ? 0 : historyAt + 1;
        history[historyAt] = x;
        history[historyAt + block] = x;
        var y = Dot(head, history.AsSpan(historyAt + 1, block)) + tail[at];
        input[block + at] = x;
        if (++at == block) {
            Advance();
        }

        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var sum = Vector<float>.Zero;
        for (var i = 0; i < va.Length; i++) {
            sum += va[i] * vb[i];
        }

        var total = Vector.Sum(sum);
        for (var i = va.Length * Vector<float>.Count; i < a.Length; i++) {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary>A block is complete: its spectrum into the delay line, the next block's tail out.</summary>
    void Advance() {
        at = 0;
        if (partitions <= 1) {
            input.AsSpan(block).CopyTo(input);
            Array.Clear(tail);
            return;
        }

        lineAt = lineAt + 1 == maxPartitions ? 0 : lineAt + 1;
        fft.Forward(input, lineRe.AsSpan(lineAt * stride, stride), lineIm.AsSpan(lineAt * stride, stride));
        input.AsSpan(block).CopyTo(input);

        var sr = MemoryMarshal.Cast<float, Vector<float>>(sumRe.AsSpan());
        var si = MemoryMarshal.Cast<float, Vector<float>>(sumIm.AsSpan());
        sr.Clear();
        si.Clear();
        var line = lineAt;
        for (var p = 1; p < partitions; p++) {
            // Partition p meets the spectrum of p − 1 blocks ago (the newest for p = 1).
            var g = new Vector<float>(gains[p]);
            var xr = MemoryMarshal.Cast<float, Vector<float>>(lineRe.AsSpan(line * stride, stride));
            var xi = MemoryMarshal.Cast<float, Vector<float>>(lineIm.AsSpan(line * stride, stride));
            var hr = MemoryMarshal.Cast<float, Vector<float>>(filterRe.AsSpan(p * stride, stride));
            var hi = MemoryMarshal.Cast<float, Vector<float>>(filterIm.AsSpan(p * stride, stride));
            for (var i = 0; i < sr.Length; i++) {
                var ar = xr[i];
                var ai = xi[i];
                var br = hr[i] * g;
                var bi = hi[i] * g;
                sr[i] += ar * br - ai * bi;
                si[i] += ar * bi + ai * br;
            }

            line = line == 0 ? maxPartitions - 1 : line - 1;
        }

        fft.Inverse(sumRe, sumIm, time);
        // Overlap-save: the second half is the linear convolution.
        time.AsSpan(block, block).CopyTo(tail);
    }
}
