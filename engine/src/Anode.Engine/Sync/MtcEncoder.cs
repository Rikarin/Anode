using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>
///     MTC out (docs/09-phase-8.md, "Sync") at 24, 25, 29.97 drop or 30 fps: quarter frames (F1 nn), four per frame,
///     placed to the frame within each block while playing; a full frame (SysEx F0 7F 7F 01 01 hh mm ss ff F7) on a
///     locate. Positions are seconds of timecode (the project time plus the start offset). Called from the audio
///     callback: no allocation.
/// </summary>
public sealed class MtcEncoder {
    public MtcEncoder(EngineMtcRate rate) {
        throw Pending.WorkstreamA();
    }

    public EngineMtcRate Rate => throw Pending.WorkstreamA();

    /// <summary>
    ///     One block of <paramref name="frames" /> frames over timecode <paramref name="startSeconds" /> …
    ///     <paramref name="endSeconds" /> while <paramref name="playing" />: writes its quarter frames to
    ///     <paramref name="output" /> and returns how many.
    /// </summary>
    public int Process(bool playing, double startSeconds, double endSeconds, int frames, Span<SyncMidiEvent> output) =>
        throw Pending.WorkstreamA();

    /// <summary>The full-frame SysEx for timecode <paramref name="seconds" /> into <paramref name="output" /> (10 bytes).</summary>
    public int FullFrame(double seconds, Span<byte> output) => throw Pending.WorkstreamA();
}
