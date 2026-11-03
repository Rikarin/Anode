using Anode.Dsp.Oscillators;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Anode.Dsp.Instruments.Tape;

/// <summary>
///     A tape set of <see cref="TapeReplay" />: one mono recording per key, from its start, at most
///     <see cref="MaxSeconds" /> long, at one sample rate. Immutable once built; loaded or made off the audio thread.
/// </summary>
/// <remarks>
///     The file (<c>.antape</c>, little-endian): <c>ANTP</c>, the format version (16-bit, 1), 16 reserved bits, the sample
///     rate (32-bit), the first key and the key count (a byte each), the name (a 16-bit byte length and UTF-8); then per
///     key its frame count (32-bit), its scale (32-bit float) and its samples as 16-bit integers times the scale.
/// </remarks>
public sealed class TapeSet {
    /// <summary>A tape's length: a recording is cut there.</summary>
    public const double MaxSeconds = 8;

    /// <summary>The file's extension.</summary>
    public const string Extension = ".antape";

    const ushort Version = 1;
    static ReadOnlySpan<byte> Magic => "ANTP"u8;

    static readonly Lazy<TapeSet> Builtin = new(Generate);

    readonly short[][] samples;
    readonly float[] scales;

    TapeSet(string name, int sampleRate, int lowKey, short[][] samples, float[] scales) {
        Name = name;
        SampleRate = sampleRate;
        LowKey = lowKey;
        this.samples = samples;
        this.scales = scales;
    }

    public string Name { get; }

    public int SampleRate { get; }

    /// <summary>The MIDI note of the first key.</summary>
    public int LowKey { get; }

    public int KeyCount => samples.Length;

    /// <summary>The MIDI note of the last key.</summary>
    public int HighKey => LowKey + KeyCount - 1;

    /// <summary>
    ///     The set every <see cref="TapeReplay" /> has before it loads one: sawtooth "strings" doubled an octave below, a
    ///     key, generated in code (35 keys, G2 … F5, as the classic tape instrument's, at 24 kHz).
    /// </summary>
    public static TapeSet Default => Builtin.Value;

    /// <summary>The frames of key <paramref name="key" /> (an index from <see cref="LowKey" />).</summary>
    public int Frames(int key) => samples[key].Length;

    /// <summary>
    ///     A set from <paramref name="recordings" /> (one per key from <paramref name="lowKey" />, any length; each cut to
    ///     <see cref="MaxSeconds" />), stored as 16-bit samples scaled to each recording's peak.
    /// </summary>
    public static TapeSet From(string name, int sampleRate, int lowKey, IReadOnlyList<float[]> recordings) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(recordings.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lowKey + recordings.Count, 128);
        var limit = (int)(MaxSeconds * sampleRate);
        var keys = new short[recordings.Count][];
        var scales = new float[recordings.Count];
        for (var k = 0; k < keys.Length; k++) {
            var recording = recordings[k].AsSpan(0, Math.Min(recordings[k].Length, limit));
            var peak = 0f;
            foreach (var x in recording) {
                peak = Math.Max(peak, MathF.Abs(x));
            }

            scales[k] = peak > 0 ? peak / short.MaxValue : 1f / short.MaxValue;
            keys[k] = new short[recording.Length];
            for (var i = 0; i < recording.Length; i++) {
                keys[k][i] = (short)Math.Clamp(MathF.Round(recording[i] / scales[k]), -short.MaxValue, short.MaxValue);
            }
        }

        return new(name, sampleRate, lowKey, keys, scales);
    }

    /// <summary>Reads a tape set file; one that is not one throws <see cref="InvalidDataException" />.</summary>
    public static TapeSet Load(string path) => Read(File.ReadAllBytes(path));

    public static TapeSet Read(ReadOnlySpan<byte> file) {
        if (file.Length < 16 || !file[..4].SequenceEqual(Magic)) {
            throw new InvalidDataException("Not an Anode tape set.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (version != Version) {
            throw new InvalidDataException($"Tape set version {version} is not supported ({Version}).");
        }

        var rate = BinaryPrimitives.ReadInt32LittleEndian(file[8..]);
        int lowKey = file[12];
        int count = file[13];
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(file[14..]);
        if (rate is < 1_000 or > 384_000 || count == 0 || lowKey + count > 128 || 16 + nameLength > file.Length) {
            throw new InvalidDataException("The tape set's header is not valid.");
        }

        var name = Encoding.UTF8.GetString(file.Slice(16, nameLength));
        var at = 16 + nameLength;
        var limit = (int)(MaxSeconds * rate);
        var keys = new short[count][];
        var scales = new float[count];
        for (var k = 0; k < count; k++) {
            if (at + 8 > file.Length) {
                throw new InvalidDataException("The tape set is cut short.");
            }

            var frames = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
            scales[k] = BinaryPrimitives.ReadSingleLittleEndian(file[(at + 4)..]);
            at += 8;
            if (frames < 0 || frames > limit || at + 2L * frames > file.Length || !float.IsFinite(scales[k])) {
                throw new InvalidDataException("A tape of the set is not valid.");
            }

            keys[k] = new short[frames];
            for (var i = 0; i < frames; i++, at += 2) {
                keys[k][i] = BinaryPrimitives.ReadInt16LittleEndian(file[at..]);
            }
        }

        return new(name, rate, lowKey, keys, scales);
    }

    /// <summary>The set as a file's bytes.</summary>
    public byte[] ToBytes() {
        var name = Encoding.UTF8.GetBytes(Name);
        var length = 16 + name.Length + samples.Sum(static k => 8 + 2 * k.Length);
        var bytes = new byte[length];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], SampleRate);
        span[12] = (byte)LowKey;
        span[13] = (byte)KeyCount;
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)name.Length);
        name.CopyTo(span[16..]);
        var at = 16 + name.Length;
        for (var k = 0; k < KeyCount; k++) {
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], samples[k].Length);
            BinaryPrimitives.WriteSingleLittleEndian(span[(at + 4)..], scales[k]);
            at += 8;
            foreach (var s in samples[k]) {
                BinaryPrimitives.WriteInt16LittleEndian(span[at..], s);
                at += 2;
            }
        }

        return bytes;
    }

    /// <summary>
    ///     Key <paramref name="key" />'s recording at <paramref name="position" /> (in its samples, fractional) by
    ///     cubic Hermite interpolation; silence outside it. Allocation-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(int key, double position) {
        var tape = samples[key];
        var whole = (int)Math.Floor(position);
        if (whole < -1 || whole >= tape.Length) {
            return 0;
        }

        var t = (float)(position - whole);
        var y0 = At(tape, whole - 1);
        var y1 = At(tape, whole);
        var y2 = At(tape, whole + 1);
        var y3 = At(tape, whole + 2);
        var c1 = 0.5f * (y2 - y0);
        var c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        var c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return (((c3 * t + c2) * t + c1) * t + y1) * scales[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float At(short[] tape, int i) => (uint)i < (uint)tape.Length ? tape[i] : 0;

    /// <summary>
    ///     The built-in set: per key a sawtooth and one an octave below at half its level, low-passed, swelling in and
    ///     fading.
    /// </summary>
    static TapeSet Generate() {
        const int rate = 24_000;
        const int low = 43;
        const int keys = 35;
        var frames = (int)(MaxSeconds * rate);
        var recordings = new float[keys][];
        for (var k = 0; k < keys; k++) {
            var hz = Pitch.Frequency(low + k);
            var recording = new float[frames];
            double a = 0, b = 0.37;
            var stepA = hz / rate;
            var stepB = hz / 2 / rate;
            // Two one-poles: a 12 dB/octave roll-off from three times the pitch (and at least 1.2 kHz).
            var corner = 1 - Math.Exp(-2 * Math.PI * Math.Max(1_200, 3 * hz) / rate);
            double s1 = 0, s2 = 0;
            for (var i = 0; i < frames; i++) {
                var x = PolyBlep.Saw(a, stepA) + 0.5 * PolyBlep.Saw(b, stepB);
                a += stepA;
                if (a >= 1) {
                    a -= 1;
                }

                b += stepB;
                if (b >= 1) {
                    b -= 1;
                }

                s1 += (x - s1) * corner;
                s2 += (s1 - s2) * corner;
                var t = i / (double)rate;
                var envelope = Math.Min(1, t / 0.09)
                    * (1 - 0.35 * t / MaxSeconds)
                    * Math.Min(1, (MaxSeconds - t) / 0.05);
                recording[i] = (float)(0.3 * s2 * envelope);
            }

            recordings[k] = recording;
        }

        return From("Default Strings", rate, low, recordings);
    }
}
