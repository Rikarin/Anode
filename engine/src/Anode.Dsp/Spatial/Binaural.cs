using Anode.Dsp.Spectral;
using System.Buffers.Binary;
using System.Text;

namespace Anode.Dsp.Spatial;

/// <summary>
///     A set of head-related impulse responses (SADIE II, converted offline from SOFA into Anode's compact file:
///     docs/adr/0012-spatial-audio.md): measured directions (azimuth, elevation in degrees) with a left and right impulse
///     response each, at one sample rate. Loaded off the audio thread; immutable.
/// </summary>
/// <remarks>
///     The file (<c>.anhrtf</c>, written by <c>scripts/build-hrtf.py</c>, little-endian): <c>ANHR</c>, version 1, the
///     sample rate, the taps per response, the direction count, the scale of the 16-bit samples, the length of a UTF-8
///     metadata text (source and licence) and the text; then per direction its azimuth, elevation and each ear's delay
///     (samples: the peak of the response low-passed at 1.5 kHz); then per direction the left and right responses as 16-bit
///     integers. Anode ships <see cref="DefaultFileName" />: SADIE II subject D2 (KEMAR), 48 kHz, Apache-2.0.
/// </remarks>
public sealed class HrtfSet {
    /// <summary>The set Anode ships, in <c>Spatial/Hrtf</c> of the DSP's sources (docs/spatial/README.md).</summary>
    public const string DefaultFileName = "sadie2-d2-48k.anhrtf";

    const int HeaderBytes = 28;
    readonly float[] azimuths;
    readonly float[] elevations;
    readonly double[] x;
    readonly double[] y;
    readonly double[] z;
    readonly float[] onsets;
    readonly float[] responses;

    HrtfSet(int sampleRate, int length, string metadata, float[] table, float[] responses) {
        SampleRate = sampleRate;
        Length = length;
        Metadata = metadata;
        DirectionCount = table.Length / 4;
        azimuths = new float[DirectionCount];
        elevations = new float[DirectionCount];
        onsets = new float[DirectionCount * 2];
        x = new double[DirectionCount];
        y = new double[DirectionCount];
        z = new double[DirectionCount];
        for (var d = 0; d < DirectionCount; d++) {
            azimuths[d] = table[4 * d];
            elevations[d] = table[4 * d + 1];
            onsets[2 * d] = table[4 * d + 2];
            onsets[2 * d + 1] = table[4 * d + 3];
            var v = Vector3.FromPolar(azimuths[d], elevations[d]);
            (x[d], y[d], z[d]) = (v.X, v.Y, v.Z);
        }

        this.responses = responses;
    }

    /// <summary>Reads Anode's HRTF file (<c>.anhrtf</c>); a file that is not one throws <see cref="InvalidDataException" />.</summary>
    public static HrtfSet Load(string path) {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderBytes || !bytes.AsSpan(0, 4).SequenceEqual("ANHR"u8)) {
            throw new InvalidDataException($"{path} is not an Anode HRTF file.");
        }

        var header = bytes.AsSpan();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var scale = BinaryPrimitives.ReadSingleLittleEndian(header[20..]);
        var metadataLength = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        if (version != 1) {
            throw new InvalidDataException($"{path}: HRTF file version {version} is not supported (1).");
        }

        if (rate is < 8000 or > 384_000 || length is 0 or > 65_536 || count is 0 or > 100_000
            || metadataLength > 1 << 20 || !float.IsFinite(scale)
            || bytes.LongLength != HeaderBytes + metadataLength + count * 16L + count * 2L * length * 2) {
            throw new InvalidDataException($"{path}: the HRTF file's header does not match its size.");
        }

        var metadata = Encoding.UTF8.GetString(bytes, HeaderBytes, (int)metadataLength);
        var at = HeaderBytes + (int)metadataLength;
        var table = new float[count * 4];
        for (var i = 0; i < table.Length; i++, at += 4) {
            table[i] = BinaryPrimitives.ReadSingleLittleEndian(header[at..]);
        }

        var responses = new float[count * 2 * length];
        for (var i = 0; i < responses.Length; i++, at += 2) {
            responses[i] = BinaryPrimitives.ReadInt16LittleEndian(header[at..]) * scale;
        }

        return new((int)rate, (int)length, metadata, table, responses);
    }

    /// <summary>The set Anode ships, found from <paramref name="folder" /> or its parents' <c>engine/src/Anode.Dsp/Spatial/Hrtf</c> (tests, development).</summary>
    public static string? Find(string folder) {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent) {
            foreach (var candidate in new[] {
                         Path.Combine(dir.FullName, "Hrtf", DefaultFileName),
                         Path.Combine(dir.FullName, "engine", "src", "Anode.Dsp", "Spatial", "Hrtf", DefaultFileName)
                     }) {
                if (File.Exists(candidate)) {
                    return candidate;
                }
            }
        }

        return null;
    }

    public int SampleRate { get; }

    /// <summary>Taps of each impulse response.</summary>
    public int Length { get; }

    public int DirectionCount { get; }

    /// <summary>The file's source and licence text.</summary>
    public string Metadata { get; }

    /// <summary>A measured direction (degrees, ADM).</summary>
    public (float Azimuth, float Elevation) Direction(int index) => (azimuths[index], elevations[index]);

    /// <summary>
    ///     The left and right impulse responses for a direction, interpolated between the nearest measured ones (for
    ///     objects), resampled to the set's rate; off the audio thread.
    /// </summary>
    /// <remarks>
    ///     A measured direction (within 0.01°) is returned as measured. Otherwise the three nearest measured directions
    ///     are weighted by the inverse of their angular distance; each ear's responses are shifted to the weighted delay
    ///     before they are summed, so the interaural delay interpolates instead of smearing. Allocation-free and
    ///     thread-safe (the set is immutable), so it may run on the audio thread too; the spans hold
    ///     <see cref="Length" /> samples.
    /// </remarks>
    public void ImpulseResponses(float azimuth, float elevation, Span<float> left, Span<float> right) {
        var v = Vector3.FromPolar(azimuth, elevation);
        Span<int> nearest = [-1, -1, -1];
        Span<double> cosines = [-2, -2, -2];
        for (var d = 0; d < DirectionCount; d++) {
            var cos = v.X * x[d] + v.Y * y[d] + v.Z * z[d];
            for (var k = 0; k < 3; k++) {
                if (cos <= cosines[k]) {
                    continue;
                }

                for (var m = 2; m > k; m--) {
                    cosines[m] = cosines[m - 1];
                    nearest[m] = nearest[m - 1];
                }

                cosines[k] = cos;
                nearest[k] = d;
                break;
            }
        }

        Span<double> weights = stackalloc double[3];
        var exact = Math.Acos(Math.Clamp(cosines[0], -1, 1)) < 0.01 * Math.PI / 180;
        var total = 0.0;
        for (var k = 0; k < 3; k++) {
            weights[k] = exact ? k == 0 ? 1 : 0 : 1 / Math.Max(1e-9, Math.Acos(Math.Clamp(cosines[k], -1, 1)));
            total += weights[k];
        }

        for (var k = 0; k < 3; k++) {
            weights[k] /= total;
        }

        Mix(nearest, weights, 0, left[..Length]);
        Mix(nearest, weights, 1, right[..Length]);
    }

    void Mix(ReadOnlySpan<int> nearest, ReadOnlySpan<double> weights, int ear, Span<float> output) {
        output.Clear();
        var onset = 0.0;
        for (var k = 0; k < 3; k++) {
            onset += weights[k] * onsets[2 * nearest[k] + ear];
        }

        for (var k = 0; k < 3; k++) {
            if (weights[k] == 0) {
                continue;
            }

            var shift = (int)Math.Round(onset - onsets[2 * nearest[k] + ear]);
            var response = responses.AsSpan((nearest[k] * 2 + ear) * Length, Length);
            var w = (float)weights[k];
            for (var n = 0; n < Length; n++) {
                var from = n - shift;
                if (from >= 0 && from < Length) {
                    output[n] += w * response[from];
                }
            }
        }
    }

    /// <summary>
    ///     Resamples an impulse response by windowed-sinc interpolation (a Blackman window of 32 zero crossings,
    ///     band-limited to the lower rate); allocation-free.
    /// </summary>
    internal static void Resample(ReadOnlySpan<float> input, double ratio, Span<float> output) {
        var cutoff = Math.Min(1, ratio);
        const int half = 16;
        for (var n = 0; n < output.Length; n++) {
            var t = n / ratio;
            var centre = (int)Math.Floor(t);
            var sum = 0.0;
            var reach = (int)Math.Ceiling(half / cutoff);
            for (var k = centre - reach + 1; k <= centre + reach; k++) {
                if (k < 0 || k >= input.Length) {
                    continue;
                }

                var u = (t - k) * cutoff;
                var sinc = Math.Abs(u) < 1e-12 ? 1 : Math.Sin(Math.PI * u) / (Math.PI * u);
                var w = Math.Abs(u) >= half ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * u / half) + 0.08 * Math.Cos(2 * Math.PI * u / half);
                sum += input[k] * sinc * w * cutoff;
            }

            output[n] = (float)sum;
        }
    }

    /// <summary>The response length at <paramref name="sampleRate" />.</summary>
    internal int LengthAt(float sampleRate) =>
        sampleRate == SampleRate ? Length : (int)Math.Ceiling(Length * (double)sampleRate / SampleRate);

    /// <summary>
    ///     A direction's responses at <paramref name="sampleRate" /> (<see cref="LengthAt" /> taps), using
    ///     <paramref name="scratch" /> (2 × <see cref="Length" />) when it must resample; allocation-free.
    /// </summary>
    internal void ImpulseResponsesAt(
        float azimuth,
        float elevation,
        float sampleRate,
        Span<float> left,
        Span<float> right,
        Span<float> scratch
    ) {
        if (sampleRate == SampleRate) {
            ImpulseResponses(azimuth, elevation, left, right);
            return;
        }

        var l = scratch[..Length];
        var r = scratch.Slice(Length, Length);
        ImpulseResponses(azimuth, elevation, l, r);
        var ratio = sampleRate / SampleRate;
        Resample(l, ratio, left[..LengthAt(sampleRate)]);
        Resample(r, ratio, right[..LengthAt(sampleRate)]);
    }
}

/// <summary>
///     Binaural monitoring (docs/11-phase-10.md, workstream D): each virtual speaker of a layout (and, optionally,
///     objects directly) convolved with its HRTF pair by uniformly partitioned FFT convolution, summed to two ears;
///     the LFE goes to both ears unfiltered. Real-time safe after <see cref="Prepare(HrtfSet, SpeakerLayout, float, int)" />; its latency is reported for
///     the monitoring path only (never in bounces).
/// </summary>
/// <remarks>
///     Each ear of each speaker is a <see cref="PartitionedConvolver" /> (partitions of <see cref="PartitionSize" />,
///     the first run directly), so there is no added latency at any block size; the HRIRs' own onset (about 1.5 ms for
///     SADIE II's, roughly linear-phase) is acoustic delay, not reported. Objects (<see cref="ProcessObject" />) each run two convolver pairs
///     and cross-fade over a block from the old direction's to the new one's when the direction moves by more than
///     <see cref="ObjectStepDegrees" />; their responses are interpolated between measured directions
///     (<see cref="HrtfSet.ImpulseResponses" />) on the audio thread, allocation-free.
/// </remarks>
public sealed class BinauralRenderer {
    /// <summary>The convolvers' partition size (taps per FFT block).</summary>
    public const int PartitionSize = 32;

    /// <summary>How far an object's direction moves before its responses are recomputed.</summary>
    public const float ObjectStepDegrees = 1;

    HrtfSet? set;
    float rate;
    int latency;
    int lfe = -1;
    int taps;
    PartitionedConvolver[] lefts = [];
    PartitionedConvolver[] rights = [];
    ObjectState[] objects = [];
    float[] scratch = [];
    float[] responseLeft = [];
    float[] responseRight = [];

    /// <summary>Off the audio thread: the HRTF pairs of <paramref name="layout" />'s speakers, partitioned for the block size.</summary>
    public void Prepare(HrtfSet hrtfs, SpeakerLayout layout, float sampleRate, int maxFrames) =>
        Prepare(hrtfs, layout, sampleRate, maxFrames, 0);

    /// <summary>
    ///     Off the audio thread: as <see cref="Prepare(HrtfSet, SpeakerLayout, float, int)" />, with room for
    ///     <paramref name="maxObjects" /> objects rendered directly (<see cref="ProcessObject" />).
    /// </summary>
    public void Prepare(HrtfSet hrtfs, SpeakerLayout layout, float sampleRate, int maxFrames, int maxObjects) {
        set = hrtfs;
        rate = sampleRate;
        latency = 0;
        lfe = layout.LfeIndex;
        taps = hrtfs.LengthAt(sampleRate);
        scratch = new float[2 * hrtfs.Length];
        responseLeft = new float[taps];
        responseRight = new float[taps];
        Layout = layout;
        lefts = new PartitionedConvolver[layout.ChannelCount];
        rights = new PartitionedConvolver[layout.ChannelCount];
        for (var c = 0; c < layout.ChannelCount; c++) {
            if (c == lfe) {
                continue;
            }

            (lefts[c], rights[c]) = (new(), new());
            lefts[c].Allocate(PartitionSize, taps);
            rights[c].Allocate(PartitionSize, taps);
            hrtfs.ImpulseResponsesAt(
                layout.Azimuths[c],
                layout.Elevations[c],
                sampleRate,
                responseLeft,
                responseRight,
                scratch
            );
            lefts[c].SetFilter(responseLeft);
            rights[c].SetFilter(responseRight);
        }

        objects = new ObjectState[maxObjects];
        for (var o = 0; o < maxObjects; o++) {
            objects[o] = new(taps);
        }
    }

    /// <summary>The layout <see cref="Prepare(HrtfSet, SpeakerLayout, float, int)" /> set up.</summary>
    public SpeakerLayout? Layout { get; private set; }

    /// <summary>The added delay in samples (0 for a partition size equal to the block).</summary>
    public int LatencySamples => latency;

    /// <summary>
    ///     Renders the layout's channels in <paramref name="speakers" /> to <paramref name="left" /> and
    ///     <paramref name="right" />.
    /// </summary>
    public void Process(PlanarBlock speakers, Span<float> left, Span<float> right) {
        var frames = Math.Min(speakers.Frames, Math.Min(left.Length, right.Length));
        left[..frames].Clear();
        right[..frames].Clear();
        for (var c = 0; c < speakers.Channels && c < lefts.Length; c++) {
            var source = speakers.Channel(c);
            if (c == lfe) {
                for (var n = 0; n < frames; n++) {
                    left[n] += source[n];
                    right[n] += source[n];
                }

                continue;
            }

            var l = lefts[c];
            var r = rights[c];
            for (var n = 0; n < frames; n++) {
                var s = source[n];
                left[n] += l.Tick(s);
                right[n] += r.Tick(s);
            }
        }
    }

    /// <summary>
    ///     Adds object <paramref name="index" />'s mono signal at a direction into <paramref name="left" /> and
    ///     <paramref name="right" /> (after <see cref="Process" />, which replaces them). A direction that moved by more
    ///     than <see cref="ObjectStepDegrees" /> since the last change cross-fades to its responses over this block.
    /// </summary>
    public void ProcessObject(
        int index,
        ReadOnlySpan<float> input,
        float azimuth,
        float elevation,
        Span<float> left,
        Span<float> right
    ) {
        var o = objects[index];
        var frames = Math.Min(input.Length, Math.Min(left.Length, right.Length));
        var target = Vector3.FromPolar(azimuth, elevation);
        var fade = false;
        if (!o.Loaded || Math.Acos(Math.Clamp(target.Dot(o.Direction), -1, 1)) > ObjectStepDegrees * Math.PI / 180) {
            var next = o.Loaded ? 1 - o.Active : o.Active;
            set!.ImpulseResponsesAt(azimuth, elevation, rate, responseLeft, responseRight, scratch);
            o.Lefts[next].SetFilter(responseLeft);
            o.Rights[next].SetFilter(responseRight);
            fade = o.Loaded;
            o.Active = next;
            o.Direction = target;
            o.Loaded = true;
        }

        var active = o.Active;
        var inverse = 1f / frames;
        for (var n = 0; n < frames; n++) {
            var s = input[n];
            var l = o.Lefts[active].Tick(s);
            var r = o.Rights[active].Tick(s);
            var lo = o.Lefts[1 - active].Tick(s);
            var ro = o.Rights[1 - active].Tick(s);
            if (fade) {
                var g = (n + 1) * inverse;
                left[n] += g * l + (1 - g) * lo;
                right[n] += g * r + (1 - g) * ro;
            } else {
                left[n] += l;
                right[n] += r;
            }
        }
    }

    /// <summary>Clears the convolution history (stop, locate).</summary>
    public void Reset() {
        for (var c = 0; c < lefts.Length; c++) {
            lefts[c]?.Reset();
            rights[c]?.Reset();
        }

        foreach (var o in objects) {
            for (var k = 0; k < 2; k++) {
                o.Lefts[k].Reset();
                o.Rights[k].Reset();
            }
        }
    }

    /// <summary>Forgets object <paramref name="index" />'s direction (its next block starts there, no cross-fade).</summary>
    public void ResetObject(int index) {
        var o = objects[index];
        o.Loaded = false;
        for (var k = 0; k < 2; k++) {
            o.Lefts[k].Reset();
            o.Rights[k].Reset();
        }
    }

    sealed class ObjectState {
        public readonly PartitionedConvolver[] Lefts = [new(), new()];
        public readonly PartitionedConvolver[] Rights = [new(), new()];
        public int Active;
        public bool Loaded;
        public Vector3 Direction;

        public ObjectState(int taps) {
            for (var k = 0; k < 2; k++) {
                Lefts[k].Allocate(PartitionSize, taps);
                Rights[k].Allocate(PartitionSize, taps);
            }
        }
    }
}
