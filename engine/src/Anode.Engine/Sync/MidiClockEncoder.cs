namespace Anode.Engine.Sync;

/// <summary>
///     MIDI Clock out (docs/09-phase-8.md, "Sync"): 24 clocks per quarter note (every 40 ticks at 960 PPQ), placed to
///     the frame within each block; Start when playback starts at the project start, Song Position Pointer and Continue
///     when it starts elsewhere, Stop when it stops, and a Song Position Pointer on a locate while stopped. Called from
///     the audio callback: no allocation.
/// </summary>
/// <remarks>
///     A receiver plays the position a Song Position Pointer names on the first clock after Continue, so playback that
///     starts between sixteenths points at the next one and sends no clock until it is reached. A jump while playing (a
///     cycle wrap, a locate) is Stop, the pointer and Continue, as MIDI forbids the pointer while running.
/// </remarks>
public sealed class MidiClockEncoder {
    /// <summary>Ticks (960 PPQ) between clocks.</summary>
    public const double TicksPerClock = 40;

    /// <summary>Ticks (960 PPQ) per Song Position Pointer unit, a sixteenth note.</summary>
    public const double TicksPerSixteenth = 240;

    const double Epsilon = 1e-6;

    bool playing;
    double nextClock;
    double lastEnd = double.NaN;

    /// <summary>Whether the last block played (the receiver was started).</summary>
    public bool Playing => playing;

    /// <summary>
    ///     One block of <paramref name="frames" /> frames over ticks <paramref name="startTick" /> …
    ///     <paramref name="endTick" /> (fractional, 960 PPQ; ticks move linearly across the block) while
    ///     <paramref name="playing" />: writes its messages to <paramref name="output" /> and returns how many.
    /// </summary>
    public int Process(bool playing, double startTick, double endTick, int frames, Span<SyncMidiEvent> output) {
        var count = 0;
        if (!playing) {
            if (this.playing) {
                this.playing = false;
                Add(output, ref count, new(0, 0xFC, 0, 0, 1));
            }

            lastEnd = double.NaN;
            return count;
        }

        startTick = Math.Max(0, startTick);
        if (!this.playing) {
            this.playing = true;
            if (startTick < Epsilon) {
                Add(output, ref count, new(0, 0xFA, 0, 0, 1)); // Start: the first clock is the project start
                nextClock = 0;
            } else {
                count += Resume(startTick, output[count..]);
            }
        } else if (!double.IsNaN(lastEnd) && Math.Abs(startTick - lastEnd) > 0.5) {
            Add(output, ref count, new(0, 0xFC, 0, 0, 1)); // a jump: stop, point, continue
            count += Resume(startTick, output[count..]);
        }

        lastEnd = endTick;
        var span = endTick - startTick;
        while (nextClock < endTick - Epsilon && count < output.Length) {
            var frame = span <= 0 ? 0 : (int)Math.Floor((nextClock - startTick) / span * frames + Epsilon);
            Add(output, ref count, new(Math.Clamp(frame, 0, Math.Max(0, frames - 1)), 0xF8, 0, 0, 1));
            nextClock += TicksPerClock;
        }

        return count;
    }

    /// <summary>The playhead moved to <paramref name="tick" /> (a locate): the Song Position Pointer, at frame 0.</summary>
    public int Locate(double tick, Span<SyncMidiEvent> output) {
        var count = 0;
        Add(output, ref count, Pointer((int)Math.Floor(Math.Max(0, tick) / TicksPerSixteenth + Epsilon)));
        lastEnd = double.NaN;
        return count;
    }

    /// <summary>Forgets the receiver's state (a new destination list): the next playing block starts it again.</summary>
    public void Reset() {
        playing = false;
        lastEnd = double.NaN;
    }

    /// <summary>A Song Position Pointer to the next sixteenth at or after the tick, and Continue; clocks resume there.</summary>
    int Resume(double tick, Span<SyncMidiEvent> output) {
        var sixteenth = (int)Math.Ceiling(tick / TicksPerSixteenth - Epsilon);
        var count = 0;
        Add(output, ref count, Pointer(sixteenth));
        Add(output, ref count, new(0, 0xFB, 0, 0, 1));
        nextClock = sixteenth * TicksPerSixteenth;
        return count;
    }

    static SyncMidiEvent Pointer(int sixteenths) {
        var value = Math.Clamp(sixteenths, 0, 16383);
        return new(0, 0xF2, (byte)(value & 0x7F), (byte)(value >> 7), 3);
    }

    static void Add(Span<SyncMidiEvent> output, ref int count, in SyncMidiEvent e) {
        if (count < output.Length) {
            output[count++] = e;
        }
    }
}
