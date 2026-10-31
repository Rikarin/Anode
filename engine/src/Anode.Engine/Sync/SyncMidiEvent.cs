namespace Anode.Engine.Sync;

/// <summary>
///     A sync message an encoder produced for a block: the frame within the block it belongs to (the host sends it at
///     that frame's host time plus the destination's delay) and its bytes (1–3; <see cref="Length" /> of them).
/// </summary>
public readonly record struct SyncMidiEvent(int Frame, byte Status, byte Data1, byte Data2, byte Length);
