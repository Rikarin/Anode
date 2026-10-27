namespace Anode.Spikes.Stretch;

/// <summary>
///     A streaming render, as the engine would drive the stretcher: fixed-size output blocks, a variable number of input
///     frames per call. <see cref="Content" /> maps each output frame to the input time (in frames) it should play.
/// </summary>
sealed record Rendered(float[][] Audio, double[] Content, long[] CallTicks, int Calls, long TotalTicks) {
    /// <summary>The output frame at which input frame <paramref name="inputFrame" /> should sound.</summary>
    public double OutputFrameOf(double inputFrame) {
        var lo = 0;
        var hi = Content.Length - 1;
        if (inputFrame <= Content[0]) {
            return 0;
        }

        if (inputFrame >= Content[hi]) {
            return hi;
        }

        while (hi - lo > 1) {
            var mid = (lo + hi) / 2;
            if (Content[mid] <= inputFrame) {
                lo = mid;
            } else {
                hi = mid;
            }
        }

        return lo + (inputFrame - Content[lo]) / (Content[hi] - Content[lo]);
    }
}

static class Renderer {
    /// <summary>
    ///     Stretches <paramref name="input" /> with a playback rate (input frames per output frame) that may follow the
    ///     output position: <c>rateAt(outputFrame)</c>. Starts aligned (outputSeek), feeds input
    ///     <c>inputLatency</c> ahead of the processing time, and ends with <c>inputLatency</c> of silence plus a flush.
    /// </summary>
    public static Rendered Render(Stretcher stretcher, float[][] input, Func<long, double> rateAt, int blockFrames) {
        var length = input[0].Length;
        var inputLatency = stretcher.InputLatency;
        var outputLatency = stretcher.OutputLatency;

        // Output frame -> input time: the integral of the rate. Extended past the end so the look-ahead can index it.
        var content = new List<double>(length * 2) { 0 };
        var maxRate = 0.0;
        for (long k = 0; content[^1] < length; k++) {
            var rate = rateAt(k);
            maxRate = Math.Max(maxRate, rate);
            content.Add(content[^1] + rate);
        }

        var total = content.Count - 1;
        var lastRate = rateAt(total);
        for (var k = 0; k < outputLatency + blockFrames + 2; k++) {
            content.Add(content[^1] + lastRate);
        }

        var pad = (int)Math.Ceiling(inputLatency + (outputLatency + blockFrames + 4) * Math.Max(maxRate, lastRate))
            + 16;
        var source = Planar.From(input, pad);
        var output = new Planar(input.Length, total + blockFrames);

        var seek = stretcher.OutputSeekLength(rateAt(0));
        stretcher.OutputSeek(source, seek);
        long fed = seek;
        long done = 0;
        var processEnd = Math.Max(0, total - outputLatency);
        var ticks = new long[processEnd / blockFrames + 2];
        var calls = 0;
        long totalTicks = 0;
        while (done < processEnd) {
            var n = (int)Math.Min(blockFrames, processEnd - done);
            var target = (long)Math.Round(inputLatency + content[(int)(done + n + outputLatency)]);
            var take = (int)Math.Max(0, target - fed);
            var t = stretcher.Process(source, fed, take, output, done, n);
            ticks[calls++] = t;
            totalTicks += t;
            fed += take;
            done += n;
        }

        totalTicks += stretcher.Flush(output, done, (int)(total - done), lastRate);

        var audio = new float[input.Length][];
        for (var c = 0; c < input.Length; c++) {
            audio[c] = output.Data[c].AsSpan(0, total).ToArray();
        }

        return new(audio, [..content.GetRange(0, total + 1)], ticks, calls, totalTicks);
    }

    public static Rendered Render(Stretcher stretcher, float[][] input, double ratio, int blockFrames) {
        var rate = 1.0 / ratio;
        return Render(stretcher, input, _ => rate, blockFrames);
    }
}
