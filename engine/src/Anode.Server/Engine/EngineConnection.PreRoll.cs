using Anode.Contracts;
using Anode.Ipc;
using Anode.Model;
using Anode.Model.Time;
using Anode.Server.Projects;

namespace Anode.Server.Engine;

// A recording's pre-roll: where it starts playing, and the punch range the host holds until the playhead.
public sealed partial class EngineConnection {
    /// <summary>1 while a pre-roll holds the host's punch range.</summary>
    int preRollPunch;

    /// <summary>
    ///     Where a recording from <paramref name="position" /> (negative: the playhead) starts playing: its pre-roll
    ///     before, from a stop. Outside autopunch the host holds the input back until the playhead, as a punch-in there
    ///     (<see cref="ProjectSession.PreRollTick" />); the pass's stop restores the range.
    /// </summary>
    long PreRolled(Connection link, ProjectState state, TempoMap map, int rate, long position) {
        var status = Transport();
        session.PreRollTick = null;
        if (state.Settings.Recording.PreRollLength == 0 || status.State != TransportState.Stopped) {
            return position;
        }

        var from = position >= 0 ? position : status.PositionSamples;
        var tick = map.TickAtSample(from, rate);
        var start = Model.Editing.Edits.PreRollStart(state, tick);
        if (start == tick) {
            return position;
        }

        session.PreRollTick = tick;
        if (!state.Settings.Recording.Autopunch) {
            Volatile.Write(ref preRollPunch, 1);
            link.Work.Writer.TryWrite(new SetPunchMessage(true, from, map.SampleAt(Model.Editing.Edits.MaxTick, rate)));
        }

        return map.SampleAt(start, rate);
    }

    /// <summary>Puts the host's punch range back to the project's after a pre-roll held it.</summary>
    static void RestorePunch(Connection link, OpenProjectView? view) {
        if (view is null) {
            link.Work.Writer.TryWrite(new SetPunchMessage(false, 0, 0));
            return;
        }

        var state = view.State;
        var recording = state.Settings.Recording;
        var map = state.PlaybackMap;
        link.Work.Writer.TryWrite(
            new SetPunchMessage(
                recording.Autopunch,
                map.SampleAt(recording.PunchInTick, state.SampleRate),
                map.SampleAt(recording.PunchOutTick, state.SampleRate)
            )
        );
    }
}
