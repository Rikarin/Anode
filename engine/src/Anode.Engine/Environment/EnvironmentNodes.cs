using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Ipc;
using Anode.Ipc.Telemetry;

namespace Anode.Engine.Environment;

/// <summary>
///     An Environment object as the host runs it (workstream A): built on the control thread from its
///     <see cref="EngineEnvironmentObject" />, then fed events at their inlets on the audio thread, emitting at its
///     outlets. Objects that hold events (delay lines, the arpeggiator) emit them through
///     <see cref="IEnvironmentEmitter.Schedule" /> or when <see cref="Advance" /> reaches them.
/// </summary>
/// <remarks>
///     Inlet −1 is live MIDI into the object itself (<see cref="EnvironmentGraph.InputAt" />: an on-screen keyboard
///     played, an on-screen fader moved); the graph feeds physical inputs at inlet 0.
/// </remarks>
public abstract class EnvironmentNode(EngineEnvironmentObject settings) {
    public EngineEnvironmentObject Settings { get; } = settings;

    /// <summary>On the control thread, once: allocates what processing needs.</summary>
    public virtual void Prepare(float sampleRate, int maxFrames) { }

    /// <summary>An event at inlet <paramref name="inlet" />.</summary>
    public abstract void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit);

    /// <summary>
    ///     The block [<paramref name="blockStart" />, + <paramref name="frames" />) has brought everything to the
    ///     object's inlets (its <see cref="Receive" />s are done): what it holds that falls in the block goes out.
    /// </summary>
    public virtual void Advance(long blockStart, int frames, IEnvironmentEmitter emit) { }

    public virtual void Reset() { }

    /// <summary>The numeric parameter <paramref name="id" />, or <paramref name="fallback" /> when it is not set.</summary>
    protected double Parameter(string id, double fallback) =>
        Settings.Parameters.TryGetValue(id, out var value) && double.IsFinite(value) ? value : fallback;

    /// <summary>The node for <paramref name="settings" />' kind.</summary>
    public static EnvironmentNode Create(EngineEnvironmentObject settings) =>
        settings.Kind switch {
            EngineEnvironmentKind.PhysicalInput => new PhysicalInputNode(settings),
            EngineEnvironmentKind.SequencerInput => new SequencerInputNode(settings),
            EngineEnvironmentKind.Keyboard => new KeyboardNode(settings),
            EngineEnvironmentKind.Monitor => new MonitorNode(settings),
            EngineEnvironmentKind.Fader => new FaderNode(settings),
            EngineEnvironmentKind.Transformer => new TransformerNode(settings),
            EngineEnvironmentKind.DelayLine => new DelayLineNode(settings),
            EngineEnvironmentKind.Arpeggiator => new ArpeggiatorNode(settings),
            EngineEnvironmentKind.ChordMemorizer => new ChordMemorizerNode(settings),
            EngineEnvironmentKind.ChannelSplitter => new ChannelSplitterNode(settings),
            EngineEnvironmentKind.MidiPort => new MidiPortNode(settings),
            EngineEnvironmentKind.TrackInput => new TrackInputNode(settings),
            EngineEnvironmentKind.ControlTarget => new ControlTargetNode(settings),
            _ => throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Kind,
                "Unknown Environment object kind."
            )
        };

    /// <summary>The status byte with channel <paramref name="channel" /> (1…16).</summary>
    protected static byte OnChannel(byte status, int channel) =>
        status >= 0xF0 ? status : (byte)((status & 0xF0) | (Math.Clamp(channel, 1, 16) - 1));

    protected static bool IsNoteOn(byte status, byte velocity) => (status & 0xF0) == 0x90 && velocity > 0;
}

/// <summary>Every physical input (or one device, <c>Port</c>): what enters the graph.</summary>
public sealed class PhysicalInputNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (Settings.Port is null || Settings.Port == e.Port) {
            emit.Emit(0, e);
        }
    }
}

/// <summary>The sequencer input: the record-enabled or selected track (<see cref="IEnvironmentSink.SequencerInput" />).</summary>
public sealed class SequencerInputNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        emit.Sink.SequencerInput(e);
}

/// <summary>
///     An on-screen keyboard: live MIDI into it (<see cref="EnvironmentGraph.InputAt" />) leaves by its outlet, on its
///     <c>channel</c>, its note-ons at its <c>velocity</c>.
/// </summary>
public sealed class KeyboardNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    readonly int channel = (int)settings.Parameters.GetValueOrDefault("channel", 1);
    readonly byte velocity = (byte)Math.Clamp(settings.Parameters.GetValueOrDefault("velocity", 100), 1, 127);

    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (e.Status >= 0xF0) {
            return;
        }

        var status = OnChannel(e.Status, channel);
        emit.Emit(0, e with { Status = status, Data2 = IsNoteOn(e.Status, e.Data2) ? velocity : e.Data2 });
    }
}

/// <summary>Passes everything through and keeps the last events for its telemetry slot.</summary>
public sealed class MonitorNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    // The ring the telemetry copies (written by the audio thread, read by the telemetry thread: each event is written
    // before the count that shows it).
    readonly long[] events = new long[2 * EnvironmentMonitorEntry.Capacity];
    long count;

    /// <summary>Events it has passed since it was compiled.</summary>
    public long Count => Volatile.Read(ref count);

    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        ref readonly var transport = ref emit.Transport;
        var sample = transport.Playing ? transport.Sample + (e.Sample - emit.BlockStart) : -1;
        var at = (int)(count % EnvironmentMonitorEntry.Capacity);
        Volatile.Write(ref events[2 * at], sample);
        Volatile.Write(ref events[2 * at + 1], e.Status | (e.Data1 << 8) | (e.Data2 << 16));
        Volatile.Write(ref count, count + 1);
        emit.Emit(0, e);
    }

    /// <summary>Its slot's entry: the object, the count and the ring as they are.</summary>
    internal unsafe void Publish(ref EnvironmentMonitorEntry entry) {
        entry.Object = Settings.Index + 1;
        entry.Count = Volatile.Read(ref count);
        for (var i = 0; i < events.Length; i++) {
            entry.Events[i] = Volatile.Read(ref events[i]);
        }
    }
}

/// <summary>
///     A MIDI value moved by its event kind and sent when moved; its value goes to its telemetry slot. An event of its
///     kind (<c>message</c>: 0 control change number <c>number</c>, 1 note <c>number</c> (its velocity), 2 program
///     change, 3 pitch bend (its high byte); on its <c>channel</c>) at its inlet moves it, any channel message played
///     into it on screen does (a control change of the value); it then sends that value scaled to <c>min</c>…<c>max</c>
///     as its own message. Other events pass through.
/// </summary>
public sealed class FaderNode : EnvironmentNode {
    readonly int message;
    readonly int channel;
    readonly byte number;
    readonly double min;
    readonly double max;
    float value;

    public FaderNode(EngineEnvironmentObject settings) : base(settings) {
        message = (int)Math.Clamp(Parameter("message", 0), 0, 3);
        channel = (int)Math.Clamp(Parameter("channel", 1), 1, 16);
        number = (byte)Math.Clamp(Parameter("number", 7), 0, 127);
        min = Math.Clamp(Parameter("min", 0), 0, 127);
        max = Math.Clamp(Parameter("max", 127), 0, 127);
        value = (float)Math.Clamp(Parameter("value", 0), 0, 127);
    }

    /// <summary>What it last sent (0…127).</summary>
    public float Value => Volatile.Read(ref value);

    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (e.Status >= 0xF0) {
            return;
        }

        int position;
        if (inlet < 0) {
            position = (e.Status & 0xF0) is 0xC0 or 0xD0 ? e.Data1 : e.Data2;
        } else if (!Moves(e, out position)) {
            emit.Emit(0, e);
            return;
        }

        var scaled = (byte)Math.Clamp((int)Math.Round(min + position * (max - min) / 127.0), 0, 127);
        Volatile.Write(ref value, scaled);
        var status = (byte)(message switch {
                0 => 0xB0,
                1 => 0x90,
                2 => 0xC0,
                _ => 0xE0
            }
            | (channel - 1));
        var sent = message switch {
            0 or 1 => e with { Status = status, Data1 = number, Data2 = scaled },
            2 => e with { Status = status, Data1 = scaled, Data2 = 0 },
            _ => e with { Status = status, Data1 = 0, Data2 = scaled }
        };
        emit.Emit(0, sent);
    }

    /// <summary>Whether <paramref name="e" /> is of the fader's kind, and its position.</summary>
    bool Moves(in EnvironmentEvent e, out int position) {
        position = 0;
        if ((e.Status & 0x0F) + 1 != channel) {
            return false;
        }

        switch (e.Status & 0xF0) {
            case 0xB0 when message == 0 && e.Data1 == number:
                position = e.Data2;
                return true;
            case 0x90 when message == 1 && e.Data1 == number:
                position = e.Data2;
                return true;
            case 0xC0 when message == 2:
                position = e.Data1;
                return true;
            case 0xE0 when message == 3:
                position = e.Data2;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Conditions and operations (Phase 7's Transform rules): matched events out of 0, the rest out of 1.</summary>
/// <remarks>
///     The fields as Phase 7's MIDI Transform reads an event: <c>status</c> the status byte's high nibble as a number
///     (144 a note, its note-offs included; 176 a control change …), <c>channel</c> 1…16, <c>pitch</c> data 1 (0 for
///     program change, channel pressure and pitch bend) and <c>velocity</c> data 2 (a program change's or channel
///     pressure's value, a pitch bend's −8192…8191). Operations apply in field order (channel, pitch, velocity),
///     clamped to each field's range; a note-on keeps a velocity of at least 1. Random operations draw from a generator
///     seeded by the object's index; the time-based and map operations leave the field as it is.
/// </remarks>
public sealed class TransformerNode : EnvironmentNode {
    readonly Rule[] conditions;
    readonly Rule[] operations;
    uint random;

    public TransformerNode(EngineEnvironmentObject settings) : base(settings) {
        conditions = [..(settings.Conditions ?? []).Select(Rule.Of).Where(static r => r.Field != Field.None)];
        operations = [
            ..(settings.Operations ?? []).Select(Rule.Of)
                .Where(static r => r.Field is Field.Channel or Field.Pitch or Field.Velocity)
                .OrderBy(static r => r.Field)
        ];
        random = Seed();
    }

    uint Seed() => (uint)(0x9E3779B9 ^ (Settings.Index * 0x85EBCA6B)) | 1;

    public override void Reset() => random = Seed();

    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (e.Status >= 0xF0) {
            emit.Emit(1, e);
            return;
        }

        var fields = Fields.Of(e);
        foreach (var c in conditions) {
            if (!c.Matches(fields.Get(c.Field))) {
                emit.Emit(1, e);
                return;
            }
        }

        foreach (var o in operations) {
            fields.Set(o.Field, o.Apply(fields.Get(o.Field), Range(fields, o.Field), ref random));
        }

        emit.Emit(0, fields.To(e));
    }

    static (int Min, int Max) Range(in Fields f, Field field) =>
        field switch {
            Field.Channel => (1, 16),
            Field.Pitch => (0, 127),
            _ when f.Kind == 0xE0 => (-8192, 8191),
            _ when f.Kind == 0x90 && f.On => (1, 127),
            _ => (0, 127)
        };

    internal enum Field {
        None,
        Status,
        Channel,
        Pitch,
        Velocity
    }

    enum Op {
        All,
        Equal,
        Unequal,
        LessEqual,
        GreaterEqual,
        Inside,
        Outside,
        Thru,
        Fix,
        Add,
        Subtract,
        Min,
        Max,
        Flip,
        Multiply,
        Divide,
        Scale,
        Range,
        Random,
        PlusMinusRandom,
        Quantize,
        Exponent
    }

    readonly record struct Rule(Field Field, Op Op, double A, double B) {
        public static Rule Of(EngineTransformRule rule) =>
            new(
                rule.Field switch {
                    "status" => Field.Status,
                    "channel" => Field.Channel,
                    "pitch" => Field.Pitch,
                    "velocity" => Field.Velocity,
                    _ => Field.None
                },
                rule.Op switch {
                    "all" => Op.All,
                    "equal" => Op.Equal,
                    "unequal" => Op.Unequal,
                    "lessEqual" => Op.LessEqual,
                    "greaterEqual" => Op.GreaterEqual,
                    "inside" => Op.Inside,
                    "outside" => Op.Outside,
                    "fix" => Op.Fix,
                    "add" => Op.Add,
                    "subtract" => Op.Subtract,
                    "min" => Op.Min,
                    "max" => Op.Max,
                    "flip" => Op.Flip,
                    "multiply" => Op.Multiply,
                    "divide" => Op.Divide,
                    "scale" => Op.Scale,
                    "range" => Op.Range,
                    "random" => Op.Random,
                    "plusMinusRandom" => Op.PlusMinusRandom,
                    "quantize" => Op.Quantize,
                    "exponent" => Op.Exponent,
                    _ => Op.Thru // thru, and the time-based and map operations the Environment does not run
                },
                rule.A,
                rule.B
            );

        public bool Matches(int x) =>
            Op switch {
                Op.Equal => x == A,
                Op.Unequal => x != A,
                Op.LessEqual => x <= A,
                Op.GreaterEqual => x >= A,
                Op.Inside => x >= A && x <= B,
                Op.Outside => x < A || x > B,
                _ => true
            };

        public int Apply(int x, (int Min, int Max) range, ref uint state) {
            var y = Op switch {
                Op.Fix => A,
                Op.Add => x + A,
                Op.Subtract => x - A,
                Op.Min => Math.Max(x, A),
                Op.Max => Math.Min(x, A),
                Op.Flip => 2 * A - x,
                Op.Multiply => x * A,
                Op.Divide => A == 0 ? x : x / A,
                Op.Scale => x * A + B,
                Op.Range => Math.Clamp(x, Math.Min(A, B), Math.Max(A, B)),
                Op.Random => Math.Min(A, B) + Next(ref state) * Math.Abs(B - A),
                Op.PlusMinusRandom => x + (2 * Next(ref state) - 1) * A,
                Op.Quantize => A > 0 ? Math.Round(x / A) * A : x,
                Op.Exponent => Curve(x, range, A),
                _ => x
            };
            return (int)Math.Clamp(Math.Round(y), range.Min, range.Max);
        }

        static double Curve(int x, (int Min, int Max) range, double exponent) {
            var span = range.Max - range.Min;
            if (span <= 0 || exponent <= 0) {
                return x;
            }

            return range.Min + Math.Pow((x - range.Min) / (double)span, exponent) * span;
        }

        /// <summary>A uniform value in [0, 1) (xorshift32).</summary>
        static double Next(ref uint state) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state / 4294967296.0;
        }
    }

    /// <summary>An event's fields as the Transform rules read them, and back.</summary>
    record struct Fields(int Kind, bool On, int Channel, int Pitch, int Velocity) {
        public static Fields Of(in EnvironmentEvent e) {
            var kind = e.Status & 0xF0;
            var channel = (e.Status & 0x0F) + 1;
            return kind switch {
                0x80 => new(0x90, false, channel, e.Data1, e.Data2),
                0x90 => new(0x90, e.Data2 > 0, channel, e.Data1, e.Data2),
                0xC0 or 0xD0 => new(kind, false, channel, 0, e.Data1),
                0xE0 => new(kind, false, channel, 0, ((e.Data2 << 7) | e.Data1) - 8192),
                _ => new(kind, false, channel, e.Data1, e.Data2)
            };
        }

        public readonly int Get(Field field) =>
            field switch {
                Field.Status => Kind,
                Field.Channel => Channel,
                Field.Pitch => Pitch,
                _ => Velocity
            };

        public void Set(Field field, int value) {
            switch (field) {
                case Field.Channel:
                    Channel = value;
                    break;
                case Field.Pitch:
                    Pitch = value;
                    break;
                case Field.Velocity:
                    Velocity = value;
                    break;
                default:
                    break; // the status is a condition only
            }
        }

        /// <summary><paramref name="e" /> with these fields (a note-off stays a note-off).</summary>
        public readonly EnvironmentEvent To(in EnvironmentEvent e) {
            var status = (byte)((e.Status & 0xF0) | (Channel - 1));
            return Kind switch {
                0xC0 or 0xD0 => e with { Status = status, Data1 = (byte)Velocity },
                0xE0 => e with {
                    Status = status, Data1 = (byte)((Velocity + 8192) & 0x7F), Data2 = (byte)((Velocity + 8192) >> 7)
                },
                _ => e with { Status = status, Data1 = (byte)Pitch, Data2 = (byte)Velocity }
            };
        }
    }
}

/// <summary>
///     Repeats each event at its taps, scheduled in the graph's queue: each tap's delay (ticks at 960 PPQ, converted by
///     the tempo at the event), a note's pitch transposed and a note-on's velocity offset (notes out of range are
///     dropped). The event itself passes at once when <c>dry</c> is 1.
/// </summary>
public sealed class DelayLineNode : EnvironmentNode {
    const double TicksPerQuarter = 960;
    readonly EngineDelayTap[] taps;
    readonly bool dry;

    public DelayLineNode(EngineEnvironmentObject settings) : base(settings) {
        taps = [..(settings.Taps ?? []).Take(16)];
        dry = Parameter("dry", 1) >= 0.5;
    }

    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (dry) {
            emit.Emit(0, e);
        }

        ref readonly var transport = ref emit.Transport;
        var samplesPerTick = 60 * transport.SampleRate / (Math.Max(1e-3, transport.Bpm) * TicksPerQuarter);
        var note = (e.Status & 0xF0) is 0x80 or 0x90 or 0xA0;
        foreach (var tap in taps) {
            var delayed = e with {
                Sample = e.Sample + (long)Math.Round(Math.Clamp(tap.DelayTicks, 1, 3840) * samplesPerTick)
            };
            if (note) {
                var pitch = e.Data1 + tap.Transpose;
                if (pitch is < 0 or > 127) {
                    continue;
                }

                var velocity = IsNoteOn(e.Status, e.Data2) ? Math.Clamp(e.Data2 + tap.Velocity, 1, 127) : e.Data2;
                delayed = delayed with { Data1 = (byte)pitch, Data2 = (byte)velocity };
            }

            emit.Schedule(0, delayed);
        }
    }
}

/// <summary>
///     A Phase 6 MIDI FX engine on the input path: the block's events at its inlet go through the effect at the
///     block's end (<see cref="Advance" />), with the block's transport; what it plays leaves by its outlet at its frame
///     (what it holds past the block it plays in a later one), marked as <see cref="EnvironmentEvent.Generated" /> when
///     the effect times its output itself (the arpeggiator's grid).
/// </summary>
public abstract class MidiEffectNode(EngineEnvironmentObject settings, string effectId, bool timed)
    : EnvironmentNode(settings) {
    readonly MidiEventBuffer output = new();
    MidiEvent[] input = [];
    int count;

    /// <summary>The effect (null until prepared, or when it could not be made).</summary>
    protected IMidiEffect? Effect { get; private set; }

    public override void Prepare(float sampleRate, int maxFrames) {
        var effect = BuiltinMidiEffects.Create(effectId)
            ?? throw new InvalidOperationException($"There is no MIDI effect '{effectId}'.");
        effect.Prepare(sampleRate, maxFrames);
        foreach (var p in effect.Descriptor.Parameters) {
            if (Settings.Parameters.TryGetValue(p.Id, out var value) && double.IsFinite(value)) {
                effect.SetParameter(p.Index, (float)value);
            }
        }

        Configure(effect);
        input = new MidiEvent[MidiEventBuffer.Capacity];
        Effect = effect;
    }

    /// <summary>On the control thread, once the parameters are set: anything else the effect needs.</summary>
    protected virtual void Configure(IMidiEffect effect) { }

    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (count < input.Length && e.Status < 0xF0) {
            input[count++] = new(
                (int)Math.Clamp(e.Sample - emit.BlockStart, 0, emit.Frames - 1),
                e.Status,
                e.Data1,
                e.Data2
            );
        }
    }

    public override void Advance(long blockStart, int frames, IEnvironmentEmitter emit) {
        if (Effect is not { } effect) {
            count = 0;
            return;
        }

        output.Clear();
        effect.Process(emit.Transport with { Frames = frames }, new(input, 0, count), output);
        count = 0;
        output.Sort();
        foreach (var e in output.Events) {
            var sample = blockStart + Math.Clamp(e.Frame, 0, frames - 1);
            emit.Emit(0, new(sample, e.Status, e.Data1, e.Data2, Generated: timed));
        }
    }

    public override void Reset() {
        count = 0;
        Effect?.Reset();
    }
}

/// <summary>Phase 6's Arpeggiator engine on the input path (its parameters <c>anode.arpeggiator</c>'s).</summary>
public sealed class ArpeggiatorNode(EngineEnvironmentObject settings)
    : MidiEffectNode(settings, BuiltinMidiEffects.ArpeggiatorId, true);

/// <summary>
///     Phase 6's Chord Trigger engine on the input path (its parameters <c>anode.chord-trigger</c>'s); a
///     <c>Chord</c> (semitones from the key) is its learned single chord, played by every key.
/// </summary>
public sealed class ChordMemorizerNode(EngineEnvironmentObject settings)
    : MidiEffectNode(settings, ChordTriggerId, false) {
    const string ChordTriggerId = "anode.chord-trigger";

    /// <summary>The Chord Trigger's <c>chord</c> value that plays its learned single chord.</summary>
    const int Learned = 11;

    /// <summary>The most notes in a chord (the Chord Trigger's).</summary>
    const int ChordSize = 12;

    protected override void Configure(IMidiEffect effect) {
        if (Settings.Chord is not { Count: > 0 } chord) {
            return;
        }

        // The learned single chord is the effect's state after its parameters ("CHRD", the single chord's intervals,
        // then the multi chords): this one, with no multi chords.
        var state = effect.SaveState() ?? [];
        var at = state.AsSpan().LastIndexOf("CHRD"u8);
        var parameters = at < 0 ? state : state[..at];
        var intervals = chord.Take(ChordSize).Select(static i => (byte)(sbyte)Math.Clamp(i, -127, 127)).ToArray();
        effect.LoadState([..parameters, .."CHRD"u8, (byte)intervals.Length, ..intervals, 0]);
        foreach (var p in effect.Descriptor.Parameters) {
            if (p.Id == "chord") {
                effect.SetParameter(p.Index, Learned);
            } else if (p.Id == "mode") {
                effect.SetParameter(p.Index, 0);
            }
        }
    }
}

/// <summary>Channel n out of outlet n − 1, everything else out of outlet 16.</summary>
public sealed class ChannelSplitterNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        emit.Emit(e.Status < 0xF0 ? e.Status & 0x0F : 16, e);
}

/// <summary>To an external MIDI output (<see cref="IEnvironmentSink.MidiPort" />).</summary>
public sealed class MidiPortNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (Settings.Port is { Length: > 0 } port) {
            emit.Sink.MidiPort(port, e);
        }
    }
}

/// <summary>To a track's input (<see cref="IEnvironmentSink.TrackInput" />).</summary>
public sealed class TrackInputNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (Settings.TrackId >= 0) {
            emit.Sink.TrackInput(Settings.TrackId, e);
        }
    }
}

/// <summary>
///     To a strip parameter (<see cref="IEnvironmentSink.Control" />): a control change's (or a fader's) value 0…127
///     over the parameter's range, a note-on's velocity, a program change's number, a pitch bend's 14 bits.
/// </summary>
public sealed class ControlTargetNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) {
        if (Settings.Control is not { } target) {
            return;
        }

        float value;
        switch (e.Status & 0xF0) {
            case 0xB0:
            case 0xA0:
                value = e.Data2 / 127f;
                break;
            case 0x90 when e.Data2 > 0:
                value = e.Data2 / 127f;
                break;
            case 0xC0:
            case 0xD0:
                value = e.Data1 / 127f;
                break;
            case 0xE0:
                value = ((e.Data2 << 7) | e.Data1) / 16383f;
                break;
            default:
                return; // note-offs and system messages move nothing
        }

        emit.Sink.Control(target, value, e.Sample);
    }
}
