using Anode.Contracts;
using Anode.Model;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Interchange.Adm;

/// <summary>
///     The project ↔ ADM scene mapping (docs/11-phase-10.md, owned by workstream B): what the <c>admExport</c> and
///     <c>admImport</c> jobs give <see cref="AdmWriter" /> and take from <see cref="AdmReader" />. Positions are the
///     object panner's (polar, degrees, distance and size 0…1). Ticks and frames meet through the playback tempo map
///     (the bounce's), frame 0 being the range's start on export and the project start on import.
/// </summary>
public static class AdmMapping {
    /// <summary>The longest block where the position moves: 20 ms (the EBU ADM production profile).</summary>
    public const double MaxMovingBlockSeconds = 0.02;

    static readonly string[] PositionTargets = [
        PannerTargets.ObjectAzimuth,
        PannerTargets.ObjectElevation,
        PannerTargets.ObjectDistance,
        PannerTargets.ObjectSize
    ];

    /// <summary>
    ///     The scene of <paramref name="request" /> for <paramref name="state" />: the bed (the master's bed bounce,
    ///     <paramref name="bedPath" />, in the project's format) and an object per object-mode track asked for (its
    ///     bounce from <paramref name="objectPaths" /> by track id), with blocks from its <c>object:…</c> automation over
    ///     the range (a block per automation change, interpolated, or per 20 ms where it ramps).
    /// </summary>
    public static AdmScene ToScene(
        ProjectState state,
        AdmExportRequest request,
        string bedPath,
        IReadOnlyDictionary<string, string> objectPaths
    ) {
        var (start, end) = Range(state, request.StartTick, request.EndTick);
        var objects = new List<AdmObject>();
        foreach (var track in ObjectTracks(state, request.TrackIds)) {
            if (objectPaths.TryGetValue(track.Id, out var path)) {
                objects.Add(new(track.Name, path, ObjectChannels(track), Blocks(state, track, start, end)));
            }
        }

        return new(
            state.Name,
            state.SampleRate,
            end - start,
            [new("Bed", state.Settings.SurroundFormat, bedPath)],
            objects
        );
    }

    /// <summary>The object-mode tracks <paramref name="trackIds" /> names (null: every one), in track order.</summary>
    public static IReadOnlyList<Track> ObjectTracks(ProjectState state, IReadOnlyList<string>? trackIds) =>
        [
            ..state.Tracks.Where(t => t.Strip.Panner.Mode == PannerMode.Object
                && (trackIds is null || trackIds.Contains(t.Id, StringComparer.Ordinal))
            )
        ];

    /// <summary>An object's channels: one for a mono input, else two (a stereo object).</summary>
    public static int ObjectChannels(Track track) => track.Strip.InputFormat == ChannelFormat.Mono ? 1 : 2;

    /// <summary>
    ///     The samples a range covers (as a bounce's): the ticks given, else the cycle when it is on, else the project;
    ///     <see cref="ArgumentException" /> when it is empty.
    /// </summary>
    public static (long Start, long End) Range(ProjectState state, long? startTick, long? endTick) {
        var settings = state.Settings;
        var (from, to) = startTick is null && endTick is null && settings.Cycle.Enabled
            ? (settings.Cycle.StartTick, settings.Cycle.EndTick)
            : (startTick ?? 0, endTick ?? settings.EndTick);
        if (from < 0 || to > Edits.MaxTick || to <= from) {
            throw new ArgumentException("An export ends after it starts, inside the project.");
        }

        var map = state.PlaybackMap;
        return (map.SampleAt(from, state.SampleRate), map.SampleAt(to, state.SampleRate));
    }

    /// <summary>
    ///     A track's position over samples [<paramref name="start" />, <paramref name="end" />) as blocks from 0: one per
    ///     stretch where it stands still (jumping there), and interpolated blocks of at most 20 ms where it moves.
    /// </summary>
    public static IReadOnlyList<AdmBlock> Blocks(ProjectState state, Track track, long start, long end) {
        var lanes = PositionTargets.Select(t => state.LaneOf(track.Id, t)?.Points).ToArray();
        var constant = track.Strip.Panner.Object;
        var map = state.PlaybackMap;
        var rate = state.SampleRate;
        double[] At(long sample, bool right) {
            var tick = map.TickAt((double)sample / rate);
            return [
                Value(lanes[0], tick, right, constant.Azimuth),
                Value(lanes[1], tick, right, constant.Elevation),
                Value(lanes[2], tick, right, constant.Distance),
                Value(lanes[3], tick, right, constant.Size)
            ];
        }

        // Where the lanes have points inside the range: the stretches between are each still or moving.
        var cuts = new SortedSet<long> { start, end };
        foreach (var points in lanes) {
            foreach (var point in points ?? []) {
                var sample = map.SampleAt(point.Tick, rate);
                if (sample > start && sample < end) {
                    cuts.Add(sample);
                }
            }
        }

        var blocks = new List<AdmBlock>();
        var longest = Math.Max(1, (long)Math.Round(MaxMovingBlockSeconds * rate));
        double[]? last = null;
        var edges = cuts.ToArray();
        for (var i = 0; i + 1 < edges.Length; i++) {
            var (a, b) = (edges[i], edges[i + 1]);
            var from = At(a, true);
            var to = At(b, false);
            var middle = At(a + (b - a) / 2, true);
            if (Same(from, to) && Same(from, middle)) {
                Add(a, b - a, from, false);
                continue;
            }

            var at = a;
            if (last is null || !Same(last, from)) {
                // A jump into a ramp: one frame there, then the ramp from it.
                Add(a, 1, from, false);
                at++;
            }

            var pieces = (long)Math.Ceiling((double)(b - at) / longest);
            for (var k = 0; k < pieces; k++) {
                var s = at + (b - at) * k / pieces;
                var e = at + (b - at) * (k + 1) / pieces;
                if (e > s) {
                    Add(s, e - s, k == pieces - 1 ? to : At(e, false), true);
                }
            }
        }

        return blocks;

        void Add(long from, long length, double[] p, bool interpolate) {
            if (length <= 0) {
                return;
            }

            blocks.Add(new(from - start, length, p[0], p[1], p[2], p[3], interpolate));
            last = p;
        }

        static bool Same(double[] x, double[] y) => x.Zip(y).All(static p => Math.Abs(p.First - p.Second) < 1e-9);
    }

    /// <summary>A lane's value at <paramref name="tick" /> (just after it when <paramref name="right" />; the constant without a lane).</summary>
    static double Value(IReadOnlyList<AutomationPoint>? points, double tick, bool right, double constant) {
        if (points is not { Count: > 0 }) {
            return constant;
        }

        var i = -1;
        for (var k = 0; k < points.Count; k++) {
            if (points[k].Tick < tick || (right && points[k].Tick <= tick)) {
                i = k;
            } else {
                break;
            }
        }

        if (i < 0) {
            return points[0].Value;
        }

        if (i == points.Count - 1) {
            return points[i].Value;
        }

        var (a, b) = (points[i], points[i + 1]);
        if (b.Tick == a.Tick) {
            return b.Value;
        }

        var x = Math.Clamp((tick - a.Tick) / (b.Tick - a.Tick), 0, 1);
        return a.Value + (b.Value - a.Value) * AutomationEvents.Shaped(x, a.Curve);
    }

    /// <summary>
    ///     The <c>importTracks</c> content of <paramref name="scene" /> for <paramref name="state" />: an audio track per
    ///     bed (input format the bed's) and per object (object mode, its blocks as <c>object:…</c> automation), each with
    ///     a region of the file the job made of its audio (<paramref name="fileIds" />, by <c>AudioPath</c>).
    /// </summary>
    public static ImportedContent ToContent(
        AdmScene scene,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    ) {
        var tracks = new List<ImportedTrack>();
        var n = 0;
        foreach (var bed in scene.Beds) {
            n++;
            tracks.Add(
                new(
                    $"adm-bed-{n}",
                    Named(bed.Name, $"Bed {n}"),
                    TrackKind.Audio,
                    null,
                    [],
                    [Region(state, fileIds, bed.AudioPath, n)],
                    bed.Format
                )
            );
        }

        foreach (var item in scene.Objects) {
            n++;
            var first = item.Blocks.Count > 0 ? item.Blocks[0] : null;
            var panner = Panner.Default with {
                Mode = PannerMode.Object,
                Object = first is null
                    ? ObjectPanner.Default
                    : new(Clamp(first.Azimuth, -180, 180), Clamp(first.Elevation, -90, 90), Clamp(first.Distance, 0, 1), Clamp(first.Size, 0, 1))
            };
            tracks.Add(
                new(
                    $"adm-object-{n}",
                    Named(item.Name, $"Object {n}"),
                    TrackKind.Audio,
                    null,
                    [],
                    [Region(state, fileIds, item.AudioPath, n)],
                    item.Channels == 1 ? ChannelFormat.Mono : ChannelFormat.Stereo,
                    panner,
                    Lanes(state, scene.SampleRate, item.Blocks, n)
                )
            );
        }

        return new(tracks);
    }

    static string Named(string? name, string fallback) {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? fallback : trimmed.Length > Edits.MaxNameLength ? trimmed[..Edits.MaxNameLength] : trimmed;
    }

    static double Clamp(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;

    /// <summary>A region of the whole file at the project start.</summary>
    static AudioRegion Region(ProjectState state, IReadOnlyDictionary<string, string> fileIds, string path, int n) {
        var id = fileIds.TryGetValue(path, out var found)
            ? found
            : throw new ArgumentException($"No project file for '{path}'.");
        var file = state.Files.TryGetValue(id, out var f) ? f : throw new ArgumentException($"No file '{id}'.");
        return new(
            $"adm-region-{n}",
            "ignored",
            id,
            file.Name,
            0,
            0,
            file.Frames,
            0,
            0,
            0,
            0,
            0,
            false,
            FlexFollow.Off,
            [],
            0,
            0,
            []
        );
    }

    /// <summary>
    ///     An object's blocks as <c>object:…</c> lanes (only the parameters that change): a jump at a block's start where
    ///     it does not interpolate, a line to its end where it does.
    /// </summary>
    static List<AutomationLane>? Lanes(ProjectState state, int sampleRate, IReadOnlyList<AdmBlock> blocks, int n) {
        if (blocks.Count < 2) {
            return null;
        }

        var map = state.PlaybackMap;
        var lanes = new List<AutomationLane>();
        (string Target, Func<AdmBlock, double> Get, double Min, double Max)[] parameters = [
            (PannerTargets.ObjectAzimuth, static b => b.Azimuth, -180, 180),
            (PannerTargets.ObjectElevation, static b => b.Elevation, -90, 90),
            (PannerTargets.ObjectDistance, static b => b.Distance, 0, 1),
            (PannerTargets.ObjectSize, static b => b.Size, 0, 1)
        ];
        foreach (var (target, get, min, max) in parameters) {
            if (blocks.All(b => Math.Abs(get(b) - get(blocks[0])) < 1e-9)) {
                continue;
            }

            var points = new List<AutomationPoint>();
            double? previous = null;
            foreach (var block in blocks) {
                var value = Clamp(get(block), min, max);
                var startTick = Tick(block.StartFrame);
                var endTick = Tick(block.StartFrame + block.DurationFrames);
                if (block.Interpolate && previous is { } from) {
                    Point(startTick, from);
                    Point(endTick, value);
                } else {
                    if (previous is { } held && Math.Abs(held - value) > 1e-9) {
                        Point(startTick, held);
                    }

                    Point(startTick, value);
                }

                previous = value;
            }

            lanes.Add(new($"adm-lane-{n}-{lanes.Count}", "ignored", target, points));

            void Point(long tick, double value) {
                // At most two points on a tick (a jump); a repeat of the last one is dropped.
                if (points.Count > 0 && points[^1].Tick == tick && Math.Abs(points[^1].Value - value) < 1e-9) {
                    return;
                }

                if (points.Count > 1 && points[^1].Tick == tick && points[^2].Tick == tick) {
                    points[^1] = new(tick, value, 0);
                    return;
                }

                points.Add(new(tick, value, 0));
            }
        }

        return lanes.Count == 0 ? null : lanes;

        long Tick(long frame) => Math.Max(0, (long)Math.Round(map.TickAt((double)frame / sampleRate)));
    }
}
