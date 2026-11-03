using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): the project's surround format, a strip's input format and panner, the movie and
///     scene markers. Output formats are never set: <see cref="Conform" /> keeps every strip's output format equal to
///     its destination's (the master's is the project's format, a bus's is its aux's input format, a device output pair
///     is stereo) after every command that changes routing or a format, and keeps panner modes valid for the output (the
///     stereo pan for a mono or stereo output, the Surround Panner or an object for a surround one, an object only into
///     the master). Panner settings survive every change.
/// </summary>
public static partial class Edits {
    /// <summary>The most objects a project has (the ADM profile's limit beside a 7.1.4 bed).</summary>
    public const int MaxObjects = 118;

    /// <summary>The Phase 10 commands; null for every other command.</summary>
    static IReadOnlyList<DeltaOp>? Phase10Ops(ProjectState state, EditCommand command, IIdSource ids) =>
        command switch {
            SetSurroundFormatCommand c => SetSurroundFormat(state, c),
            SetStripFormatCommand c => SetStripFormat(state, c),
            SetPannerCommand c => SetPanner(state, c),
            SetMovieCommand c => [new MovieChanged(CheckMovie(c.Movie))],
            SetMoviePositionCommand c => SetMoviePosition(state, c),
            RemoveMovieCommand => state.Movie is null
                ? throw new EditException("The project has no movie.")
                : [new MovieChanged(null)],
            AddSceneMarkersCommand c => AddSceneMarkers(state, c, ids),
            _ => null
        };

    static DeltaOp[] SetSurroundFormat(ProjectState state, SetSurroundFormatCommand c) {
        CheckFormat(c.Format);
        return [new SettingsChanged(state.Settings with { SurroundFormat = c.Format })];
    }

    static DeltaOp[] SetStripFormat(ProjectState state, SetStripFormatCommand c) {
        var track = TrackOf(state, c.TrackId ?? string.Empty);
        CheckFormat(c.InputFormat);
        CheckInputFormat(track.Kind, c.InputFormat);
        return [new TrackUpserted(track with { Strip = track.Strip with { InputFormat = c.InputFormat } }, state.IndexOfTrack(track.Id))];
    }

    /// <summary>Whether a track of <paramref name="kind" /> takes <paramref name="format" /> as its input format.</summary>
    static void CheckInputFormat(TrackKind kind, ChannelFormat format) {
        switch (kind) {
            case TrackKind.Instrument when format is not (ChannelFormat.Mono or ChannelFormat.Stereo):
                throw new EditException("An instrument track's input is mono or stereo.");
            case TrackKind.Instrument or TrackKind.Audio or TrackKind.Aux:
                return;
            default:
                throw new EditException($"A {Kind(kind)} track has no input format.");
        }
    }

    static void CheckFormat(ChannelFormat format) {
        if (!Enum.IsDefined(format)) {
            throw new EditException($"Unknown channel format {format}.");
        }
    }

    static DeltaOp[] SetPanner(ProjectState state, SetPannerCommand c) {
        var track = MixTrackOf(state, c.TrackId, "panner");
        var old = track.Strip.Panner;
        var surround = old.Surround with {
            Angle = c.Angle ?? old.Surround.Angle,
            Diversity = c.Diversity ?? old.Surround.Diversity,
            Elevation = c.SurroundElevation ?? old.Surround.Elevation,
            CentreLevelDb = c.CentreLevelDb ?? old.Surround.CentreLevelDb,
            LfeLevelDb = c.LfeLevelDb ?? old.Surround.LfeLevelDb,
            Spread = c.Spread ?? old.Surround.Spread
        };
        var position = old.Object with {
            Azimuth = c.Azimuth ?? old.Object.Azimuth,
            Elevation = c.ObjectElevation ?? old.Object.Elevation,
            Distance = c.Distance ?? old.Object.Distance,
            Size = c.Size ?? old.Object.Size
        };
        var panner = CheckPanner(new(c.Mode ?? old.Mode, surround, position));
        if (c.Mode is { } mode && mode != old.Mode) {
            CheckMode(state, track, mode);
        }

        return [new TrackUpserted(track with { Strip = track.Strip with { Panner = panner } }, state.IndexOfTrack(track.Id))];
    }

    /// <summary>Whether <paramref name="track" />'s strip may pan in <paramref name="mode" />.</summary>
    static void CheckMode(ProjectState state, Track track, PannerMode mode) {
        var surround = SpeakerLayouts.IsSurround(track.Strip.OutputFormat);
        switch (mode) {
            case PannerMode.Stereo when surround:
                throw new EditException("A strip with a surround output pans with the Surround Panner or as an object.");
            case PannerMode.Surround when !surround:
                throw new EditException("The Surround Panner needs a surround output.");
            case PannerMode.Object when track.Strip.Output != Routing.Master
                || !SpeakerLayouts.IsSurround(state.Settings.SurroundFormat):
                throw new EditException("An object needs the master of a surround project as its output.");
            case PannerMode.Object when Objects(state) >= MaxObjects:
                throw new EditException($"A project has at most {MaxObjects} objects.");
        }
    }

    /// <summary>The tracks in object mode.</summary>
    static int Objects(ProjectState state) => state.Tracks.Count(static t => t.Strip.Panner.Mode == PannerMode.Object);

    /// <summary>A panner whose settings are in their ranges (<see cref="SurroundPanner" />, <see cref="ObjectPanner" />).</summary>
    static Panner CheckPanner(Panner? panner) {
        if (panner is not { Surround: { } s, Object: { } o } || !Enum.IsDefined(panner.Mode)) {
            throw new EditException("A panner has a mode and both panners' settings.");
        }

        CheckPannerValue(s.Angle, -180, 180, "Surround Panner angle");
        CheckPannerValue(s.Diversity, 0, 1, "diversity");
        CheckPannerValue(s.Elevation, 0, 90, "Surround Panner elevation");
        CheckPannerValue(s.CentreLevelDb, Units.SilenceDb, 0, "centre level");
        CheckPannerValue(s.LfeLevelDb, Units.SilenceDb, 0, "LFE level");
        CheckPannerValue(s.Spread, 0, 180, "spread");
        CheckPannerValue(o.Azimuth, -180, 180, "object azimuth");
        CheckPannerValue(o.Elevation, -90, 90, "object elevation");
        CheckPannerValue(o.Distance, 0, 1, "object distance");
        CheckPannerValue(o.Size, 0, 1, "object size");
        return panner;
    }

    static void CheckPannerValue(double value, double min, double max, string what) {
        if (!double.IsFinite(value) || value < min || value > max) {
            throw new EditException($"The {what} is {min}…{max}, not {value}.");
        }
    }

    /// <summary>
    ///     The range of a panner automation target (<see cref="PannerTargets" />), or null when
    ///     <paramref name="target" /> is none.
    /// </summary>
    static LaneRange? PannerRange(string target) =>
        target switch {
            PannerTargets.SurroundAngle or PannerTargets.ObjectAzimuth => new(-180, 180, false),
            PannerTargets.SurroundDiversity
                or PannerTargets.ObjectDistance
                or PannerTargets.ObjectSize => new(0, 1, false),
            PannerTargets.SurroundElevation => new(0, 90, false),
            PannerTargets.SurroundCentre or PannerTargets.SurroundLfe => new(Units.SilenceDb, 0, false),
            PannerTargets.SurroundSpread => new(0, 180, false),
            PannerTargets.ObjectElevation => new(-90, 90, false),
            _ => null
        };

    /// <summary>A panner parameter's value (<see cref="PannerTargets" />), or null when <paramref name="target" /> is none.</summary>
    public static double? PannerValue(Panner panner, string target) =>
        target switch {
            PannerTargets.SurroundAngle => panner.Surround.Angle,
            PannerTargets.SurroundDiversity => panner.Surround.Diversity,
            PannerTargets.SurroundElevation => panner.Surround.Elevation,
            PannerTargets.SurroundCentre => panner.Surround.CentreLevelDb,
            PannerTargets.SurroundLfe => panner.Surround.LfeLevelDb,
            PannerTargets.SurroundSpread => panner.Surround.Spread,
            PannerTargets.ObjectAzimuth => panner.Object.Azimuth,
            PannerTargets.ObjectElevation => panner.Object.Elevation,
            PannerTargets.ObjectDistance => panner.Object.Distance,
            PannerTargets.ObjectSize => panner.Object.Size,
            _ => null
        };

    /// <summary>A panner with one parameter set (clamped to its range); the panner itself when the target is none.</summary>
    public static Panner WithPannerValue(Panner panner, string target, double value) {
        if (PannerRange(target) is not { } range) {
            return panner;
        }

        var v = Math.Clamp(value, range.Min, range.Max);
        return target switch {
            PannerTargets.SurroundAngle => panner with { Surround = panner.Surround with { Angle = v } },
            PannerTargets.SurroundDiversity => panner with { Surround = panner.Surround with { Diversity = v } },
            PannerTargets.SurroundElevation => panner with { Surround = panner.Surround with { Elevation = v } },
            PannerTargets.SurroundCentre => panner with { Surround = panner.Surround with { CentreLevelDb = v } },
            PannerTargets.SurroundLfe => panner with { Surround = panner.Surround with { LfeLevelDb = v } },
            PannerTargets.SurroundSpread => panner with { Surround = panner.Surround with { Spread = v } },
            PannerTargets.ObjectAzimuth => panner with { Object = panner.Object with { Azimuth = v } },
            PannerTargets.ObjectElevation => panner with { Object = panner.Object with { Elevation = v } },
            PannerTargets.ObjectDistance => panner with { Object = panner.Object with { Distance = v } },
            _ => panner with { Object = panner.Object with { Size = v } }
        };
    }

    /// <summary>Whether an operation may have changed what a strip's output format or panner mode follows.</summary>
    static bool MayReformat(DeltaOp op) => op is TrackUpserted or TrackRemoved or MasterChanged or SettingsChanged;

    /// <summary>
    ///     The operations that make <paramref name="state" />'s strips agree with their destinations (none when they do):
    ///     the master in and out in the project's format; each track's output format its destination's (the master's
    ///     format, its bus's aux's input format, stereo for a bus without an aux and a device output pair); its panner
    ///     mode the stereo pan for a mono or stereo output, else the Surround Panner unless it is an object into the
    ///     master. Panner settings stay.
    /// </summary>
    public static List<DeltaOp> Conform(ProjectState state) {
        var ops = new List<DeltaOp>();
        var format = state.Settings.SurroundFormat;
        if (state.Master.InputFormat != format || state.Master.OutputFormat != format) {
            ops.Add(new MasterChanged(state.Master with { InputFormat = format, OutputFormat = format }));
        }

        Dictionary<int, ChannelFormat>? buses = null;
        for (var i = 0; i < state.Tracks.Count; i++) {
            var track = state.Tracks[i];
            var strip = track.Strip;
            var output = strip.Output == Routing.Master ? format
                : Routing.BusOf(strip.Output) is { } bus ? BusFormat(bus)
                : ChannelFormat.Stereo;
            var mode = !SpeakerLayouts.IsSurround(output) ? PannerMode.Stereo
                : strip.Panner.Mode == PannerMode.Object && strip.Output == Routing.Master ? PannerMode.Object
                : PannerMode.Surround;
            if (output != strip.OutputFormat || mode != strip.Panner.Mode) {
                ops.Add(
                    new TrackUpserted(
                        track with { Strip = strip with { OutputFormat = output, Panner = strip.Panner with { Mode = mode } } },
                        i
                    )
                );
            }
        }

        return ops;

        ChannelFormat BusFormat(int bus) {
            buses ??= state.Tracks.Where(static t => t.Kind == TrackKind.Aux && t.InputBus > 0)
                .GroupBy(static t => t.InputBus)
                .ToDictionary(static g => g.Key, static g => g.First().Strip.InputFormat);
            return buses.GetValueOrDefault(bus, ChannelFormat.Stereo);
        }
    }

    /// <summary><paramref name="state" /> with its strips agreeing with their destinations (<see cref="Conform" />).</summary>
    public static ProjectState Conformed(ProjectState state) => state.Apply(Conform(state));
}
