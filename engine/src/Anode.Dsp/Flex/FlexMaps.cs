namespace Anode.Dsp.Flex;

/// <summary>
///     Lookups in a warp map: the file frame an entry offset plays and the local rate there. Outside the map the first
///     and last segments extend linearly. <c>hint</c> is a segment index kept between calls, so sequential lookups cost
///     a step, not a search.
/// </summary>
internal static class WarpMap {
    /// <summary>The fractional file frame entry offset <paramref name="offset" /> plays.</summary>
    public static double FileFrameAt(ReadOnlySpan<FlexWarpPoint> warp, double offset, ref int hint) {
        var s = Segment(warp, offset, ref hint);
        var a = warp[s];
        var b = warp[s + 1];
        return a.FileFrame + (offset - a.Offset) * (b.FileFrame - a.FileFrame) / (b.Offset - a.Offset);
    }

    /// <summary>File frames per output frame at <paramref name="offset" /> (the slope of its segment).</summary>
    public static double RateAt(ReadOnlySpan<FlexWarpPoint> warp, double offset, ref int hint) {
        var s = Segment(warp, offset, ref hint);
        return (warp[s + 1].FileFrame - warp[s].FileFrame) / (warp[s + 1].Offset - warp[s].Offset);
    }

    /// <summary>The index of the segment (between points i and i + 1) holding <paramref name="offset" />.</summary>
    public static int Segment(ReadOnlySpan<FlexWarpPoint> warp, double offset, ref int hint) {
        var last = warp.Length - 2;
        var s = Math.Clamp(hint, 0, last);
        if (s < last && warp[s + 1].Offset <= offset) {
            s++;
            if (s < last && warp[s + 1].Offset <= offset) {
                s = Search(warp, offset);
            }
        } else if (s > 0 && warp[s].Offset > offset) {
            s--;
            if (s > 0 && warp[s].Offset > offset) {
                s = Search(warp, offset);
            }
        }

        hint = s;
        return s;
    }

    /// <summary>The last segment whose start is at or before <paramref name="offset" /> (0 before the map).</summary>
    static int Search(ReadOnlySpan<FlexWarpPoint> warp, double offset) {
        int lo = 0, hi = warp.Length - 2;
        while (lo < hi) {
            var mid = (lo + hi + 1) >>> 1;
            if (warp[mid].Offset <= offset) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo;
    }
}

/// <summary>
///     Lookups in pitch points: cents, formant cents and gain, linear between points by file frame and held beyond the
///     first and last. No points: no change.
/// </summary>
internal static class PitchCurve {
    /// <summary>The shift at <paramref name="fileFrame" />.</summary>
    public static (double Cents, double FormantCents, double GainDb) At(
        ReadOnlySpan<FlexPitchPoint> pitch,
        double fileFrame,
        ref int hint
    ) {
        if (pitch.IsEmpty) {
            return default;
        }

        if (fileFrame <= pitch[0].FileFrame) {
            return (pitch[0].Cents, pitch[0].FormantCents, pitch[0].GainDb);
        }

        var last = pitch.Length - 1;
        if (fileFrame >= pitch[last].FileFrame) {
            return (pitch[last].Cents, pitch[last].FormantCents, pitch[last].GainDb);
        }

        // Now pitch[i].FileFrame <= fileFrame < pitch[i + 1].FileFrame for some i < last.
        var i = Math.Clamp(hint, 0, last - 1);
        if (pitch[i].FileFrame > fileFrame || pitch[i + 1].FileFrame <= fileFrame) {
            if (i + 2 <= last && pitch[i + 1].FileFrame <= fileFrame && pitch[i + 2].FileFrame > fileFrame) {
                i++;
            } else {
                int lo = 0, hi = last - 1;
                while (lo < hi) {
                    var mid = (lo + hi + 1) >>> 1;
                    if (pitch[mid].FileFrame <= fileFrame) {
                        lo = mid;
                    } else {
                        hi = mid - 1;
                    }
                }

                i = lo;
            }
        }

        hint = i;
        var a = pitch[i];
        var b = pitch[i + 1];
        var span = b.FileFrame - a.FileFrame;
        var x = span > 0 ? (fileFrame - a.FileFrame) / span : 0;
        return (
            a.Cents + (b.Cents - a.Cents) * x,
            a.FormantCents + (b.FormantCents - a.FormantCents) * x,
            a.GainDb + (b.GainDb - a.GainDb) * x
        );
    }

    /// <summary>The linear gain at <paramref name="fileFrame" /> (1 without points).</summary>
    public static float GainAt(ReadOnlySpan<FlexPitchPoint> pitch, double fileFrame, ref int hint) {
        if (pitch.IsEmpty) {
            return 1;
        }

        var db = At(pitch, fileFrame, ref hint).GainDb;
        return db == 0 ? 1 : (float)Math.Pow(10, db / 20);
    }
}

/// <summary>
///     A window of a source's frames (interleaved), read in large blocks so that renderers call the source rarely.
///     Allocation-free after construction.
/// </summary>
internal sealed class SourceWindow {
    readonly int channels;
    readonly float[] data;
    readonly int frames;
    long start = long.MinValue;

    public SourceWindow(int channels, int frames) {
        this.channels = channels;
        this.frames = frames;
        data = new float[frames * channels];
    }

    public float[] Data => data;

    /// <summary>The most frames one <see cref="Locate" /> may ask for.</summary>
    public int Capacity => frames;

    /// <summary>
    ///     The index in <see cref="Data" /> of file frame <paramref name="first" />, with the <paramref name="count" />
    ///     frames from it in the window (reading them in, with a quarter of the window before them, if need be).
    /// </summary>
    public int Locate(IFlexSource source, long first, int count) {
        if (first < start || first + count > start + frames) {
            start = first - Math.Max(0, (frames - count) / 4);
            source.Read(start, data);
        }

        return (int)(first - start) * channels;
    }

    public void Invalidate() => start = long.MinValue;
}
