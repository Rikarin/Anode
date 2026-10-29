using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.Transport;
using Anode.Engine.Plugins;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     MIDI effects for the engine's MIDI FX tests (workstream E's built-ins are not on this branch): a transposer, a
///     note filter (drops notes above a limit, so the order of a chain shows), a doubler (an octave above each note) and
///     a probe that records the transport contexts it was given. Allocation-free while processing.
/// </summary>
static class TestMidiEffects {
    public const string Transposer = "test.transposer";
    public const string Filter = "test.note-filter";
    public const string Doubler = "test.doubler";
    public const string Probe = "test.midi-probe";
    public const string Script = "test.script";

    /// <summary>
    ///     A registry with the test instrument, the test effects, these MIDI effects (the script effect's compiles wait
    ///     for <paramref name="compileGate" /> when one is given) and the probes.
    /// </summary>
    public static PluginRegistry Registry(List<IMidiEffect>? created = null, ScriptGate? compileGate = null) {
        var registry = TestEffects.Registry();
        registry.AddBuiltinMidiEffect(TransposerEffect.Info, () => Track(created, new TransposerEffect()));
        registry.AddBuiltinMidiEffect(FilterEffect.Info, () => Track(created, new FilterEffect()));
        registry.AddBuiltinMidiEffect(DoublerEffect.Info, () => Track(created, new DoublerEffect()));
        registry.AddBuiltinMidiEffect(ProbeMidiEffect.Info, () => Track(created, new ProbeMidiEffect()));
        registry.AddBuiltinMidiEffect(ScriptEffect.Info, () => Track(created, new ScriptEffect(compileGate)));
        registry.AddBuiltinEffect(ProbeAudioEffect.Info, static () => new ProbeAudioEffect());
        registry.AddBuiltin(ProbeInstrument.Info, static () => new ProbeInstrument());
        return registry;
    }

    static IMidiEffect Track(List<IMidiEffect>? created, IMidiEffect effect) {
        if (created is not null) {
            lock (created) {
                created.Add(effect);
            }
        }

        return effect;
    }

    public static EngineInsert Slot(
        string id,
        string plugin,
        IReadOnlyDictionary<string, double>? parameters = null,
        bool bypassed = false
    ) =>
        new(id, plugin, bypassed, parameters ?? new Dictionary<string, double>());

    public static bool IsNote(in MidiEvent e) => (e.Status & 0xF0) is 0x80 or 0x90 or 0xA0;
}

/// <summary>The common parts: parameters by index, no state.</summary>
abstract class TestMidiEffect(MidiEffectDescriptor descriptor) : IMidiEffect {
    readonly float[] values = [..descriptor.Parameters.Select(static p => p.Default)];

    public MidiEffectDescriptor Descriptor { get; } = descriptor;

    public int Resets { get; private set; }

    public bool Disposed { get; private set; }

    public void Prepare(float sampleRate, int maxFrames) { }

    public void SetParameter(int index, float value) => values[index] = value;

    public float GetParameter(int index) => values[index];

    public abstract void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output);

    public void Reset() => Resets++;

    public byte[]? SaveState() => null;

    public bool LoadState(ReadOnlySpan<byte> state) => true;

    public void Dispose() => Disposed = true;

    protected static MidiEffectDescriptor Describe(string id, params InstrumentParameter[] parameters) =>
        new(id, id, "Anode tests", "1.0", parameters);
}

/// <summary>Moves every note by <c>semitones</c> (−24…24), dropping those it moves out of range.</summary>
sealed class TransposerEffect() : TestMidiEffect(Info) {
    public static readonly MidiEffectDescriptor Info =
        Describe(TestMidiEffects.Transposer, new InstrumentParameter(0, "semitones", "Semitones", -24, 24, 0, "st"));

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var shift = (int)Math.Round(GetParameter(0));
        foreach (var e in input) {
            if (!TestMidiEffects.IsNote(e)) {
                output.Add(e);
                continue;
            }

            var note = e.Data1 + shift;
            if (note is >= 0 and <= 127) {
                output.Add(e with { Data1 = (byte)note });
            }
        }
    }
}

/// <summary>Drops notes above <c>limit</c>.</summary>
sealed class FilterEffect() : TestMidiEffect(Info) {
    public static readonly MidiEffectDescriptor Info =
        Describe(TestMidiEffects.Filter, new InstrumentParameter(0, "limit", "Limit", 0, 127, 127, string.Empty));

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        var limit = GetParameter(0);
        foreach (var e in input) {
            if (!TestMidiEffects.IsNote(e) || e.Data1 <= limit) {
                output.Add(e);
            }
        }
    }
}

/// <summary>Adds the note an octave above to every note (written after the originals: the engine sorts).</summary>
sealed class DoublerEffect() : TestMidiEffect(Info) {
    public static readonly MidiEffectDescriptor Info = Describe(TestMidiEffects.Doubler);

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        foreach (var e in input) {
            output.Add(e);
        }

        foreach (var e in input) {
            if (TestMidiEffects.IsNote(e) && e.Data1 + 12 <= 127) {
                output.Add(e with { Data1 = (byte)(e.Data1 + 12) });
            }
        }
    }
}

/// <summary>Passes MIDI through and records the transport context of each block (preallocated).</summary>
sealed class ProbeMidiEffect() : TestMidiEffect(Info) {
    public static readonly MidiEffectDescriptor Info = Describe(TestMidiEffects.Probe);

    public TransportContext[] Contexts { get; } = new TransportContext[8192];

    public int Count { get; private set; }

    public override void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        if (Count < Contexts.Length) {
            Contexts[Count++] = transport;
        }

        foreach (var e in input) {
            output.Add(e);
        }
    }
}

/// <summary>
///     An insert that passes audio and records each <c>SetTransport</c> and the frames of the <c>Process</c> after
///     it.
/// </summary>
sealed class ProbeAudioEffect() : TestEffect(Info), IAudioEffect {
    public const string Id = "test.transport-probe";

    public static readonly EffectDescriptor Info = new(
        Id,
        "Transport probe",
        "Anode tests",
        "1.0",
        "Utility",
        [new(0, "x", "x", 0, 1, 0, string.Empty)],
        false
    );

    public (TransportContext Transport, int Processed)[] Calls { get; } = new (TransportContext, int)[8192];

    public int Count { get; private set; }

    public void SetTransport(in TransportContext transport) {
        if (Count < Calls.Length) {
            Calls[Count++] = (transport, -1);
        }
    }

    public override void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        if (Count > 0 && Calls[Count - 1].Processed < 0) {
            Calls[Count - 1].Processed = left.Length;
        }
    }
}

/// <summary>An instrument (silent) that records the transport contexts it is given.</summary>
sealed class ProbeInstrument : IInstrument {
    public const string Id = "test.instrument-probe";

    public static readonly InstrumentDescriptor Info = new(Id, "Probe", "Anode tests", "1.0", [], []);

    public TransportContext[] Contexts { get; } = new TransportContext[8192];

    public int Count { get; private set; }

    public InstrumentDescriptor Descriptor => Info;

    public void Prepare(float sampleRate, int maxFrames) { }

    public void SetParameter(int index, float value) { }

    public float GetParameter(int index) => 0;

    public void SetTransport(in TransportContext transport) {
        if (Count < Contexts.Length) {
            Contexts[Count++] = transport;
        }
    }

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) {
        left.Clear();
        right.Clear();
    }

    public void Reset() { }

    public bool LoadFile(string path) => false;

    public byte[]? SaveState() => null;

    public bool LoadState(ReadOnlySpan<byte> state) => true;

    public void Dispose() { }
}
