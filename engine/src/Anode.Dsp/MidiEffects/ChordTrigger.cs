using Anode.Dsp.Instruments;
using Anode.Dsp.Transport;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     <c>anode.chord-trigger</c>: one key plays a chord. Single mode plays the Chord parameter's chord (or the learned
///     one) on every key, transposed to it; multi mode plays each key's own learned chord (a key with none plays
///     itself).
/// </summary>
/// <remarks>
///     <para>
///         Learn: while Learn is on, notes pass through as played and are collected; the first key down (with none
///         held) is the trigger, every key held with it joins the chord, and releasing them all stores it: in single
///         mode as intervals from the trigger (the Chord parameter's <c>Learned</c> value plays it), in multi mode as
///         the trigger key's chord. The learned chords are the effect's state (after the parameters: <c>CHRD</c>, the
///         single chord, then the multi chords) and can be set on the control thread with <see cref="SetChord" />.
///     </para>
///     <para>
///         A chord's notes go out with the trigger's velocity and channel, and stop with the trigger's note-off; a note
///         two chords share sounds until both have let it go. Notes outside 0 … 127 are dropped.
///     </para>
/// </remarks>
internal sealed class ChordTrigger : MidiEffect {
    public const string Id = "anode.chord-trigger";

    /// <summary>The most notes in a chord.</summary>
    public const int MaxChord = 12;

    static ReadOnlySpan<byte> Magic => "CHRD"u8;

    static readonly ParameterList List = new();

    static readonly string[] ChordNames = [
        "Major", "Minor", "Dominant 7", "Major 7", "Minor 7", "Sus 2", "Sus 4", "Diminished", "Augmented", "Power",
        "Octaves", "Learned"
    ];

    static readonly int[][] Chords = [
        [0, 4, 7], [0, 3, 7], [0, 4, 7, 10], [0, 4, 7, 11], [0, 3, 7, 10], [0, 2, 7], [0, 5, 7], [0, 3, 6], [0, 4, 8],
        [0, 7, 12], [0, 12, 24]
    ];

    public static readonly int Mode = List.Choice("mode", "Mode", ["Single", "Multi"], 0, 2);
    public static readonly int Chord = List.Choice("chord", "Chord", ChordNames, 0, 1);
    public static readonly int Learn = List.Toggle("learn", "Learn", false);

    /// <summary>The <see cref="Chord" /> value that plays the learned single chord.</summary>
    public const int Learned = 11;

    public static MidiEffectDescriptor Info { get; } = new(Id, "Chord Trigger", Vendor, Version, List.All);

    // The learned chords: the single chord's intervals and each key's chord (absolute notes).
    readonly int[] single = new int[MaxChord];
    readonly int[] multi = new int[128 * MaxChord];
    readonly int[] multiCount = new int[128];

    // What each sounding trigger (channel, key) played, and how many triggers hold each output note.
    readonly int[] playing = new int[16 * 128 * MaxChord];
    readonly int[] playingCount = new int[16 * 128];
    readonly int[] holders = new int[16 * 128];

    // Learning.
    readonly int[] learning = new int[MaxChord];
    readonly bool[] learnDown = new bool[128];
    int singleCount;
    int learnCount;
    int learnTrigger = -1;
    int learnHeld;

    public ChordTrigger() : base(Info) {
        single[0] = 0;
        single[1] = 4;
        single[2] = 7;
        singleCount = 3;
    }

    /// <summary>
    ///     Control thread: stores <paramref name="notes" /> (at most <see cref="MaxChord" />) as the chord of key
    ///     <paramref name="trigger" /> (multi mode), or with <paramref name="trigger" /> −1 as the single chord's
    ///     intervals.
    /// </summary>
    public void SetChord(int trigger, ReadOnlySpan<int> notes) {
        var n = Math.Min(notes.Length, MaxChord);
        if (trigger < 0) {
            notes[..n].CopyTo(single);
            singleCount = n;
        } else {
            notes[..n].CopyTo(multi.AsSpan((trigger & 0x7F) * MaxChord));
            multiCount[trigger & 0x7F] = n;
        }
    }

    /// <summary>The chord of key <paramref name="trigger" />, or with −1 the single chord's intervals.</summary>
    public int[] GetChord(int trigger) =>
        trigger < 0
            ? single[..singleCount]
            : multi.AsSpan((trigger & 0x7F) * MaxChord, multiCount[trigger & 0x7F]).ToArray();

    public override void Reset() {
        Array.Clear(playingCount);
        Array.Clear(holders);
        Array.Clear(learnDown);
        learnCount = 0;
        learnTrigger = -1;
        learnHeld = 0;
    }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var learn = Parameters[Learn] >= 0.5f;
        foreach (var e in input) {
            if (learn && (IsNoteOn(e) || IsNoteOff(e))) {
                LearnFrom(e);
                output.Add(e);
            } else if (IsNoteOn(e)) {
                Trigger(e, output);
            } else if (IsNoteOff(e)) {
                Release(e, output);
            } else {
                output.Add(e);
            }
        }
    }

    void Trigger(in MidiEvent e, MidiEventBuffer output) {
        var channel = e.Status & 0x0F;
        var key = Slot(channel, e.Data1);
        if (playingCount[key] > 0) {
            Release(e, output); // the same key again: let go of what it played first
        }

        var notes = playing.AsSpan(key * MaxChord, MaxChord);
        var n = 0;
        if (Parameters[Mode] >= 0.5f) {
            var count = multiCount[e.Data1];
            if (count == 0) {
                notes[n++] = e.Data1;
            } else {
                for (var i = 0; i < count; i++) {
                    notes[n++] = multi[e.Data1 * MaxChord + i];
                }
            }
        } else {
            var chord = (int)Parameters[Chord];
            ReadOnlySpan<int> intervals = chord == Learned ? single.AsSpan(0, singleCount) : Chords[chord];
            foreach (var interval in intervals) {
                notes[n++] = e.Data1 + interval;
            }
        }

        var kept = 0;
        for (var i = 0; i < n; i++) {
            var note = notes[i];
            if (note is < 0 or > 127) {
                continue;
            }

            notes[kept++] = note;
            holders[Slot(channel, note)]++;
            output.Add(NoteOn(e.Frame, channel, note, e.Data2));
        }

        playingCount[key] = kept;
    }

    void Release(in MidiEvent e, MidiEventBuffer output) {
        var channel = e.Status & 0x0F;
        var key = Slot(channel, e.Data1);
        var count = playingCount[key];
        for (var i = 0; i < count; i++) {
            var note = playing[key * MaxChord + i];
            var held = Slot(channel, note);
            if (holders[held] > 0 && --holders[held] == 0) {
                output.Add(NoteOff(e.Frame, channel, note));
            }
        }

        playingCount[key] = 0;
    }

    void LearnFrom(in MidiEvent e) {
        var note = e.Data1 & 0x7F;
        if (IsNoteOn(e)) {
            if (learnHeld == 0) {
                learnTrigger = note;
                learnCount = 0;
            }

            if (!learnDown[note]) {
                learnDown[note] = true;
                learnHeld++;
            }

            if (learnCount < MaxChord && learning.AsSpan(0, learnCount).IndexOf(note) < 0) {
                learning[learnCount++] = note;
            }

            return;
        }

        if (learnDown[note]) {
            learnDown[note] = false;
            learnHeld--;
        }

        if (learnHeld > 0 || learnTrigger < 0) {
            return;
        }

        // All keys up: the chord is learned, sorted.
        var chord = learning.AsSpan(0, learnCount);
        chord.Sort();
        if (Parameters[Mode] >= 0.5f) {
            chord.CopyTo(multi.AsSpan(learnTrigger * MaxChord));
            multiCount[learnTrigger] = learnCount;
        } else {
            for (var i = 0; i < learnCount; i++) {
                single[i] = chord[i] - learnTrigger;
            }

            singleCount = learnCount;
        }

        learnTrigger = -1;
        learnCount = 0;
    }

    public override byte[]? SaveState() {
        var parameters = base.SaveState()!;
        var chords = 0;
        foreach (var count in multiCount) {
            chords += count > 0 ? 1 : 0;
        }

        using var stream = new MemoryStream();
        stream.Write(parameters);
        stream.Write(Magic);
        stream.WriteByte((byte)singleCount);
        foreach (var interval in single.AsSpan(0, singleCount)) {
            stream.WriteByte((byte)(sbyte)interval);
        }

        stream.WriteByte((byte)chords);
        for (var key = 0; key < 128; key++) {
            var count = multiCount[key];
            if (count == 0) {
                continue;
            }

            stream.WriteByte((byte)key);
            stream.WriteByte((byte)count);
            foreach (var note in multi.AsSpan(key * MaxChord, count)) {
                stream.WriteByte((byte)note);
            }
        }

        return stream.ToArray();
    }

    public override bool LoadState(ReadOnlySpan<byte> state) {
        if (!InstrumentState.TryRead(state, Id, out _, out var values, out var end)) {
            return false;
        }

        var rest = state[end..];
        Span<int> newSingle = stackalloc int[MaxChord];
        var newSingleCount = 3;
        newSingle[0] = 0;
        newSingle[1] = 4;
        newSingle[2] = 7;
        var newMulti = new int[multi.Length];
        var newMultiCount = new int[128];
        if (rest.Length > 0) {
            if (rest.Length < 6 || !rest[..4].SequenceEqual(Magic)) {
                return false;
            }

            var at = 4;
            newSingleCount = rest[at++];
            if (newSingleCount > MaxChord || at + newSingleCount + 1 > rest.Length) {
                return false;
            }

            for (var i = 0; i < newSingleCount; i++) {
                newSingle[i] = (sbyte)rest[at++];
            }

            var chords = rest[at++];
            for (var c = 0; c < chords; c++) {
                if (at + 2 > rest.Length) {
                    return false;
                }

                var key = rest[at++] & 0x7F;
                var count = rest[at++];
                if (count > MaxChord || at + count > rest.Length) {
                    return false;
                }

                for (var i = 0; i < count; i++) {
                    newMulti[key * MaxChord + i] = rest[at++] & 0x7F;
                }

                newMultiCount[key] = count;
            }
        }

        InstrumentState.Apply(values, Parameters);
        newSingle[..newSingleCount].CopyTo(single);
        singleCount = newSingleCount;
        newMulti.CopyTo(multi, 0);
        newMultiCount.CopyTo(multiCount, 0);
        Reset();
        return true;
    }
}
