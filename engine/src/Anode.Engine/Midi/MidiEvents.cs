namespace Anode.Engine.Midi;

/// <summary>
///     Live MIDI for one track (from a device or the server), stamped with when it arrived on the monotonic clock in
///     nanoseconds (0: as soon as possible).
/// </summary>
public readonly record struct LiveMidi(long TimeNs, byte Status, byte Data1, byte Data2);

/// <summary>A message the audio thread sends to an external MIDI track's port, due at <see cref="TimeNs" />.</summary>
public readonly record struct OutgoingMidi(int TrackId, long TimeNs, byte Status, byte Data1, byte Data2);

/// <summary>
///     Live MIDI a track received, at the project sample it was played at (latency already compensated): an armed
///     track's while recording, or, with <see cref="Flashback" />, any track's while playing (Flashback Capture's
///     history, with the transport <see cref="Clock" /> it was played at).
/// </summary>
public readonly record struct CapturedMidi(
    int TrackId,
    long Sample,
    byte Status,
    byte Data1,
    byte Data2,
    long Clock = 0,
    bool Flashback = false);

/// <summary>A parameter change on its way from the control thread to the audio thread.</summary>
public readonly record struct ParameterChange(int Index, float Value);

/// <summary>Which notes sound, per channel: 16 × 128 bits. Audio-thread state, allocation-free.</summary>
public sealed class NoteSet {
    readonly ulong[] bits = new ulong[16 * 2];
    int count;

    public bool IsEmpty => count == 0;

    public void On(int channel, int note) {
        ref var word = ref bits[(channel & 15) * 2 + ((note >> 6) & 1)];
        var mask = 1UL << (note & 63);
        if ((word & mask) == 0) {
            word |= mask;
            count++;
        }
    }

    public void Off(int channel, int note) {
        ref var word = ref bits[(channel & 15) * 2 + ((note >> 6) & 1)];
        var mask = 1UL << (note & 63);
        if ((word & mask) != 0) {
            word &= ~mask;
            count--;
        }
    }

    /// <summary>
    ///     Tracks a message: a note-on with a velocity sets its note, a note-off (or a note-on at velocity 0) clears it.
    /// </summary>
    public void Track(byte status, byte data1, byte data2) {
        switch (status & 0xF0) {
            case 0x90 when data2 > 0:
                On(status & 0x0F, data1 & 0x7F);
                break;
            case 0x80:
            case 0x90:
                Off(status & 0x0F, data1 & 0x7F);
                break;
        }
    }

    /// <summary>
    ///     Writes a note-off (status, note) for every sounding note into <paramref name="into" />,
    ///     clears them, and returns how many it wrote (stopping when <paramref name="into" /> is
    ///     full).
    /// </summary>
    public int Release(Span<(byte Status, byte Note)> into) {
        var n = 0;
        for (var w = 0; w < bits.Length && n < into.Length; w++) {
            while (bits[w] != 0 && n < into.Length) {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(bits[w]);
                bits[w] &= bits[w] - 1;
                count--;
                into[n++] = ((byte)(0x80 | (w >> 1)), (byte)(((w & 1) << 6) | bit));
            }
        }

        return n;
    }

    public void Clear() {
        Array.Clear(bits);
        count = 0;
    }
}
