using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Tests.Support;

/// <summary>A MIDI effect's output event at an absolute frame of a run.</summary>
public readonly record struct OutputEvent(long Frame, byte Status, byte Data1, byte Data2) {
    public bool IsNoteOn => (Status & 0xF0) == 0x90 && Data2 > 0;
    public bool IsNoteOff => (Status & 0xF0) == 0x80 || ((Status & 0xF0) == 0x90 && Data2 == 0);
}

/// <summary>Runs MIDI effects block by block, as the engine does, with transport contexts it computes.</summary>
public static class MidiRun {
    public const double Rate = 48_000;

    /// <summary>A new, prepared instance of <paramref name="id" /> with <paramref name="parameters" /> set.</summary>
    public static IMidiEffect Create(string id, params (int Index, float Value)[] parameters) {
        var effect = BuiltinMidiEffects.Create(id) ?? throw new InvalidOperationException(id);
        effect.Prepare((float)Rate, 1_024);
        foreach (var (index, value) in parameters) {
            effect.SetParameter(index, value);
        }

        return effect;
    }

    /// <summary>
    ///     The contexts of consecutive <paramref name="block" />-frame blocks from quarter note
    ///     <paramref name="startQuarter" />, block <c>b</c> at <paramref name="bpm" />(b) (constant within a block, as
    ///     the engine's contexts are): the quarter-note position advances by each block's frames at its tempo.
    /// </summary>
    public static List<TransportContext> Contexts(
        int frames,
        Func<int, double> bpm,
        bool playing = true,
        int block = 64,
        double startQuarter = 0
    ) {
        var contexts = new List<TransportContext>();
        var quarter = startQuarter;
        for (int at = 0, b = 0; at < frames; at += block, b++) {
            var count = Math.Min(block, frames - at);
            var context = new TransportContext(playing, at, quarter, bpm(b), 4, 4, 0, Rate, count);
            contexts.Add(context);
            quarter += count * context.QuartersPerSample;
        }

        return contexts;
    }

    /// <summary>
    ///     Runs <paramref name="effects" /> in order over <paramref name="contexts" /> (whose frames add up), each event
    ///     in the block it falls in; each effect's output is sorted (stably) before the next, as the engine does.
    /// </summary>
    public static List<OutputEvent> Run(
        IReadOnlyList<IMidiEffect> effects,
        IReadOnlyList<TransportContext> contexts,
        IReadOnlyList<TimedEvent> events
    ) {
        var sorted = events.OrderBy(static e => e.Frame).ToArray();
        var result = new List<OutputEvent>();
        var buffers = effects.Select(static _ => new MidiEventBuffer()).ToArray();
        var next = 0;
        long at = 0;
        foreach (var context in contexts) {
            var block = new List<MidiEvent>();
            while (next < sorted.Length && sorted[next].Frame < at + context.Frames) {
                var e = sorted[next++];
                block.Add(new((int)(e.Frame - at), e.Status, e.Data1, e.Data2));
            }

            ReadOnlySpan<MidiEvent> input = block.ToArray();
            for (var i = 0; i < effects.Count; i++) {
                buffers[i].Clear();
                effects[i].Process(context, input, buffers[i]);
                buffers[i].Sort();
                input = buffers[i].Events;
            }

            foreach (var e in input) {
                Assert.InRange(e.Frame, 0, context.Frames - 1);
                result.Add(new(at + e.Frame, e.Status, e.Data1, e.Data2));
            }

            at += context.Frames;
        }

        return result;
    }

    /// <summary>Runs one effect over <paramref name="frames" /> frames at a constant tempo.</summary>
    public static List<OutputEvent> Run(
        IMidiEffect effect,
        int frames,
        IReadOnlyList<TimedEvent> events,
        double bpm = 120,
        bool playing = true,
        int block = 64
    ) =>
        Run([effect], Contexts(frames, _ => bpm, playing, block), events);
}
