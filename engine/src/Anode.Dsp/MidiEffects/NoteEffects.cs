using Anode.Dsp.Instruments;
using Anode.Dsp.Oscillators;
using Anode.Dsp.Transport;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     <c>anode.transposer</c>: moves notes by semitones and, with a scale, onto it: a note is transposed, moved to the
///     scale's nearest note (a tie goes down), then moved Degrees steps along the scale. A note's note-off follows where
///     its note-on went; a note pushed outside 0 … 127 is dropped. Other events pass through.
/// </summary>
internal sealed class Transposer : MidiEffect {
    public const string Id = "anode.transposer";

    static readonly ParameterList List = new();

    public static readonly int Semitones = List.Stepped("semitones", "Transpose", -24, 24, 0, "st", 48, 1);
    public static readonly int Key = List.Choice("key", "Key", Scales.Keys, 0, 2);
    public static readonly int Scale = List.Choice("scale", "Scale", Scales.Names, 0, 3);
    public static readonly int Degrees = List.Stepped("degrees", "Scale Degrees", -7, 7, 0, string.Empty, 14, 4);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Transposer", Vendor, Version, List.All);

    readonly int[] mapped = new int[16 * 128];

    public Transposer() : base(Info) => Reset();

    public override void Reset() => Array.Fill(mapped, -1);

    /// <summary>Where note <paramref name="note" /> goes with the current settings (−1: dropped).</summary>
    public int Map(int note) {
        var target = note + (int)Parameters[Semitones];
        var scale = (int)Parameters[Scale];
        if (scale > 0) {
            var key = (int)Parameters[Key];
            target = Scales.Shift(Scales.Snap(target, key, scale), key, scale, (int)Parameters[Degrees]);
        }

        return target is < 0 or > 127 ? -1 : target;
    }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        foreach (var e in input) {
            if (IsNoteOn(e)) {
                var key = Slot(e.Status, e.Data1);
                if (mapped[key] >= 0) {
                    output.Add(NoteOff(e.Frame, e.Status, mapped[key])); // the key again: end what it played
                }

                var target = Map(e.Data1);
                mapped[key] = target;
                if (target >= 0) {
                    output.Add(e with { Data1 = (byte)target });
                }
            } else if (IsNoteOff(e)) {
                var key = Slot(e.Status, e.Data1);
                var target = mapped[key];
                mapped[key] = -1;
                if (target >= 0) {
                    output.Add(e with { Data1 = (byte)target });
                }
            } else if ((e.Status & 0xF0) == 0xA0) {
                var target = mapped[Slot(e.Status, e.Data1)];
                if (target >= 0) {
                    output.Add(e with { Data1 = (byte)target });
                }
            } else {
                output.Add(e);
            }
        }
    }
}

/// <summary>
///     <c>anode.velocity-processor</c>: reshapes note-on velocities. A velocity is scaled and offset, compressed (or
///     expanded, a ratio under 1) above the threshold, then limited to Min … Max (and at least 1):
///     <c>v' = v·scale + offset; v' = t + (v' − t)/ratio above t</c>. Other events pass through.
/// </summary>
internal sealed class VelocityProcessor : MidiEffect {
    public const string Id = "anode.velocity-processor";

    static readonly ParameterList List = new();

    public static readonly int Scale = List.Add("scale", "Scale", 0, 400, 100, "%", 1);
    public static readonly int Offset = List.Stepped("offset", "Offset", -127, 127, 0, string.Empty, 254, 2);
    public static readonly int Threshold = List.Stepped("threshold", "Threshold", 1, 127, 127, string.Empty, 126, 3);
    public static readonly int Ratio = List.Add("ratio", "Ratio", 0.25f, 8, 1, ":1", 4);
    public static readonly int Min = List.Stepped("min", "Min", 1, 127, 1, string.Empty, 126);
    public static readonly int Max = List.Stepped("max", "Max", 1, 127, 127, string.Empty, 126);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Velocity Processor", Vendor, Version, List.All);

    public VelocityProcessor() : base(Info) { }

    public override void Reset() { }

    /// <summary>The velocity <paramref name="velocity" /> becomes.</summary>
    public int Map(int velocity) {
        var p = Parameters;
        double v = velocity * p[Scale] / 100 + p[Offset];
        var threshold = p[Threshold];
        if (v > threshold) {
            v = threshold + (v - threshold) / p[Ratio];
        }

        var low = Math.Min(p[Min], p[Max]);
        var high = Math.Max(p[Min], p[Max]);
        return (int)Math.Round(Math.Clamp(Math.Clamp(v, low, high), 1, 127));
    }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        foreach (var e in input) {
            output.Add(IsNoteOn(e) ? e with { Data2 = (byte)Map(e.Data2) } : e);
        }
    }
}

/// <summary>
///     <c>anode.randomizer</c>: plays each note with Chance, its velocity moved by up to ±Velocity and, with Pitch
///     Chance, its pitch by up to ±Pitch semitones (uniformly, from a generator seeded by Seed at every reset, so a
///     bounce repeats). A note's note-off follows its note-on; other events pass through.
/// </summary>
internal sealed class Randomizer : MidiEffect {
    public const string Id = "anode.randomizer";

    static readonly ParameterList List = new();

    public static readonly int Chance = List.Add("chance", "Chance", 0, 100, 100, "%", 1);
    public static readonly int Velocity = List.Stepped("velocity", "Velocity Range", 0, 127, 20, string.Empty, 127, 2);
    public static readonly int Pitch = List.Stepped("pitch", "Pitch Range", 0, 24, 0, "st", 24, 3);
    public static readonly int PitchChance = List.Add("pitchChance", "Pitch Chance", 0, 100, 100, "%", 4);
    public static readonly int Seed = List.Stepped("seed", "Seed", 0, 1_000, 0, string.Empty, 1_000);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Randomizer", Vendor, Version, List.All);

    readonly int[] mapped = new int[16 * 128];
    uint state;

    public Randomizer() : base(Info) => Reset();

    public override void Reset() {
        Array.Fill(mapped, -1);
        state = 0x9E3779B9u ^ ((uint)Parameters[Seed] * 0x85EBCA6Bu);
        if (state == 0) {
            state = 1;
        }
    }

    /// <summary>A uniform draw in 0 … 1.</summary>
    double Next() => (PolyBlep.White(ref state) + 1) * 0.5;

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        foreach (var e in input) {
            if (IsNoteOn(e)) {
                var key = Slot(e.Status, e.Data1);
                if (mapped[key] >= 0) {
                    output.Add(NoteOff(e.Frame, e.Status, mapped[key]));
                }

                mapped[key] = -1;
                if (Next() * 100 >= Parameters[Chance]) {
                    continue;
                }

                var note = (int)e.Data1;
                var range = (int)Parameters[Pitch];
                if (range > 0 && Next() * 100 < Parameters[PitchChance]) {
                    note += (int)Math.Floor(Next() * (2 * range + 1)) - range;
                }

                var spread = (int)Parameters[Velocity];
                var velocity = e.Data2 + (spread > 0 ? (int)Math.Floor(Next() * (2 * spread + 1)) - spread : 0);
                if (note is < 0 or > 127) {
                    continue;
                }

                mapped[key] = note;
                output.Add(NoteOn(e.Frame, e.Status, note, velocity));
            } else if (IsNoteOff(e)) {
                var key = Slot(e.Status, e.Data1);
                if (mapped[key] >= 0) {
                    output.Add(e with { Data1 = (byte)mapped[key] });
                    mapped[key] = -1;
                }
            } else {
                output.Add(e);
            }
        }
    }
}

/// <summary>
///     <c>anode.note-repeater</c>: each note plays again Repeats times, Rate apart (a note length, so it follows the
///     tempo), each repeat Gate of the rate long, its velocity changed by Velocity Ramp (a percentage of the original,
///     per repeat) and its pitch by Transpose semitones per repeat. The original passes through as played. Repeats are
///     timed on the effect's own clock (the contexts' tempo, block after block), so they keep going through a stop or a
///     cycle jump.
/// </summary>
internal sealed class NoteRepeater : MidiEffect {
    public const string Id = "anode.note-repeater";
    const int MaxPending = 512;

    static readonly ParameterList List = new();

    public static readonly int Rate = List.Choice("rate", "Rate", NoteDivisions.Names, NoteDivisions.Sixteenth, 1);
    public static readonly int Repeats = List.Stepped("repeats", "Repeats", 1, 32, 3, string.Empty, 31, 2);
    public static readonly int VelocityRamp = List.Add("velocityRamp", "Velocity Ramp", -100, 100, -20, "%", 3);
    public static readonly int Transpose = List.Stepped("transpose", "Transpose", -12, 12, 0, "st", 24, 4);
    public static readonly int Gate = List.Add("gate", "Gate", 5, 100, 50, "%", 5);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Note Repeater", Vendor, Version, List.All);

    readonly double[] at = new double[MaxPending];
    readonly byte[] status = new byte[MaxPending];
    readonly byte[] note = new byte[MaxPending];
    readonly byte[] velocity = new byte[MaxPending];
    int pending;
    double clock;

    public NoteRepeater() : base(Info) { }

    public override void Reset() {
        pending = 0;
        clock = 0;
    }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var qps = transport.QuartersPerSample;
        var frames = transport.Frames;
        foreach (var e in input) {
            output.Add(e);
            if (!IsNoteOn(e)) {
                continue;
            }

            var start = clock + e.Frame * qps;
            var length = NoteDivisions.Length(Parameters[Rate]);
            var gate = Parameters[Gate] / 100 * length;
            var repeats = (int)Parameters[Repeats];
            var ramp = Parameters[VelocityRamp] / 100;
            var step = (int)Parameters[Transpose];
            var channel = e.Status & 0x0F;
            for (var k = 1; k <= repeats; k++) {
                var pitch = e.Data1 + k * step;
                var v = (int)Math.Round(e.Data2 * (1 + ramp * k));
                if (pitch is < 0 or > 127 || v < 1) {
                    continue;
                }

                var on = start + k * length;
                Schedule(on, (byte)(0x90 | channel), pitch, Math.Min(v, 127));
                Schedule(on + gate, (byte)(0x80 | channel), pitch, 0);
            }
        }

        // Due events, note-offs first: a repeat that ends where the next starts lets go before it sounds again.
        var end = clock + frames * qps;
        for (var pass = 0; pass < 2; pass++) {
            Emit(output, end, qps, frames, pass == 0 ? 0x80 : 0x90);
        }

        clock = end;
    }

    void Emit(MidiEventBuffer output, double end, double qps, int frames, int kind) {
        for (var i = 0; i < pending; i++) {
            if ((status[i] & 0xF0) == kind && at[i] < end - 1e-9) {
                var frame = Math.Clamp((int)Math.Ceiling((at[i] - clock) / qps - 1e-6), 0, frames - 1);
                output.Add(new(frame, status[i], note[i], velocity[i]));
                pending--;
                at[i] = at[pending];
                status[i] = status[pending];
                note[i] = note[pending];
                velocity[i] = velocity[pending];
                i--;
            }
        }
    }

    void Schedule(double time, byte s, int pitch, int v) {
        if (pending == MaxPending) {
            return;
        }

        at[pending] = time;
        status[pending] = s;
        note[pending] = (byte)pitch;
        velocity[pending] = (byte)v;
        pending++;
    }
}

/// <summary>
///     <c>anode.modifier</c>: remaps one controller. Events of the Input controller become the Output (a controller,
///     channel aftertouch or pitch bend) with their value scaled and offset (<c>v·scale + add</c>, clamped to 0 … 127);
///     with Thru, the original passes too. Everything else passes through.
/// </summary>
internal sealed class Modifier : MidiEffect {
    public const string Id = "anode.modifier";

    /// <summary>The Output value for channel aftertouch.</summary>
    public const int Aftertouch = 128;

    /// <summary>The Output value for pitch bend.</summary>
    public const int PitchBend = 129;

    static readonly ParameterList List = new();

    public static readonly int Input = List.Stepped("input", "Input Controller", 0, 127, 1, string.Empty, 127, 1);
    public static readonly int Output = List.Stepped("output", "Output", 0, 129, 11, string.Empty, 129, 2);
    public static readonly int Scale = List.Add("scale", "Scale", -200, 200, 100, "%", 3);
    public static readonly int Add = List.Stepped("add", "Add", -127, 127, 0, string.Empty, 254, 4);
    public static readonly int Thru = List.Toggle("thru", "Thru", false);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Modifier", Vendor, Version, List.All);

    public Modifier() : base(Info) { }

    public override void Reset() { }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var source = (int)Parameters[Input];
        foreach (var e in input) {
            if ((e.Status & 0xF0) != 0xB0 || e.Data1 != source) {
                output.Add(e);
                continue;
            }

            if (Parameters[Thru] >= 0.5f) {
                output.Add(e);
            }

            var value = (int)Math.Round(Math.Clamp(e.Data2 * Parameters[Scale] / 100 + Parameters[Add], 0, 127));
            output.Add(ControllerOutput.Event(e.Frame, e.Status & 0x0F, (int)Parameters[Output], value));
        }
    }
}

/// <summary>Controller, aftertouch and pitch bend events for an output value 0 … 127.</summary>
internal static class ControllerOutput {
    public static MidiEvent Event(int frame, int channel, int target, int value) =>
        target switch {
            Modifier.Aftertouch => new(frame, (byte)(0xD0 | channel), (byte)value, 0),
            Modifier.PitchBend => Bend(frame, channel, value),
            _ => new(frame, (byte)(0xB0 | channel), (byte)target, (byte)value)
        };

    /// <summary>A 0 … 127 value as a 14-bit bend: 64 is the centre, 127 the top.</summary>
    static MidiEvent Bend(int frame, int channel, int value) {
        var bend = value >= 64 ? 8192 + (value - 64) * 8191 / 63 : value * 8192 / 64;
        return new(frame, (byte)(0xE0 | channel), (byte)(bend & 0x7F), (byte)((bend >> 7) & 0x7F));
    }
}

/// <summary>
///     <c>anode.modulator</c>: an LFO and an envelope sent as a controller (or channel aftertouch or pitch bend) on
///     channel 1: <c>offset + depth·63.5·lfo + amount·127·envelope</c>, clamped to 0 … 127, sent every 32 samples (on a
///     grid counted from the reset) when the value changes. The LFO runs in Hz or synced (while the transport plays its
///     phase is the project position over the note length); the envelope (attack, release) opens while any note is held.
///     Every input event passes through.
/// </summary>
internal sealed class Modulator : MidiEffect {
    public const string Id = "anode.modulator";

    /// <summary>The output grid, in samples.</summary>
    public const int Interval = 32;

    static readonly ParameterList List = new();

    public static readonly int Target = List.Stepped("target", "Target", 0, 129, 1, string.Empty, 129);
    public static readonly int Shape =
        List.Choice("shape", "LFO Shape", ["Sine", "Triangle", "Saw", "Square", "Sample & Hold"], 0, 3);
    public static readonly int Rate = List.Add("rate", "LFO Rate", 0.05f, 20, 2, "Hz", 1);
    public static readonly int Sync = List.Choice("sync", "LFO Sync", ["Off", .. NoteDivisions.Names], 0);
    public static readonly int Depth = List.Add("depth", "LFO Depth", 0, 100, 50, "%", 2);
    public static readonly int Offset = List.Stepped("offset", "Offset", 0, 127, 64, string.Empty, 127, 4);
    public static readonly int Attack = List.Add("attack", "Env Attack", 0, 10_000, 100, "ms");
    public static readonly int Release = List.Add("release", "Env Release", 0, 10_000, 300, "ms");
    public static readonly int Amount = List.Add("amount", "Env Amount", -100, 100, 0, "%", 5);

    public static MidiEffectDescriptor Info { get; } = new(Id, "Modulator", Vendor, Version, List.All);

    readonly bool[] down = new bool[16 * 128];
    int held;
    long position;
    double phase;
    double envelope;
    float sampleHeld;
    int last = -1;
    uint random;

    public Modulator() : base(Info) => Reset();

    public override void Reset() {
        Array.Clear(down);
        held = 0;
        position = 0;
        phase = 0;
        envelope = 0;
        last = -1;
        random = 0x2545F491;
        sampleHeld = PolyBlep.White(ref random);
    }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var frames = transport.Frames;
        var qps = transport.QuartersPerSample;
        var p = Parameters;
        var sync = (int)p[Sync];
        var length = sync > 0 ? NoteDivisions.Quarters[sync - 1] : 0;
        var increment = (sync > 0 ? transport.Bpm / 60 / length : p[Rate]) / SampleRate;
        var attack = Math.Max(1, p[Attack] * 0.001 * SampleRate);
        var release = Math.Max(1, p[Release] * 0.001 * SampleRate);
        var next = 0;
        var first = (int)((Interval - position % Interval) % Interval);
        var from = 0;
        for (var frame = first; frame < frames || next < input.Length; frame += Interval) {
            // Inputs up to the output point, in order.
            while (next < input.Length && (frame >= frames || input[next].Frame <= frame)) {
                var e = input[next++];
                output.Add(e);
                Track(e);
            }

            if (frame >= frames) {
                break;
            }

            // Advance the modulators to this frame.
            var elapsed = frame - from;
            from = frame;
            Advance(elapsed, increment, attack, release);
            var lfoPhase = sync > 0 && transport.Playing
                ? Fraction((transport.Quarter + frame * qps) / length)
                : phase;
            var lfo = (int)p[Shape] switch {
                0 => Math.Sin(2 * Math.PI * lfoPhase),
                1 => lfoPhase < 0.25 ? 4 * lfoPhase : lfoPhase < 0.75 ? 2 - 4 * lfoPhase : 4 * lfoPhase - 4,
                2 => 2 * lfoPhase - 1,
                3 => lfoPhase < 0.5 ? 1 : -1,
                _ => sampleHeld
            };
            var value = (int)Math.Round(
                Math.Clamp(p[Offset] + p[Depth] / 100 * 63.5 * lfo + p[Amount] / 100 * 127 * envelope, 0, 127)
            );
            if (value != last) {
                last = value;
                output.Add(ControllerOutput.Event(frame, 0, (int)p[Target], value));
            }
        }

        Advance(frames - from, increment, attack, release);
        position += frames;
    }

    void Advance(int samples, double increment, double attack, double release) {
        if (samples <= 0) {
            return;
        }

        phase += increment * samples;
        if (phase >= 1) {
            phase -= Math.Floor(phase);
            sampleHeld = PolyBlep.White(ref random);
        }

        envelope = held > 0 ? Math.Min(1, envelope + samples / attack) : Math.Max(0, envelope - samples / release);
    }

    void Track(in MidiEvent e) {
        if (IsNoteOn(e)) {
            var key = Slot(e.Status, e.Data1);
            if (!down[key]) {
                down[key] = true;
                held++;
            }
        } else if (IsNoteOff(e)) {
            var key = Slot(e.Status, e.Data1);
            if (down[key]) {
                down[key] = false;
                held--;
            }
        }
    }

    static double Fraction(double x) => x - Math.Floor(x);
}
