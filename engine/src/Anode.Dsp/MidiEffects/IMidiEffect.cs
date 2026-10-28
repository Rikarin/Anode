using Anode.Dsp.Instruments;
using Anode.Dsp.Transport;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     What a MIDI effect is: its stable id (<c>anode.arpeggiator</c>), names and parameters (macro hints included). Its
///     catalogue category is <c>MIDI Effect</c> (<see cref="Effects.EffectCategories.MidiEffect" />).
/// </summary>
public sealed record MidiEffectDescriptor(
    string Id,
    string Name,
    string Vendor,
    string Version,
    IReadOnlyList<InstrumentParameter> Parameters
);

/// <summary>
///     A MIDI effect in a track's MIDI FX slots (docs/07-phase-6.md): MIDI in, MIDI out, before the instrument. The
///     engine runs a track's slots in order on the block's MIDI (regions, live and step input); a bypassed slot passes
///     it through.
///     <para>
///         Threading as <see cref="IInstrument" />: <see cref="Prepare" />, <see cref="LoadState" />,
///         <see cref="SaveState" /> and disposal on the engine's control thread while the instance is not processing;
///         <see cref="SetParameter" />, <see cref="Process" /> and <see cref="Reset" /> on the audio thread, with no
///         allocation, lock or blocking.
///     </para>
/// </summary>
public interface IMidiEffect : IDisposable {
    MidiEffectDescriptor Descriptor { get; }

    /// <summary>Allocates everything processing needs, for blocks of up to <paramref name="maxFrames" />.</summary>
    void Prepare(float sampleRate, int maxFrames);

    void SetParameter(int index, float value);

    float GetParameter(int index);

    /// <summary>
    ///     Processes one block of <see cref="TransportContext.Frames" /> frames: reads <paramref name="input" />
    ///     (sorted by frame, frames inside the block) and adds what it plays to <paramref name="output" />, which the
    ///     engine has cleared. Events it adds need not be sorted (the engine sorts the output, stably, before the next
    ///     slot); events past the block are the effect's to hold until a later block. A full output drops further
    ///     events (<see cref="MidiEventBuffer.Add" /> returns false).
    /// </summary>
    void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output);

    /// <summary>Forgets held and pending notes at once (stop, locate); the engine silences the instrument itself.</summary>
    void Reset();

    /// <summary>State beyond the parameters (null when there is none), opaque to the engine.</summary>
    byte[]? SaveState();

    bool LoadState(ReadOnlySpan<byte> state);
}

/// <summary>
///     A block's MIDI events, allocation-free: a fixed array of <see cref="Capacity" /> events, reused block after block.
/// </summary>
public sealed class MidiEventBuffer {
    /// <summary>The most events a block carries between MIDI effects.</summary>
    public const int Capacity = 1_024;

    readonly MidiEvent[] events = new MidiEvent[Capacity];

    public int Count { get; private set; }

    public ReadOnlySpan<MidiEvent> Events => events.AsSpan(0, Count);

    /// <summary>Appends <paramref name="e" />; false (and nothing added) when the buffer is full.</summary>
    public bool Add(in MidiEvent e) {
        if (Count == Capacity) {
            return false;
        }

        events[Count++] = e;
        return true;
    }

    public void Clear() => Count = 0;

    /// <summary>Sorts the events by frame, keeping the order of events at the same frame (an insertion sort: no allocation).</summary>
    public void Sort() {
        for (var i = 1; i < Count; i++) {
            var e = events[i];
            var j = i - 1;
            while (j >= 0 && events[j].Frame > e.Frame) {
                events[j + 1] = events[j];
                j--;
            }

            events[j + 1] = e;
        }
    }
}
