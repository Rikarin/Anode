using Anode.Engine.LiveLoops;
using Anode.Engine.Midi;
using Anode.Engine.Timing;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine;

// The Live Loops launcher on the audio thread (docs/07-phase-6.md, workstream A): every block, first the launches and
// stops the control thread queued (one message at a time, so a scene is one decision), then each track's launcher
// works out the block (CellPlayer.Step: boundaries from the tempo map, sample-accurate, a block never spanning a cycle
// jump), then a track whose Live Loops activation is on plays its schedule instead of its regions: an audio cell from
// the memory it was rendered into at model load, a MIDI cell's events (into the track's MIDI FX and instrument). State
// changes go to the control thread through CellStates. Audio-thread code: no allocation, no locks.
public sealed unsafe partial class AudioEngine {
    ModelRoot? launcherModel;

    /// <summary>Launches and stops on their way to the audio thread (producer: the control thread).</summary>
    public LaunchRing Launches { get; } = new();

    /// <summary>The launcher's state changes (consumer: the control thread, which pushes them).</summary>
    public SpscChannel<CellStateEvent> CellStates { get; } = new(8192);

    /// <summary>A request closer than this to a boundary (1 ms, rounded up) is late and takes the next boundary.</summary>
    public int LateFrames => (SampleRate + 999) / 1000;

    /// <summary>The model's tempo map at the engine's rate (a model without tempo points: 120 bpm in 4/4).</summary>
    TempoMap TempoOf(ModelRoot model) => model.Tempo.SampleRate == SampleRate ? model.Tempo : new(SampleRate);

    /// <summary>The block's launcher work: queued requests, then every track's schedule.</summary>
    void Launchers(ModelRoot model, in TempoMap tempo, long position, long clock, int frames, bool rolling) {
        var tracks = model.Tracks;
        var cycle = transport.PlayOrder;
        if (!ReferenceEquals(model, launcherModel)) {
            launcherModel = model; // a recompile may have removed playing or queued cells
            foreach (var track in tracks) {
                track.Player.Forget(model.Cells, position, CellStates);
            }
        }

        while (Launches.TryDequeue(out var command)) {
            Apply(model, command, tempo, position, clock, rolling, cycle);
        }

        foreach (var track in tracks) {
            var player = track.Player;
            if (player.Busy || player.Segments > 0) {
                player.Step(rolling, position, clock, frames, model.Cells, tempo, cycle, CellStates);
            }
        }
    }

    /// <summary>One launch or stop; a launch of a cell the model does not have is reported stopped.</summary>
    void Apply(
        ModelRoot model,
        in LaunchCommand command,
        in TempoMap tempo,
        long position,
        long clock,
        bool rolling,
        Cycle cycle
    ) {
        if (command.Kind == LaunchKind.Stop) {
            if (model.IndexOf(command.Value) is var t and >= 0) {
                model.Tracks[t]
                    .Player.Stop(command.Quantize, rolling, position, clock, LateFrames, tempo, cycle, CellStates);
            }

            return;
        }

        var cell = model.Cells[command.Value];
        var i = cell is null ? -1 : model.IndexOf(cell.TrackId);
        if (cell is null || i < 0) {
            CellStates.TryEnqueue(new(command.Value, EngineCellState.Stopped, position));
            return;
        }

        model.Tracks[i]
            .Player.Launch(
                cell.Index,
                cell.Mode,
                command.Quantize,
                rolling,
                position,
                clock,
                LateFrames,
                tempo,
                cycle,
                CellStates
            );
    }

    /// <summary>The transport stopped: every cell stops.</summary>
    void StopLaunchers(ModelRoot model) {
        foreach (var track in model.Tracks) {
            track.Player.StopAll(transport.Position, CellStates);
        }
    }

    /// <summary>An audio track's playing cell for the block, from memory, in place of its stream.</summary>
    void CellAudio(TrackState track, CellTable cells, Span<float> left, Span<float> right) {
        left.Clear();
        right.Clear();
        var player = track.Player;
        for (var s = 0; s < player.Segments; s++) {
            var (from, index, start) = player.Segment(s);
            var to = s + 1 < player.Segments ? player.Segment(s + 1).From : left.Length;
            if (index >= 0 && cells[index] is { Audio: not null } cell) {
                CopyCell(cell, blockClock + from - start, track.Stereo, left[from..to], right[from..to]);
            }
        }
    }

    /// <summary>
    ///     An audio cell's audio from <paramref name="offset" /> into it (a looping cell wraps at its length; silence
    ///     outside what it holds) into the track's channels.
    /// </summary>
    static void CopyCell(CellContent cell, long offset, bool stereo, Span<float> left, Span<float> right) {
        var audio = cell.Audio!;
        var loop = cell.Mode == CellMode.Loop && cell.Length > 0;
        for (var f = 0; f < left.Length; f++) {
            var at = loop ? (offset + f) % cell.Length : offset + f;
            if (at < 0 || at >= cell.Frames) {
                continue;
            }

            if (cell.Channels == 1) {
                left[f] = right[f] = audio[at];
                continue;
            }

            var l = audio[2 * at];
            var r = audio[2 * at + 1];
            left[f] = stereo ? l : (l + r) * 0.5f;
            right[f] = r;
        }
    }

    /// <summary>
    ///     A MIDI track's playing cell's events for the block (looping cells wrap; the notes a cell holds end where it
    ///     stops, wraps or gives way to another).
    /// </summary>
    void CellMidi(MidiTrackState midi, TrackState track, CellTable cells, int frames) {
        var player = track.Player;
        for (var s = 0; s < player.Segments; s++) {
            var (from, index, start) = player.Segment(s);
            var to = s + 1 < player.Segments ? player.Segment(s + 1).From : frames;
            if (s > 0) {
                Release(midi, Math.Min(from, frames - 1));
            }

            if (index >= 0 && cells[index] is { } cell && to > from) {
                CellEvents(midi, cell, blockClock + from - start, from, to, frames);
            }
        }
    }

    /// <summary>
    ///     A cell's events from <paramref name="offset" /> into it, at block frames [<paramref name="from" />,
    ///     <paramref name="to" />).
    /// </summary>
    void CellEvents(MidiTrackState midi, CellContent cell, long offset, int from, int to, int frames) {
        var length = cell.Length;
        if (cell.Mode != CellMode.Loop || length <= 0) {
            AddEvents(midi, cell.View, offset, to - from, from);
            return;
        }

        offset %= length;
        for (var at = from; at < to;) {
            var count = (int)Math.Min(to - at, length - offset);
            AddEvents(midi, cell.View, offset, count, at);
            at += count;
            offset += count;
            if (offset >= length) {
                offset = 0;
                Release(midi, Math.Min(at, frames - 1)); // a lap ends: the next starts clean
            }
        }
    }
}
