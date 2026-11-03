namespace Anode.Dsp.Spatial;

/// <summary>A Cartesian vector in ADM's convention: x to the right, y ahead, z up.</summary>
readonly record struct Vector3(double X, double Y, double Z) {
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(Vector3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public Vector3 Cross(Vector3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Length => Math.Sqrt(Dot(this));

    /// <summary>ADM polar (degrees, azimuth positive to the left) to a unit vector, as the EBU ADM Renderer's <c>cart</c>.</summary>
    public static Vector3 FromPolar(double azimuth, double elevation) {
        var a = azimuth * Math.PI / 180;
        var e = elevation * Math.PI / 180;
        return new(-Math.Sin(a) * Math.Cos(e), Math.Cos(a) * Math.Cos(e), Math.Sin(e));
    }
}

/// <summary>
///     The EBU ADM Renderer's point-source panner (ITU-R BS.2127 §6.1, <c>ear.core.point_source</c>), ported: the
///     layout (without its LFE) plus extra speakers that fill the layers it lacks (each mixed down to the middle-layer
///     speaker below or above it) and virtual speakers at the poles, triangulated as their convex hull; facets touching a
///     pole are virtual n-gons (the pole's gain spread equally over the ring), the others VBAP triplets or quad regions.
///     The gains are normalised for power. Stereo pans over 0+5+0 and down-mixes as BS.2127 does; LCR (not a BS.2051
///     system) does the same keeping its centre; mono takes everything. Built off the audio thread;
///     <see cref="Pan" /> allocates nothing and may run on any number of threads at once.
/// </summary>
sealed class PointSourcePanner {
    readonly Region[] regions;
    readonly double[,]? downmix;
    readonly int extended;
    readonly Kind kind;
    readonly PointSourcePanner? inner;

    enum Kind {
        Full,
        Single,
        Stereo,
        Lcr
    }

    /// <param name="azimuths">The speakers' azimuths (no LFE), in output order.</param>
    /// <param name="elevations">Their elevations.</param>
    /// <param name="labels">Their ADM labels, which pick the special cases (stereo, LCR, mono).</param>
    public PointSourcePanner(double[] azimuths, double[] elevations, string[] labels) {
        Channels = azimuths.Length;
        regions = [];
        if (Channels == 1) {
            kind = Kind.Single;
            return;
        }

        if (Channels == 2 && labels.Contains("M+030") && labels.Contains("M-030")) {
            kind = Kind.Stereo;
            Left = Array.IndexOf(labels, "M+030");
            Right = Array.IndexOf(labels, "M-030");
            inner = FivePointZero();
            return;
        }

        if (Channels == 3 && labels.Contains("M+030") && labels.Contains("M-030") && labels.Contains("M+000")) {
            kind = Kind.Lcr;
            Left = Array.IndexOf(labels, "M+030");
            Right = Array.IndexOf(labels, "M-030");
            Centre = Array.IndexOf(labels, "M+000");
            inner = FivePointZero();
            return;
        }

        kind = Kind.Full;
        var (positions, mix) = WithExtraSpeakers(azimuths, elevations);
        extended = positions.Count;
        downmix = mix;
        regions = Triangulate(positions);
    }

    public int Channels { get; }
    int Left { get; }
    int Right { get; }
    int Centre { get; }

    /// <summary>The scratch <see cref="Pan" /> needs: the extended speakers' gains and a region's own.</summary>
    public int ScratchLength => inner?.ScratchLength ?? extended + 8;

    static PointSourcePanner FivePointZero() =>
        new([30, -30, 0, 110, -110], [0, 0, 0, 0, 0], ["M+030", "M-030", "M+000", "M+110", "M-110"]);

    /// <summary>
    ///     The power-normalised gains of a source in direction <paramref name="direction" /> (any length) into
    ///     <paramref name="gains" /> (<see cref="Channels" />); <paramref name="scratch" /> holds at least
    ///     <see cref="ScratchLength" /> values. False (and zeros) only for a direction no region covers, which a layout
    ///     around the listener never has.
    /// </summary>
    public bool Pan(Vector3 direction, Span<double> gains, Span<double> scratch) {
        gains[..Channels].Clear();
        switch (kind) {
            case Kind.Single:
                gains[0] = 1;
                return true;
            case Kind.Stereo:
            case Kind.Lcr:
                return PanFront(direction, gains, scratch);
        }

        var extendedGains = scratch[..extended];
        foreach (var region in regions) {
            extendedGains.Clear();
            if (!region.Handle(direction, extendedGains, scratch[extended..])) {
                continue;
            }

            var mix = downmix!;
            for (var e = 0; e < extended; e++) {
                if (extendedGains[e] == 0) {
                    continue;
                }

                for (var c = 0; c < Channels; c++) {
                    gains[c] += mix[c, e] * extendedGains[e];
                }
            }

            Normalise(gains[..Channels]);
            return true;
        }

        return false;
    }

    /// <summary>BS.2127's stereo panner: 0+5+0, down-mixed (centre at √3/3 for the velocity, surrounds at √½), then 0 dB at the front to −3 dB at the back.</summary>
    bool PanFront(Vector3 direction, Span<double> gains, Span<double> scratch) {
        Span<double> five = stackalloc double[5];
        if (!inner!.Pan(direction, five, scratch)) {
            return false;
        }

        var half = Math.Sqrt(0.5);
        if (kind == Kind.Stereo) {
            var centre = Math.Sqrt(3) / 3;
            gains[Left] = five[0] + centre * five[2] + half * five[3];
            gains[Right] = five[1] + centre * five[2] + half * five[4];
        } else {
            gains[Left] = five[0] + half * five[3];
            gains[Right] = five[1] + half * five[4];
            gains[Centre] = five[2];
        }

        Normalise(gains[..Channels]);
        var front = Math.Max(five[0], Math.Max(five[1], five[2]));
        var back = Math.Max(five[3], five[4]);
        var level = Math.Pow(0.5, 0.5 * back / (front + back));
        for (var c = 0; c < Channels; c++) {
            gains[c] *= level;
        }

        return true;
    }

    internal static void Normalise(Span<double> values) {
        var sum = 0.0;
        foreach (var v in values) {
            sum += v * v;
        }

        if (sum <= 0) {
            return;
        }

        var scale = 1 / Math.Sqrt(sum);
        for (var i = 0; i < values.Length; i++) {
            values[i] *= scale;
        }
    }

    /// <summary>
    ///     BS.2127's <c>extra_pos_vertical_nominal</c>: for each layer (−30°, +30°) a copy of every middle-layer speaker
    ///     whose azimuth is beyond the layer's widest speaker plus 40° (all of them when the layer is empty), at the
    ///     layer's elevation, mixed down to that middle-layer speaker.
    /// </summary>
    static (List<Vector3> Positions, double[,] Downmix) WithExtraSpeakers(double[] azimuths, double[] elevations) {
        var n = azimuths.Length;
        var positions = new List<Vector3>();
        var sources = new List<int>();
        for (var c = 0; c < n; c++) {
            positions.Add(Vector3.FromPolar(azimuths[c], elevations[c]));
            sources.Add(c);
        }

        (double Nominal, double Low, double High)[] layers = [(-30, -70, -10), (30, 10, 70)];
        foreach (var (nominal, low, high) in layers) {
            var inLayer = Enumerable.Range(0, n).Where(c => elevations[c] >= low && elevations[c] <= high).ToArray();
            double limit;
            double elevation;
            if (inLayer.Length > 0) {
                limit = inLayer.Max(c => Math.Abs(azimuths[c])) + 40;
                elevation = inLayer.Average(c => elevations[c]);
            } else {
                limit = 0;
                elevation = nominal;
            }

            for (var c = 0; c < n; c++) {
                if (elevations[c] < -10 || elevations[c] > 10 || Math.Abs(azimuths[c]) < limit - 1e-5) {
                    continue;
                }

                positions.Add(Vector3.FromPolar(azimuths[c], elevation));
                sources.Add(c);
            }
        }

        var downmix = new double[n, positions.Count];
        for (var e = 0; e < positions.Count; e++) {
            downmix[sources[e], e] = 1;
        }

        return (positions, downmix);
    }

    /// <summary>
    ///     The regions of the convex hull of <paramref name="speakers" /> and the poles (BS.2127's <c>_configure_full</c>;
    ///     a layout with a speaker straight up would drop the upper pole, which none of Anode's has).
    /// </summary>
    static Region[] Triangulate(List<Vector3> speakers) {
        var all = new List<Vector3>(speakers) { new(0, 0, -1), new(0, 0, 1) };
        var virtuals = new[] { speakers.Count, speakers.Count + 1 };
        var facets = HullFacets(all);
        var regions = new List<Region>();
        foreach (var pole in virtuals) {
            var ring = facets.Where(f => f.Contains(pole))
                .SelectMany(static f => f)
                .Where(v => v != pole)
                .Distinct()
                .Order()
                .ToArray();
            regions.Add(new VirtualNgon(ring, [..ring.Select(v => all[v])], all[pole]));
        }

        foreach (var facet in facets) {
            if (facet.Overlaps(virtuals)) {
                continue;
            }

            var vertices = facet.Order().ToArray();
            regions.Add(
                vertices.Length switch {
                    3 => new Triplet(vertices, [..vertices.Select(v => all[v])]),
                    4 => new QuadRegion(vertices, [..vertices.Select(v => all[v])]),
                    _ => throw new InvalidOperationException("A hull facet with more than four speakers.")
                }
            );
        }

        return [..regions];
    }

    /// <summary>
    ///     The facets of the convex hull of <paramref name="points" /> (all on or near the unit sphere, around the
    ///     origin), coplanar triangles merged: every plane through three points with all points on one side. A few dozen
    ///     points, built once, so brute force is plenty.
    /// </summary>
    static List<HashSet<int>> HullFacets(List<Vector3> points) {
        const double side = 1e-9;
        const double same = 1e-5;
        var planes = new List<(Vector3 Normal, double Offset, HashSet<int> Vertices)>();
        var n = points.Count;
        for (var i = 0; i < n; i++) {
            for (var j = i + 1; j < n; j++) {
                for (var k = j + 1; k < n; k++) {
                    var normal = (points[j] - points[i]).Cross(points[k] - points[i]);
                    var length = normal.Length;
                    if (length < 1e-9) {
                        continue;
                    }

                    normal *= 1 / length;
                    var offset = normal.Dot(points[i]);
                    var above = false;
                    var below = false;
                    for (var m = 0; m < n && !(above && below); m++) {
                        var s = normal.Dot(points[m]) - offset;
                        above |= s > side;
                        below |= s < -side;
                    }

                    if (above && below) {
                        continue;
                    }

                    if (above) {
                        normal *= -1;
                        offset = -offset;
                    }

                    var existing = planes.FindIndex(p =>
                        (p.Normal - normal).Length < same && Math.Abs(p.Offset - offset) < same
                    );
                    if (existing < 0) {
                        planes.Add((normal, offset, [i, j, k]));
                    } else {
                        planes[existing].Vertices.UnionWith([i, j, k]);
                    }
                }
            }
        }

        return [..planes.Select(static p => p.Vertices)];
    }

    /// <summary>BS.2127's <c>ngon_vertex_order</c>: the order of a convex, roughly planar polygon's vertices.</summary>
    internal static int[] VertexOrder(Vector3[] vertices) {
        var centre = vertices.Aggregate(new Vector3(0, 0, 0), static (a, v) => a + v) * (1.0 / vertices.Length);
        var a = vertices[0] - centre;
        var b = vertices.Skip(1).Select(v => v - centre).MinBy(v => Math.Abs(v.Dot(a)));
        return [
            ..Enumerable.Range(0, vertices.Length)
                .OrderBy(i => Math.Atan2((vertices[i] - centre).Dot(a), (vertices[i] - centre).Dot(b)))
        ];
    }

    abstract class Region {
        /// <summary>Adds this region's gains at the extended speakers' indices; false if the direction is outside it.</summary>
        public abstract bool Handle(Vector3 direction, Span<double> gains, Span<double> scratch);
    }

    /// <summary>VBAP over three speakers: gains g with g·P along the direction, all non-negative, unit norm.</summary>
    sealed class Triplet : Region {
        readonly int[] channels;
        readonly double[] basis = new double[9];

        public Triplet(int[] channels, Vector3[] positions) {
            this.channels = channels;
            // The inverse of the matrix whose rows are the positions.
            var (a, b, c) = (positions[0], positions[1], positions[2]);
            var det = a.Dot(b.Cross(c));
            var columns = new[] { b.Cross(c), c.Cross(a), a.Cross(b) };
            for (var j = 0; j < 3; j++) {
                basis[0 * 3 + j] = columns[j].X / det;
                basis[1 * 3 + j] = columns[j].Y / det;
                basis[2 * 3 + j] = columns[j].Z / det;
            }
        }

        public bool Solve(Vector3 p, Span<double> g) {
            for (var j = 0; j < 3; j++) {
                g[j] = p.X * basis[j] + p.Y * basis[3 + j] + p.Z * basis[6 + j];
            }

            const double epsilon = -1e-11;
            if (g[0] < epsilon || g[1] < epsilon || g[2] < epsilon) {
                return false;
            }

            var norm = Math.Sqrt(g[0] * g[0] + g[1] * g[1] + g[2] * g[2]);
            for (var j = 0; j < 3; j++) {
                g[j] = Math.Clamp(g[j] / norm, 0, 1);
            }

            return true;
        }

        public override bool Handle(Vector3 direction, Span<double> gains, Span<double> scratch) {
            if (!Solve(direction, scratch)) {
                return false;
            }

            for (var j = 0; j < 3; j++) {
                gains[channels[j]] = scratch[j];
            }

            return true;
        }
    }

    /// <summary>
    ///     A ring of real speakers around a virtual one (a pole): triplets of neighbours on the ring and the virtual
    ///     speaker, whose gain is shared equally (1/√n) by the ring, then normalised.
    /// </summary>
    sealed class VirtualNgon : Region {
        readonly int[] channels;
        readonly Triplet[] triplets;
        readonly int[][] corners;

        public VirtualNgon(int[] channels, Vector3[] positions, Vector3 centre) {
            this.channels = channels;
            var order = VertexOrder(positions);
            var n = channels.Length;
            triplets = new Triplet[n];
            corners = new int[n][];
            for (var i = 0; i < n; i++) {
                var (p, q) = (order[i], order[(i + 1) % n]);
                triplets[i] = new([0, 1, 2], [positions[p], positions[q], centre]);
                corners[i] = [p, q];
            }
        }

        public override bool Handle(Vector3 direction, Span<double> gains, Span<double> scratch) {
            for (var i = 0; i < triplets.Length; i++) {
                if (!triplets[i].Solve(direction, scratch)) {
                    continue;
                }

                var share = scratch[2] / Math.Sqrt(channels.Length);
                var (p, q) = (corners[i][0], corners[i][1]);
                var sum = 0.0;
                for (var c = 0; c < channels.Length; c++) {
                    var g = share + (c == p ? scratch[0] : 0) + (c == q ? scratch[1] : 0);
                    sum += g * g;
                }

                var scale = 1 / Math.Sqrt(sum);
                for (var c = 0; c < channels.Length; c++) {
                    gains[channels[c]] = (share + (c == p ? scratch[0] : 0) + (c == q ? scratch[1] : 0)) * scale;
                }

                return true;
            }

            return false;
        }
    }

    /// <summary>
    ///     BS.2127's quad region: four speakers on a plane, the direction's position along two axes of the quad found
    ///     from a quadratic each, the gains bilinear in them, then normalised.
    /// </summary>
    sealed class QuadRegion : Region {
        readonly int[] channels;
        readonly int[] order;
        readonly Vector3[] positions;
        readonly Vector3[] axisX;
        readonly Vector3[] axisY;

        public QuadRegion(int[] channels, Vector3[] positions) {
            this.channels = channels;
            this.positions = positions;
            order = VertexOrder(positions);
            var p = order.Select(i => positions[i]).ToArray();
            axisX = Axis(p[0], p[1], p[2], p[3]);
            axisY = Axis(p[1], p[2], p[3], p[0]);
        }

        static Vector3[] Axis(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
            [(b - a).Cross(c - d), a.Cross(c - d) + (b - a).Cross(d), a.Cross(d)];

        static bool Root(Vector3[] axis, Vector3 p, out double root) {
            const double epsilon = 1e-10;
            var qa = axis[0].Dot(p);
            var qb = axis[1].Dot(p);
            var qc = axis[2].Dot(p);
            Span<double> roots = stackalloc double[2];
            var count = 0;
            if (Math.Abs(qa) < 1e-14) {
                if (Math.Abs(qb) > 1e-14) {
                    roots[count++] = -qc / qb;
                }
            } else {
                var disc = qb * qb - 4 * qa * qc;
                if (disc < 0) {
                    // Complex roots: only when the imaginary part is negligible.
                    if (Math.Sqrt(-disc) / (2 * Math.Abs(qa)) < epsilon) {
                        roots[count++] = -qb / (2 * qa);
                    }
                } else {
                    var s = Math.Sqrt(disc);
                    roots[count++] = (-qb + s) / (2 * qa);
                    roots[count++] = (-qb - s) / (2 * qa);
                }
            }

            for (var i = 0; i < count; i++) {
                if (roots[i] > -epsilon && roots[i] < 1 + epsilon) {
                    root = Math.Clamp(roots[i], 0, 1);
                    return true;
                }
            }

            root = 0;
            return false;
        }

        public override bool Handle(Vector3 direction, Span<double> gains, Span<double> scratch) {
            if (!Root(axisX, direction, out var x) || !Root(axisY, direction, out var y)) {
                return false;
            }

            var g = scratch[..4];
            g[order[0]] = (1 - x) * (1 - y);
            g[order[1]] = x * (1 - y);
            g[order[2]] = x * y;
            g[order[3]] = (1 - x) * y;
            var towards = new Vector3(0, 0, 0);
            for (var i = 0; i < 4; i++) {
                towards += positions[i] * g[i];
            }

            if (towards.Dot(direction) <= 0) {
                return false;
            }

            Normalise(g);
            for (var i = 0; i < 4; i++) {
                gains[channels[i]] = g[i];
            }

            return true;
        }
    }
}
