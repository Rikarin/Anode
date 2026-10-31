namespace Anode.Engine.Sync;

/// <summary>
///     MIDI Clock out (docs/09-phase-8.md, "Sync"): 24 clocks per quarter note (every 40 ticks at 960 PPQ), placed to
///     the frame within each block; Start when playback starts at the project start, Song Position Pointer and Continue
///     when it starts elsewhere, Stop when it stops, and a Song Position Pointer on a locate while stopped. Called from
///     the audio callback: no allocation.
/// </summary>
public sealed class MidiClockEncoder {
    /// <summary>
    ///     One block of <paramref name="frames" /> frames over ticks <paramref name="startTick" /> …
    ///     <paramref name="endTick" /> (fractional, 960 PPQ; ticks move linearly across the block) while
    ///     <paramref name="playing" />: writes its messages to <paramref name="output" /> and returns how many.
    /// </summary>
    public int Process(bool playing, double startTick, double endTick, int frames, Span<SyncMidiEvent> output) =>
        throw Pending.WorkstreamA();

    /// <summary>The playhead moved to <paramref name="tick" /> (a locate): the Song Position Pointer, at frame 0.</summary>
    public int Locate(double tick, Span<SyncMidiEvent> output) => throw Pending.WorkstreamA();
}
