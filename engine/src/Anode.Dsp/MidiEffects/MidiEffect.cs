using Anode.Dsp.Instruments;
using Anode.Dsp.Transport;
using System.Runtime.CompilerServices;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     What the built-in MIDI effects share: the descriptor, the parameters (clamped and snapped like an instrument's)
///     and their state blob (the instruments' <c>ANIS</c> format, by parameter id).
/// </summary>
internal abstract class MidiEffect : IMidiEffect {
    public const string Vendor = "Anode";
    public const string Version = "1.0.0";

    protected MidiEffect(MidiEffectDescriptor descriptor) {
        Descriptor = descriptor;
        Parameters = new(descriptor.Parameters);
    }

    public MidiEffectDescriptor Descriptor { get; }

    protected ParameterBank Parameters { get; }

    public double SampleRate { get; private set; } = 48_000;

    public bool Prepared { get; private set; }

    public virtual void Prepare(float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        SampleRate = sampleRate;
        Prepared = true;
        Reset();
    }

    public void SetParameter(int index, float value) => Parameters.Set(index, value);

    public float GetParameter(int index) => Parameters.Get(index);

    public abstract void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output);

    public abstract void Reset();

    public virtual byte[]? SaveState() => InstrumentState.Write(Descriptor.Id, null, Descriptor.Parameters, Parameters);

    public virtual bool LoadState(ReadOnlySpan<byte> state) {
        if (!InstrumentState.TryRead(state, Descriptor.Id, out _, out var values)) {
            return false;
        }

        InstrumentState.Apply(values, Parameters);
        return true;
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }

    /// <summary>A note-on with a velocity above 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static bool IsNoteOn(in MidiEvent e) => (e.Status & 0xF0) == 0x90 && e.Data2 > 0;

    /// <summary>A note-off, or a note-on with velocity 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static bool IsNoteOff(in MidiEvent e) =>
        (e.Status & 0xF0) == 0x80 || ((e.Status & 0xF0) == 0x90 && e.Data2 == 0);

    /// <summary>The key of a note on channel <paramref name="channel" /> in per-channel tables (16 × 128).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static int Slot(int channel, int note) => ((channel & 0x0F) << 7) | (note & 0x7F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static MidiEvent NoteOn(int frame, int channel, int note, int velocity) =>
        new(frame, (byte)(0x90 | (channel & 0x0F)), (byte)note, (byte)Math.Clamp(velocity, 1, 127));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static MidiEvent NoteOff(int frame, int channel, int note) =>
        new(frame, (byte)(0x80 | (channel & 0x0F)), (byte)note, 0);
}

/// <summary>Note lengths for tempo-synced rates, with their lengths in quarter notes.</summary>
internal static class NoteDivisions {
    /// <summary>The names, as a rate parameter's values (".": dotted, "T": triplet).</summary>
    public static readonly string[] Names = [
        "1/1", "1/2", "1/2.", "1/2T", "1/4", "1/4.", "1/4T", "1/8", "1/8.", "1/8T", "1/16", "1/16.", "1/16T", "1/32",
        "1/32T"
    ];

    /// <summary>Each name's length in quarter notes.</summary>
    public static readonly double[] Quarters = [
        4, 2, 3, 4 / 3.0, 1, 1.5, 2 / 3.0, 0.5, 0.75, 1 / 3.0, 0.25, 0.375, 1 / 6.0, 0.125, 1 / 12.0
    ];

    /// <summary>The index of 1/16.</summary>
    public const int Sixteenth = 10;

    /// <summary>The length of rate value <paramref name="index" />.</summary>
    public static double Length(float index) => Quarters[Math.Clamp((int)index, 0, Quarters.Length - 1)];
}

/// <summary>Musical scales for the scale-aware MIDI effects.</summary>
internal static class Scales {
    public static readonly string[] Names = [
        "Chromatic", "Major", "Natural Minor", "Harmonic Minor", "Melodic Minor", "Dorian", "Phrygian", "Lydian",
        "Mixolydian", "Locrian", "Major Pentatonic", "Minor Pentatonic", "Blues"
    ];

    public static readonly string[] Keys = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"];

    /// <summary>Each scale's steps from its tonic.</summary>
    static readonly int[][] Steps = [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], [0, 2, 4, 5, 7, 9, 11], [0, 2, 3, 5, 7, 8, 10], [0, 2, 3, 5, 7, 8, 11],
        [0, 2, 3, 5, 7, 9, 11], [0, 2, 3, 5, 7, 9, 10], [0, 1, 3, 5, 7, 8, 10], [0, 2, 4, 6, 7, 9, 11],
        [0, 2, 4, 5, 7, 9, 10], [0, 1, 3, 5, 6, 8, 10], [0, 2, 4, 7, 9], [0, 3, 5, 7, 10], [0, 3, 5, 6, 7, 10]
    ];

    public static ReadOnlySpan<int> Of(int scale) => Steps[Math.Clamp(scale, 0, Steps.Length - 1)];

    /// <summary>
    ///     <paramref name="note" /> moved to the nearest note of <paramref name="scale" /> on <paramref name="key" />
    ///     (a tie goes down).
    /// </summary>
    public static int Snap(int note, int key, int scale) {
        for (var distance = 0; distance < 12; distance++) {
            if (Contains(note - distance, key, scale)) {
                return note - distance;
            }

            if (Contains(note + distance, key, scale)) {
                return note + distance;
            }
        }

        return note;
    }

    /// <summary>Scale note <paramref name="note" /> (in the scale) moved <paramref name="degrees" /> scale steps.</summary>
    public static int Shift(int note, int key, int scale, int degrees) {
        var steps = Of(scale);
        var relative = note - key;
        var octave = (int)Math.Floor(relative / 12.0);
        var within = relative - octave * 12;
        var degree = steps.IndexOf(within);
        if (degree < 0) {
            return note;
        }

        var target = degree + degrees;
        var octaves = (int)Math.Floor((double)target / steps.Length);
        var wrapped = target - octaves * steps.Length;
        return key + (octave + octaves) * 12 + steps[wrapped];
    }

    public static bool Contains(int note, int key, int scale) {
        var within = ((note - key) % 12 + 12) % 12;
        return Of(scale).Contains(within);
    }
}
