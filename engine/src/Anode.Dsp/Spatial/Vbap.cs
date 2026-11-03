namespace Anode.Dsp.Spatial;

/// <summary>
///     An object's position (the contract's <c>ObjectPanner</c>): azimuth and elevation in degrees, distance 0…1, size
///     0…1.
/// </summary>
public readonly record struct ObjectPosition(float Azimuth, float Elevation, float Distance, float Size) {
    /// <summary>
    ///     The ADM extent of a size (<c>width</c> and <c>height</c> in degrees, both this): a cap of angular radius
    ///     180° × size around the position, so size 1 covers the sphere.
    /// </summary>
    public static double ExtentDegrees(double size) => 360 * Math.Clamp(size, 0, 1);

    /// <summary>
    ///     The level of an object at <paramref name="distance" /> (docs/spatial/README.md): distance only changes the
    ///     level, by the inverse of the distance from the sphere inwards, at most +6 dB (from 0.5 to 0); 1 and beyond is 0 dB.
    /// </summary>
    public static float DistanceGain(float distance) => 1 / Math.Clamp(distance, 0.5f, 1f);
}

/// <summary>
///     Vector base amplitude panning over a layout's triangulation (docs/adr/0012-spatial-audio.md, workstream D),
///     modelled on the EBU ADM Renderer's point-source panner (virtual speakers where the layout has gaps, the
///     triangulation of its convex hull, gains normalised for power), with size spread over neighbouring directions.
///     The LFE takes no object signal.
/// </summary>
/// <remarks>
///     Size follows BS.2127's polar extent panner for a circular extent: the point-source gains of a fixed grid of
///     directions (5° rows, as the reference's 37 rows) weighted 1 inside the cap and fading to 0 over 10° outside it,
///     summed and normalised; below an extent of 10° it cross-fades in power from the point source. Distance is not
///     part of the gains here (<see cref="ObjectRenderer" /> applies <see cref="ObjectPosition.DistanceGain" />).
/// </remarks>
public sealed class Vbap {
    const double FadeDegrees = 10;
    readonly PointSourcePanner panner;
    readonly int[] channels;
    readonly int speakers;
    readonly double[] gridX;
    readonly double[] gridY;
    readonly double[] gridZ;
    readonly float[] gridGains;

    /// <summary>Off the audio thread: triangulates <paramref name="layout" /> (the triplets and their inverse matrices).</summary>
    public Vbap(SpeakerLayout layout) {
        Layout = layout;
        channels = [..Enumerable.Range(0, layout.ChannelCount).Where(c => c != layout.LfeIndex)];
        speakers = channels.Length;
        panner = new(
            [..channels.Select(c => (double)layout.Azimuths[c])],
            [..channels.Select(c => (double)layout.Elevations[c])],
            [..channels.Select(layout.Label)]
        );

        var grid = Grid(37);
        gridX = [..grid.Select(static g => g.X)];
        gridY = [..grid.Select(static g => g.Y)];
        gridZ = [..grid.Select(static g => g.Z)];
        gridGains = new float[grid.Count * speakers];
        var gains = new double[speakers];
        var scratch = new double[panner.ScratchLength];
        for (var i = 0; i < grid.Count; i++) {
            panner.Pan(grid[i], gains, scratch);
            for (var s = 0; s < speakers; s++) {
                gridGains[i * speakers + s] = (float)gains[s];
            }
        }
    }

    public SpeakerLayout Layout { get; }

    /// <summary>The directions of the extent grid: BS.2127's evenly spread points, <paramref name="rows" /> rows pole to pole.</summary>
    static List<Vector3> Grid(int rows) {
        var points = new List<Vector3>();
        for (var r = 0; r < rows; r++) {
            var elevation = -90 + 180.0 * r / (rows - 1);
            var count = (int)Math.Round(Math.Cos(elevation * Math.PI / 180) * 2 * (rows - 1), MidpointRounding.ToEven);
            count = Math.Max(count, 1);
            for (var k = 0; k < count; k++) {
                points.Add(Vector3.FromPolar(360.0 * k / count, elevation));
            }
        }

        return points;
    }

    /// <summary>
    ///     The gain of each of the layout's channels for an object at <paramref name="position" />, power-normalised;
    ///     real-time safe.
    /// </summary>
    public void Gains(in ObjectPosition position, Span<float> gains) {
        Span<double> point = stackalloc double[speakers];
        Span<double> scratch = stackalloc double[panner.ScratchLength];
        var direction = Vector3.FromPolar(position.Azimuth, position.Elevation);
        panner.Pan(direction, point, scratch);

        var extent = ObjectPosition.ExtentDegrees(position.Size);
        var spread = Math.Clamp(extent / FadeDegrees, 0, 1);
        if (spread > 1e-10) {
            Span<double> wide = stackalloc double[speakers];
            Spread(direction, Math.Max(extent, FadeDegrees / 2), wide);
            for (var s = 0; s < speakers; s++) {
                point[s] = Math.Sqrt((1 - spread) * point[s] * point[s] + spread * wide[s] * wide[s]);
            }
        }

        gains[..Layout.ChannelCount].Clear();
        for (var s = 0; s < speakers; s++) {
            gains[channels[s]] = (float)point[s];
        }
    }

    /// <summary>The spread gains of a circular extent <paramref name="extent" /> degrees across around <paramref name="direction" />.</summary>
    void Spread(Vector3 direction, double extent, Span<double> gains) {
        var radius = extent / 2 * Math.PI / 180;
        var fade = FadeDegrees * Math.PI / 180;
        var outer = Math.Cos(Math.Min(Math.PI, radius + fade));
        var inner = Math.Cos(Math.Min(Math.PI, radius));
        gains.Clear();
        for (var i = 0; i < gridX.Length; i++) {
            var cos = direction.X * gridX[i] + direction.Y * gridY[i] + direction.Z * gridZ[i];
            if (cos < outer) {
                continue;
            }

            var weight = cos >= inner ? 1 : 1 - (Math.Acos(Math.Clamp(cos, -1, 1)) - radius) / fade;
            if (weight <= 0) {
                continue;
            }

            var row = gridGains.AsSpan(i * speakers, speakers);
            for (var s = 0; s < speakers; s++) {
                gains[s] += weight * row[s];
            }
        }

        PointSourcePanner.Normalise(gains);
    }
}

/// <summary>
///     Renders objects to the master's layout at the master (after the bed sum, before the master's inserts): each
///     object's mono or stereo signal through <see cref="Vbap" /> gains, ramped from the previous block's to this
///     block's position (the position automation delivers per block, sample-accurately at its events). Real-time safe
///     after <see cref="Prepare" />.
/// </summary>
/// <remarks>
///     A block's positions are points of a piecewise-linear gain curve: a position set at frame f &gt; 0 is reached at
///     f, ramping linearly in gain from the previous point (the end of the last block, to start with); one set at frame
///     0 (the per-block default) is reached at the end of the block, so per-block positions glide over each block; after
///     the last point the gains hold. A stereo object's two channels take the same gains (ADM gives both the same
///     blocks). Gains include <see cref="ObjectPosition.DistanceGain" />. At most <see cref="MaxPoints" /> positions
///     per object per block (later ones replace the last).
/// </remarks>
public sealed class ObjectRenderer {
    /// <summary>The ADM profile's limit for a 7.1.4 bed (docs/11-phase-10.md).</summary>
    public const int MaxObjects = 118;

    /// <summary>Positions an object takes per block.</summary>
    public const int MaxPoints = 16;

    Vbap? vbap;
    int channels;
    int capacity;
    float[] current = [];
    float[] targets = [];
    int[] pointFrames = [];
    int[] pointCounts = [];
    bool[] started = [];
    ObjectPosition[] last = [];
    bool[] hasLast = [];
    float[] lastGains = [];

    /// <summary>Off the audio thread: the layout's VBAP and gain state for up to <paramref name="maxObjects" /> objects.</summary>
    public void Prepare(SpeakerLayout layout, int maxObjects, float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxObjects, MaxObjects);
        if (vbap?.Layout != layout) {
            vbap = new(layout);
        }

        channels = layout.ChannelCount;
        capacity = maxObjects;
        current = new float[maxObjects * channels];
        targets = new float[maxObjects * MaxPoints * channels];
        pointFrames = new int[maxObjects * MaxPoints];
        pointCounts = new int[maxObjects];
        started = new bool[maxObjects];
        last = new ObjectPosition[maxObjects];
        hasLast = new bool[maxObjects];
        lastGains = new float[maxObjects * channels];
    }

    /// <summary>Object <paramref name="index" />'s position from the next block (or from <paramref name="frame" /> within it).</summary>
    public void SetPosition(int index, in ObjectPosition position, int frame = 0) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, capacity);
        var count = pointCounts[index];
        var slot = Math.Min(count, MaxPoints - 1);
        var gains = targets.AsSpan((index * MaxPoints + slot) * channels, channels);
        var cached = lastGains.AsSpan(index * channels, channels);
        if (!hasLast[index] || last[index] != position) {
            vbap!.Gains(position, cached);
            var level = ObjectPosition.DistanceGain(position.Distance);
            for (var c = 0; c < channels; c++) {
                cached[c] *= level;
            }

            last[index] = position;
            hasLast[index] = true;
        }

        cached.CopyTo(gains);
        pointFrames[index * MaxPoints + slot] = Math.Max(0, frame);
        pointCounts[index] = slot + 1;
        if (!started[index]) {
            // The first position after a reset: no ramp from silence.
            gains.CopyTo(current.AsSpan(index * channels, channels));
            started[index] = true;
        }
    }

    /// <summary>
    ///     Adds object <paramref name="index" />'s signal (one or two channels), rendered, into
    ///     <paramref name="output" />.
    /// </summary>
    public void Render(int index, PlanarBlock input, PlanarBlock output) {
        var frames = Math.Min(input.Frames, output.Frames);
        var gains = current.AsSpan(index * channels, channels);
        var count = pointCounts[index];
        var at = 0;
        for (var p = 0; p < count && at < frames; p++) {
            var frame = pointFrames[index * MaxPoints + p];
            var end = frame == 0 && count == 1 ? frames : Math.Min(frame, frames);
            var target = targets.AsSpan((index * MaxPoints + p) * channels, channels);
            if (end <= at) {
                target.CopyTo(gains);
                continue;
            }

            Ramp(input, output, gains, target, at, end);
            target.CopyTo(gains);
            at = end;
        }

        if (at < frames) {
            Ramp(input, output, gains, gains, at, frames);
        }

        pointCounts[index] = 0;
    }

    void Ramp(PlanarBlock input, PlanarBlock output, ReadOnlySpan<float> from, ReadOnlySpan<float> to, int start, int end) {
        var length = end - start;
        var inverse = 1f / length;
        for (var c = 0; c < channels; c++) {
            var g0 = from[c];
            var g1 = to[c];
            if (g0 == 0 && g1 == 0) {
                continue;
            }

            var step = (g1 - g0) * inverse;
            var destination = output.Channel(c).Slice(start, length);
            for (var k = 0; k < input.Channels; k++) {
                var source = input.Channel(k).Slice(start, length);
                if (g0 == g1) {
                    for (var n = 0; n < length; n++) {
                        destination[n] += g0 * source[n];
                    }
                } else {
                    for (var n = 0; n < length; n++) {
                        destination[n] += (g0 + step * (n + 1)) * source[n];
                    }
                }
            }
        }
    }

    /// <summary>Forgets object <paramref name="index" />'s previous gains (it starts at its position, no ramp).</summary>
    public void Reset(int index) {
        started[index] = false;
        pointCounts[index] = 0;
        current.AsSpan(index * channels, channels).Clear();
    }
}
