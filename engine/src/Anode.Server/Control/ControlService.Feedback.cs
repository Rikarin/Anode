using Anode.Contracts;
using Anode.Control.Surfaces;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Server.Projects;
using System.Diagnostics;
using System.Globalization;

namespace Anode.Server.Control;

/// <summary>
///     Feedback (docs/09-phase-8.md, "Surfaces"): every <see cref="TickInterval" /> the service builds a
///     <see cref="ProjectView" /> of the open project, the transport, the meters and the selection, and gives it to each
///     surface's protocol, which answers only what changed since it last encoded (faders and LEDs at most 30 times a
///     second, however the ticks fall); about every third tick the meters and the protocols' own polls (Mackie
///     Control's handshake, HUI's ping) go out too (at most 10 a second). Connection and bank changes go out as
///     <see cref="SurfacesEvent" />s.
/// </summary>
public sealed partial class ControlService {
    /// <summary>Feedback (faders, LEDs, OSC values) goes out at most this often: 30 a second.</summary>
    public static readonly TimeSpan MinFeedbackInterval = TimeSpan.FromSeconds(1.0 / 30);

    /// <summary>Meters and the protocols' polls go out at most this often: 10 a second.</summary>
    public static readonly TimeSpan MinMeterInterval = TimeSpan.FromSeconds(1.0 / 10);

    long lastFeedback;
    long lastMeters;

    void Tick() {
        var view = session.View;
        if ((view?.Session ?? -1) != seenSession) {
            // Another project (or none): pickup and partial messages start over.
            seenSession = view?.Session ?? -1;
            ResetEngines();
        }

        EndGestures();
        RepeatHeld();
        // A late tick is followed by an early one: the rates hold between the sends themselves, not the ticks.
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastFeedback, now) < MinFeedbackInterval) {
            return;
        }

        lastFeedback = now;
        var slow = Stopwatch.GetElapsedTime(lastMeters, now) >= MinMeterInterval;
        if (slow) {
            lastMeters = now;
        }

        if (surfaces.Count > 0 || OscWanted) {
            var projectView = View(view);
            foreach (var group in groups) {
                SendAll(group, group.Encode(projectView, logger));
                if (slow) {
                    SendAll(group, group.EncodeMeters(projectView, logger));
                    SendAll(group, group.Poll(NowNs, logger));
                }
            }

            OscFeedback(projectView);
        }

        if (slow) {
            PublishStatuses();
        }
    }

    /// <summary>The clock surfaces poll on: nanoseconds, as the host stamps control events (<c>cth_midi_now_ns</c>).</summary>
    static long NowNs => (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));

    /// <summary>The tracks surfaces and OSC show, in order: those with an audio strip (audio, instrument, aux).</summary>
    static IEnumerable<Track> Shown(ProjectState state) => state.Tracks.Where(static t => Edits.HasAudioStrip(t.Kind));

    /// <summary>The project as surfaces and OSC see it (empty without a project).</summary>
    ProjectView View(OpenProjectView? open) {
        var transport = engine.Transport();
        if (open is null) {
            return new([], transport.State, false, ProjectDefaults.Tempo, default, new(1, 1, 1, 0), [], []);
        }

        var state = open.State;
        var selected = Selected(open);
        var levels = Volatile.Read(ref meters);
        var tracks = new List<TrackView>();
        foreach (var track in Shown(state)) {
            var strip = track.Strip;
            var isSelected = track.Id == selected;
            tracks.Add(
                new(
                    track.Id,
                    track.Name,
                    Taper.Position(strip.VolumeDb),
                    strip.VolumeDb,
                    strip.Pan,
                    strip.Mute,
                    strip.Solo,
                    track.RecordArmed,
                    isSelected,
                    track.AutomationMode,
                    Taper.Meter(levels.GetValueOrDefault(track.Id)),
                    [
                        ..strip.Sends.Select(static s => new ParameterView(
                                "send:" + s.Id,
                                "Bus " + s.Bus.ToString(CultureInfo.InvariantCulture),
                                Taper.Position(s.LevelDb),
                                Taper.FormatDb(s.LevelDb)
                            )
                        )
                    ],
                    isSelected ? Inserts(state, track) : []
                )
            );
        }

        var tick = transport.PositionTick;
        var smart = selected is not null && state.FindTrack(selected)?.SmartControls is { } controls
            ? controls.Controls.Select(static c => new ParameterView(
                    c.Id,
                    c.Name,
                    c.Value,
                    Math.Round(c.Value * 100).ToString(CultureInfo.InvariantCulture) + " %"
                )
            )
            : [];
        return new(
            tracks,
            transport.State,
            state.Settings.Cycle.Enabled,
            state.PlaybackMap.BpmAt(tick),
            Timecode(
                transport.PositionSamples / (double)Math.Max(1, state.SampleRate)
                + state.Settings.Sync.StartOffsetSeconds,
                state.Settings.Sync.FrameRate
            ),
            Beats(state, tick),
            [
                ..state.Markers.Values.OrderBy(static m => m.Tick)
                    .ThenBy(static m => m.Id, StringComparer.Ordinal)
                    .Select(static m => new MarkerView(m.Id, m.Name, m.Tick))
            ],
            [..smart]
        );
    }

    /// <summary>The selected track's inserts with their parameters (normalised), for the plug-in VPot mode.</summary>
    InsertView[] Inserts(ProjectState state, Track track) {
        var values = new TargetValues(this, session.View);
        return [
            ..track.Strip.Inserts.Select(insert => new InsertView(
                    insert.Id,
                    insert.Name,
                    [
                        ..(insert.RuntimeParameters ?? catalog.ForEdits.Find(insert.PluginId)?.Parameters ?? [])
                            .Select(p => {
                                    var address = $"insert:{insert.Id}:{p.Id}";
                                    var plain = insert.Parameters.TryGetValue(p.Id, out var v) ? v : p.Default;
                                    return new ParameterView(
                                        address,
                                        p.Name,
                                        values.Value(track, address) ?? 0,
                                        Display(plain, p)
                                    );
                                }
                            )
                    ]
                )
            )
        ];

        static string Display(double value, ParameterInfo p) {
            if (p.ValueNames is { Count: > 0 } names && p.Steps > 0) {
                var index = (int)Math.Round(value - p.Min);
                return names[Math.Clamp(index, 0, names.Count - 1)];
            }

            var text = value.ToString(Math.Abs(value) >= 100 ? "0" : "0.0#", CultureInfo.InvariantCulture);
            return p.Unit is { Length: > 0 } unit ? $"{text} {unit}" : text;
        }
    }

    /// <summary>The playhead as bar, beat, sixteenth (1-based) and ticks (of 240).</summary>
    static BeatPosition Beats(ProjectState state, long tick) {
        var position = state.SignatureMap.Position(Math.Max(0, tick));
        const int division = Model.Time.TempoMap.Ppq / 4;
        return new(
            (int)Math.Min(int.MaxValue, position.Bar + 1),
            position.Beat + 1,
            (int)(position.Tick / division) + 1,
            (int)(position.Tick % division)
        );
    }

    /// <summary>A time in seconds as timecode at <paramref name="rate" /> (29.97 counted as drop frame), within a day.</summary>
    public static Timecode Timecode(double seconds, MtcFrameRate rate) {
        seconds = Math.Max(0, seconds);
        if (rate == MtcFrameRate.Fps2997Drop) {
            var frame = (long)Math.Floor(seconds * 30_000 / 1001 + 1e-9);
            var tens = frame / 17_982;
            var rest = frame % 17_982;
            frame += 18 * tens + (rest < 2 ? 0 : 2 * ((rest - 2) / 1798));
            return new(
                (int)(frame / 108_000 % 24),
                (int)(frame / 1800 % 60),
                (int)(frame / 30 % 60),
                (int)(frame % 30)
            );
        }

        var fps = rate switch {
            MtcFrameRate.Fps24 => 24,
            MtcFrameRate.Fps25 => 25,
            _ => 30
        };
        var frames = (long)Math.Floor(seconds * fps + 1e-9);
        return new(
            (int)(frames / (3600L * fps) % 24),
            (int)(frames / (60L * fps) % 60),
            (int)(frames / fps % 60),
            (int)(frames % fps)
        );
    }
}
