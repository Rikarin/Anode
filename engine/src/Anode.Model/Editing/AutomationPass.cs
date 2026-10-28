using Anode.Contracts;
using System.Collections.Immutable;
using System.Globalization;

namespace Anode.Model.Editing;

/// <summary>
///     One playback pass's automation recording (docs/05-phase-4.md, "Automation"). While the transport plays, a
///     control move (<c>setStrip</c>, <c>setSend</c>, <c>setInsertParameter</c>, <c>setParameter</c>, and the group
///     members that follow) on a track in touch, latch or write mode is written into that control's lane at the
///     playhead: the move replaces the lane's points since the control's previous move in this pass. In touch the lane
///     takes over again after the last move (there is no release message: the control stops moving); at the end of
///     the pass latch and write hold the last value up to where playback stopped, and write also flattens the track's
///     other lanes over the whole pass to their controls' values. The server coalesces a pass into one undo step
///     (<see cref="History.EditHistory.PassPrefix" />).
/// </summary>
public sealed class AutomationPass(long number, long startTick) {
    readonly Dictionary<(string Track, string Target), (long Tick, double Value)> touched = [];

    public long Number { get; } = number;

    /// <summary>Where the pass started playing.</summary>
    public long StartTick { get; } = startTick;

    /// <summary>The undo coalescing key of this pass.</summary>
    public string CoalesceKey => History.EditHistory.PassPrefix + Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a batch is made of control moves only: the edits that record.</summary>
    public static bool Records(IReadOnlyList<EditCommand> commands) =>
        commands.Count > 0
        && commands.All(static c => c is SetStripCommand
            or SetSendCommand { Bus: null }
            or SetInsertParameterCommand
            or SetParameterCommand
        );

    /// <summary>Whether a track in <paramref name="mode" /> records control moves.</summary>
    public static bool Recording(AutomationMode mode) =>
        mode is AutomationMode.Touch or AutomationMode.Latch or AutomationMode.Write;

    /// <summary>
    ///     The lane operations that record what a batch of control moves changed between <paramref name="before" /> and
    ///     <paramref name="after" />, at <paramref name="tick" />; empty when no recording track changed.
    /// </summary>
    public List<DeltaOp> Record(ProjectState before, ProjectState after, long tick, IIdSource ids) {
        var ops = new List<DeltaOp>();
        foreach (var track in after.Tracks) {
            if (!Recording(track.AutomationMode) || before.FindTrack(track.Id) is not { } old) {
                continue;
            }

            var was = Controls(old);
            foreach (var (target, value) in Controls(track)) {
                if (was.TryGetValue(target, out var previous) && previous.Equals(value)) {
                    continue;
                }

                var key = (track.Id, target);
                var from = touched.TryGetValue(key, out var last) && last.Tick <= tick ? last.Tick : tick - 1;
                touched[key] = (tick, value);
                ops.Add(Write(after, track.Id, target, from, tick, [new(tick, value, 0)], ids));
            }
        }

        return ops;
    }

    /// <summary>
    ///     The lane operations that end the pass at <paramref name="stopTick" />: latch and write hold each touched
    ///     control's last value to there, and write flattens the track's untouched lanes over the pass.
    /// </summary>
    public List<DeltaOp> Finish(ProjectState state, long stopTick, IIdSource ids) {
        var ops = new List<DeltaOp>();
        foreach (var ((trackId, target), (tick, value)) in touched) {
            if (state.FindTrack(trackId) is { AutomationMode: AutomationMode.Latch or AutomationMode.Write }
                && stopTick > tick
                && Edits.TargetExists(state, trackId, target)) {
                var op = Write(state, trackId, target, tick, stopTick, [new(stopTick, value, 0)], ids);
                ops.Add(op);
                state = state.Apply(op);
            }
        }

        foreach (var track in state.Tracks.Where(static t => t.AutomationMode == AutomationMode.Write)) {
            var controls = Controls(track);
            foreach (var lane in state.LanesOf(track.Id).ToArray()) {
                if (touched.ContainsKey((track.Id, lane.Target))
                    || !controls.TryGetValue(lane.Target, out var value)
                    || stopTick <= StartTick) {
                    continue;
                }

                var op = Write(
                    state,
                    track.Id,
                    lane.Target,
                    StartTick - 1,
                    stopTick,
                    [new(StartTick, value, 0), new(stopTick, value, 0)],
                    ids
                );
                ops.Add(op);
                state = state.Apply(op);
            }
        }

        touched.Clear();
        return ops;
    }

    /// <summary>The lane of a target with its points in (<paramref name="from" />, <paramref name="to" />] replaced.</summary>
    static AutomationLaneUpserted Write(
        ProjectState state,
        string trackId,
        string target,
        long from,
        long to,
        IReadOnlyList<AutomationPoint> points,
        IIdSource ids
    ) {
        var lane = state.LaneOf(trackId, target);
        var kept = (lane?.Points ?? []).Where(p => p.Tick <= from || p.Tick > to);
        var merged = kept.Concat(points).OrderBy(static p => p.Tick).ToArray();
        return new(new(lane?.Id ?? ids.Next(IdPrefix.Automation), trackId, target, merged));
    }

    /// <summary>A track's automatable controls and their values, by lane target.</summary>
    public static ImmutableSortedDictionary<string, double> Controls(Track track) {
        var controls = ImmutableSortedDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        var strip = track.Strip;
        controls["volume"] = strip.VolumeDb;
        controls["mute"] = strip.Mute ? 1 : 0;
        if (track.Kind != TrackKind.Vca) {
            controls["pan"] = strip.Pan;
        }

        foreach (var send in strip.Sends) {
            controls[$"send:{send.Id}"] = send.LevelDb;
        }

        foreach (var insert in strip.Inserts) {
            foreach (var (parameter, value) in insert.Parameters) {
                if (parameter == Edits.HostTempoParameter) {
                    continue;
                }

                controls[$"insert:{insert.Id}:{parameter}"] = value;
            }
        }

        if (track.Instrument is { } slot) {
            foreach (var (parameter, value) in slot.Parameters) {
                controls[$"instrument:{parameter}"] = value;
            }
        }

        return controls.ToImmutable();
    }
}
