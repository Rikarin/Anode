using Anode.Dsp.Instruments;
using Anode.Dsp.Oscillators;
using Anode.Dsp.Transport;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     <c>anode.arpeggiator</c>: plays the held notes one at a time on a grid of note lengths, up, down, up and down
///     (the ends not repeated), at random or in the order played, over 1 … 4 octaves.
/// </summary>
/// <remarks>
///     <para>
///         Timing: while the transport plays, step <c>k</c> falls on quarter note <c>k · rate</c> of the project (so
///         a held note arpeggiates on the bar's grid, through tempo ramps and cycle jumps); swing delays every second
///         step by <c>swing · rate / 2</c>. A step sounds on the first sample at or after its position. Stopped, the
///         grid runs at the context's tempo from the first note of a chord. A step lasts Gate of the rate.
///     </para>
///     <para>
///         The held notes' note-ons and note-offs are consumed; other events pass through. With Latch, released notes
///         keep playing until a key goes down with none held, which starts a new chord. Velocity 0 plays each note at the
///         velocity it was played with, else at that velocity.
///     </para>
/// </remarks>
internal sealed class Arpeggiator : MidiEffect {
    public const string Id = BuiltinMidiEffects.ArpeggiatorId;
    const int MaxNotes = 64;
    const int MaxPending = 256;

    static readonly ParameterList List = new();

    public static readonly int Rate = List.Choice("rate", "Rate", NoteDivisions.Names, NoteDivisions.Sixteenth, 1);
    public static readonly int Mode =
        List.Choice("mode", "Mode", ["Up", "Down", "Up-Down", "Random", "As Played"], 0, 2);
    public static readonly int Octaves = List.Stepped("octaves", "Octaves", 1, 4, 1, string.Empty, 3, 3);
    public static readonly int Gate = List.Add("gate", "Gate", 5, 100, 80, "%", 4);
    public static readonly int Swing = List.Add("swing", "Swing", 0, 100, 0, "%", 5);
    public static readonly int Latch = List.Toggle("latch", "Latch", false, 6);
    public static readonly int Velocity = List.Stepped("velocity", "Velocity", 0, 127, 0, string.Empty, 127);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Arpeggiator", Vendor, Version, List.All);

    // The arpeggio's notes in the order played, and sorted; and the keys physically down.
    readonly int[] played = new int[MaxNotes];
    readonly int[] velocities = new int[MaxNotes];
    readonly int[] channels = new int[MaxNotes];
    readonly int[] sorted = new int[MaxNotes];
    readonly bool[] down = new bool[16 * 128];
    readonly int[] pendingNote = new int[MaxPending];
    readonly int[] pendingChannel = new int[MaxPending];
    readonly double[] pendingSamples = new double[MaxPending];
    int count;
    int held;
    int pending;
    long step;
    long counter;
    double anchor;
    double freeClock;
    double expected;
    bool running;
    bool wasPlaying;
    uint random;

    public Arpeggiator() : base(Info) { }

    public override void Reset() {
        count = 0;
        held = 0;
        pending = 0;
        counter = 0;
        step = 0;
        running = false;
        freeClock = 0;
        expected = double.NaN;
        wasPlaying = false;
        random = 0x2545F491;
        Array.Clear(down);
    }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var frames = transport.Frames;
        var qps = transport.QuartersPerSample;
        var playing = transport.Playing;
        var clock = playing ? transport.Quarter : freeClock;
        var length = NoteDivisions.Length(Parameters[Rate]);
        if (playing != wasPlaying || (playing && Math.Abs(clock - expected) > 1e-6)) {
            // A start, stop or jump: the grid is the project's (anchored at 0) while playing, else from here.
            anchor = playing ? 0 : clock;
            if (running) {
                step = FirstStep(clock, anchor, length);
            }
        }

        wasPlaying = playing;
        if (Parameters[Latch] < 0.5f && held == 0 && count > 0) {
            Clear(); // latch turned off with nothing held
        }

        var next = 0;
        while (true) {
            var stepFrame = running ? FrameOf(step, clock, qps, length) : long.MaxValue;
            var inputFrame = next < input.Length ? input[next].Frame : long.MaxValue;
            if (stepFrame >= frames && inputFrame >= frames) {
                break;
            }

            if (inputFrame <= stepFrame) {
                Handle(input[next++], output, clock, qps, length);
            } else {
                Fire((int)stepFrame, output, qps, length);
            }
        }

        // Note-offs due in this block; the rest wait.
        for (var i = 0; i < pending; i++) {
            if (pendingSamples[i] < frames) {
                output.Add(NoteOff(Math.Max(0, (int)pendingSamples[i]), pendingChannel[i], pendingNote[i]));
                RemovePending(i--);
            } else {
                pendingSamples[i] -= frames;
            }
        }

        freeClock += frames * qps;
        expected = clock + frames * qps;
    }

    /// <summary>The first step at or after <paramref name="clock" />.</summary>
    static long FirstStep(double clock, double anchor, double length) =>
        (long)Math.Ceiling((clock - anchor) / length - 1e-9);

    /// <summary>The frame of step <paramref name="k" />: the first sample at or after its (swung) position.</summary>
    long FrameOf(long k, double clock, double qps, double length) {
        var at = anchor + k * length;
        if ((k & 1) == 1) {
            at += Parameters[Swing] / 100 * length / 2;
        }

        var offset = Math.Ceiling((at - clock) / qps - 1e-6);
        return offset < 0 ? 0 : (long)offset;
    }

    void Handle(in MidiEvent e, MidiEventBuffer output, double clock, double qps, double length) {
        if (IsNoteOn(e)) {
            var key = Slot(e.Status, e.Data1);
            if (Parameters[Latch] >= 0.5f && held == 0) {
                Clear(); // a new chord replaces the latched one
            }

            if (!down[key]) {
                down[key] = true;
                held++;
            }

            Add(e.Data1, e.Data2, e.Status & 0x0F);
            if (!running && count > 0) {
                running = true;
                counter = 0;
                var now = clock + e.Frame * qps;
                if (!wasPlaying) {
                    anchor = now; // stopped: the grid starts with the chord
                }

                step = FirstStep(now, anchor, length);
            }
        } else if (IsNoteOff(e)) {
            var key = Slot(e.Status, e.Data1);
            if (down[key]) {
                down[key] = false;
                held--;
            }

            if (Parameters[Latch] < 0.5f) {
                Remove(e.Data1, e.Status & 0x0F);
            }
        } else {
            output.Add(e);
        }
    }

    void Fire(int frame, MidiEventBuffer output, double qps, double length) {
        // Note-offs due by now go first, so a repeated note is released before it sounds again.
        for (var i = 0; i < pending; i++) {
            if (pendingSamples[i] <= frame) {
                output.Add(NoteOff(Math.Max(0, (int)pendingSamples[i]), pendingChannel[i], pendingNote[i]));
                RemovePending(i--);
            }
        }

        var octaves = (int)Parameters[Octaves];
        var span = count * octaves;
        var c = counter++;
        step++;
        if (span == 0) {
            return;
        }

        int index;
        switch ((int)Parameters[Mode]) {
            case 0:
                index = (int)(c % span);
                break;
            case 1:
                index = span - 1 - (int)(c % span);
                break;
            case 2: {
                    var period = Math.Max(1, 2 * span - 2);
                    var j = (int)(c % period);
                    index = span == 1 ? 0 : j < span ? j : period - j;
                    break;
                }
            case 3:
                _ = PolyBlep.White(ref random);
                index = (int)(random % (uint)span);
                break;
            default:
                index = (int)(c % span);
                break;
        }

        var asPlayed = (int)Parameters[Mode] == 4;
        var slot = index % count;
        var source = asPlayed ? slot : sorted[slot];
        var note = played[source] + 12 * (index / count);
        if (note > 127) {
            return;
        }

        var channel = channels[source];
        var fixedVelocity = (int)Parameters[Velocity];
        var velocity = fixedVelocity > 0 ? fixedVelocity : velocities[source];
        for (var i = 0; i < pending; i++) {
            if (pendingNote[i] == note && pendingChannel[i] == channel) {
                output.Add(NoteOff(frame, channel, note)); // still sounding past its gate: end it here
                RemovePending(i--);
            }
        }

        output.Add(NoteOn(frame, channel, note, velocity));
        if (pending < MaxPending) {
            pendingNote[pending] = note;
            pendingChannel[pending] = channel;
            pendingSamples[pending] = frame + Math.Max(1, Parameters[Gate] / 100 * length / qps);
            pending++;
        }
    }

    void Add(int note, int velocity, int channel) {
        for (var i = 0; i < count; i++) {
            if (played[i] == note && channels[i] == channel) {
                velocities[i] = velocity;
                return;
            }
        }

        if (count == MaxNotes) {
            return;
        }

        played[count] = note;
        velocities[count] = velocity;
        channels[count] = channel;
        count++;
        Sort();
    }

    void Remove(int note, int channel) {
        for (var i = 0; i < count; i++) {
            if (played[i] == note && channels[i] == channel) {
                for (var j = i; j < count - 1; j++) {
                    played[j] = played[j + 1];
                    velocities[j] = velocities[j + 1];
                    channels[j] = channels[j + 1];
                }

                count--;
                Sort();
                break;
            }
        }

        if (count == 0) {
            running = false;
        }
    }

    void Clear() {
        count = 0;
        running = false;
    }

    /// <summary><see cref="sorted" />: the indices of <see cref="played" /> in ascending pitch.</summary>
    void Sort() {
        for (var i = 0; i < count; i++) {
            var j = i;
            while (j > 0 && played[sorted[j - 1]] > played[i]) {
                sorted[j] = sorted[j - 1];
                j--;
            }

            sorted[j] = i;
        }
    }

    void RemovePending(int i) {
        pending--;
        pendingNote[i] = pendingNote[pending];
        pendingChannel[i] = pendingChannel[pending];
        pendingSamples[i] = pendingSamples[pending];
    }
}
