using Anode.Ipc;

namespace Anode.Engine.Midi;

/// <summary>
///     MIDI step input's held-key state (Phase 7, <c>stepInputMidi</c>): the keys that went down since the previous
///     step make the next one, sent when the first of them is released, so several keys down before any goes up make a
///     chord. A key that belonged to a step already sent ends without making another. Keys are pitches, whatever their
///     channel; a step's velocity is the highest of its keys'. Not thread-safe: its owner serialises it.
/// </summary>
public sealed class StepChord {
    readonly List<int> pending = [];
    readonly bool[] down = new bool[128];
    int velocity;

    /// <summary>Takes one MIDI message; returns the step it completes, or null (messages other than notes are ignored).</summary>
    public StepInputMidiMessage? Take(int status, int data1, int data2) {
        var note = data1 & 0x7F;
        switch (status & 0xF0) {
            case 0x90 when (data2 & 0x7F) > 0:
                if (!down[note]) {
                    down[note] = true;
                    if (!pending.Contains(note)) {
                        pending.Add(note);
                    }

                    velocity = Math.Max(velocity, data2 & 0x7F);
                }

                return null;
            case 0x80:
            case 0x90:
                if (!down[note]) {
                    return null;
                }

                down[note] = false;
                if (!pending.Contains(note)) {
                    return null; // part of a step already sent
                }

                var step = new StepInputMidiMessage([..pending], velocity);
                pending.Clear();
                velocity = 0;
                return step;
            default:
                return null;
        }
    }
}
