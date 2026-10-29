using Anode.Dsp.Analysis;
using Anode.Dsp.Envelopes;
using Anode.Dsp.IO;
using Anode.Dsp.Instruments.Sampler;
using System.Buffers.Binary;

namespace Anode.Dsp.Instruments.Quick;

/// <summary>How <see cref="QuickSampler" /> plays its sample.</summary>
public enum QuickSamplerMode {
    /// <summary>The whole sample (start to end) on every note, whatever the note-off.</summary>
    OneShot,

    /// <summary>While the key is held, through the ADSR, looping if Loop is on.</summary>
    Classic,

    /// <summary>The sample cut into slices, one a key from Slice Start Note up, each at its original pitch.</summary>
    Slice,

    /// <summary>Records from the input fed to it (<see cref="IInstrumentInput" />), then plays the take as Classic.</summary>
    Recorder
}

/// <summary>
///     An instrument that takes audio input: the host feeds it the track's input on the audio thread, once per block
///     before <see cref="IInstrument.Process" />, with the block's frames (docs/instruments/T2.md).
/// </summary>
public interface IInstrumentInput {
    /// <summary>The block's input, <c>left.Length</c> frames (equal to <c>right.Length</c>); allocation-free.</summary>
    void Feed(ReadOnlySpan<float> left, ReadOnlySpan<float> right);
}

/// <summary><c>anode.quick-sampler</c>'s parameters.</summary>
public static class QuickSamplerParameters {
    static readonly ParameterList List = new();

    public static readonly int Mode = List.Choice("mode", "Mode", ["One Shot", "Classic", "Slice", "Recorder"], 1);
    public static readonly int RootNote = List.Stepped("rootNote", "Root Note", 0, 127, 60, string.Empty, 127);
    public static readonly int KeyFollow = List.Toggle("keyFollow", "Key Follow", true);
    public static readonly int Tune = List.Add("tune", "Tune", -24, 24, 0, "st", 1);
    public static readonly int Fine = List.Add("fine", "Fine", -100, 100, 0, "ct");
    public static readonly int Start = List.Add("start", "Sample Start", 0, 100, 0, "%", 2);
    public static readonly int End = List.Add("end", "Sample End", 0, 100, 100, "%", 3);
    public static readonly int Loop = List.Toggle("loop", "Loop", false);
    public static readonly int LoopStart = List.Add("loopStart", "Loop Start", 0, 100, 0, "%");
    public static readonly int LoopEnd = List.Add("loopEnd", "Loop End", 0, 100, 100, "%");
    public static readonly int Attack = List.Add("attack", "Attack", 0, 10_000, 0, "ms", 4);
    public static readonly int Decay = List.Add("decay", "Decay", 1, 20_000, 1_000, "ms");
    public static readonly int Sustain = List.Add("sustain", "Sustain", 0, 100, 100, "%");
    public static readonly int Release = List.Add("release", "Release", 1, 10_000, 100, "ms", 5);

    public static readonly int SliceMode =
        List.Choice("sliceMode", "Slice Mode", ["Transients", "4 Equal", "8 Equal", "16 Equal", "32 Equal"], 0);

    public static readonly int SliceStartNote =
        List.Stepped("sliceStartNote", "Slice Start Note", 0, 127, 36, string.Empty, 127);

    public static readonly int RecordArm = List.Toggle("recordArm", "Record Arm", false);
    public static readonly int RecordThreshold = List.Add("recordThreshold", "Record Threshold", -80, 0, -40, "dB");

    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 70, "%");

    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 12, 2, "st", 12);
    public static readonly int Volume = List.Add("volume", "Volume", -60, 12, 0, "dB", 6);

    public static IReadOnlyList<InstrumentParameter> All => List.All;
}

/// <summary>
///     A sample in memory for <see cref="QuickSampler" />, each channel padded for the cubic interpolation, with the
///     slice points its transients give.
/// </summary>
internal sealed class QuickSample {
    public const int PadBefore = 1;
    public const int PadAfter = 3;

    public QuickSample(float[] left, float[] right, int frames, int sampleRate, long[] transients) {
        Left = left;
        Right = right;
        Frames = frames;
        SampleRate = sampleRate;
        Transients = transients;
    }

    /// <summary>A sample of <paramref name="channels" /> (one or two) as a WAV gives them.</summary>
    public static QuickSample From(float[][] channels, int sampleRate) {
        var frames = channels[0].Length;
        var left = Pad(channels[0]);
        var right = channels.Length > 1 ? Pad(channels[1]) : left;
        var mono = channels.Length > 1 ? Downmix.ToMono(channels) : channels[0];
        var transients = frames > 0 ? Anode.Dsp.Analysis.Transients.Detect(mono, sampleRate) : [];
        return new(left, right, frames, sampleRate, transients);
    }

    public float[] Left { get; }
    public float[] Right { get; }

    /// <summary>The frames that play (a recording grows it on the audio thread).</summary>
    public int Frames { get; set; }

    public int SampleRate { get; }

    /// <summary>The detected onsets, ascending (empty for a recording: its slices are equal).</summary>
    public long[] Transients { get; }

    /// <summary>The frames the buffers hold.</summary>
    public int Capacity => Left.Length - PadBefore - PadAfter;

    static float[] Pad(float[] samples) {
        var padded = new float[samples.Length + PadBefore + PadAfter];
        samples.CopyTo(padded, PadBefore);
        return padded;
    }
}

/// <summary>
///     <c>anode.quick-sampler</c>: a single-sample instrument, 16 voices, with one-shot, classic, slice and recorder
///     modes (<see cref="QuickSamplerMode" />). It loads a WAV by <see cref="LoadFile" /> (its <c>smpl</c> unity note
///     sets Root Note) and finds its transients for slicing then.
/// </summary>
/// <remarks>
///     <para>
///         A note plays the sample at its root when Key Follow is off (as a Drum Machine Designer pad uses it), else
///         transposed by the note's distance from Root Note; Tune, Fine and the pitch bend apply on top. Start and End
///         bound what plays, as percentages of the sample; Loop (classic and recorder) repeats Loop Start … Loop End
///         while the key is held. In slice mode, slice <c>k</c> plays on note Slice Start Note + <c>k</c> from its start
///         to the next slice's: the transients' (the sample's start counts as one) or 4 … 32 equal parts of Start … End.
///     </para>
///     <para>
///         Recorder: once <see cref="PrepareRecorder" /> has made room (Prepare does when the mode is Recorder), and
///         while Record Arm is on, the first input sample at or above Record Threshold starts a take, which grows until
///         Record Arm goes off or 30 s are full, and then is the sample (sliced in equal parts). A take is kept in the
///         state, so a project reopens with it. The host feeds the input through <see cref="IInstrumentInput.Feed" />.
///     </para>
/// </remarks>
public sealed class QuickSampler : IInstrument, IPolyphonic, IInstrumentInput {
    public const string Id = "anode.quick-sampler";
    public const int Polyphony = 16;

    /// <summary>The longest take the recorder keeps, in seconds.</summary>
    public const double RecordSeconds = 30;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Quick Sampler", "Anode", "1.0.0", QuickSamplerParameters.All, [".wav"]);

    public InstrumentDescriptor Descriptor => Info;

    public int MaxVoices => Polyphony;

    public int ActiveVoices => engine.ActiveVoices;

    /// <summary>The file loaded, or null (none, or a recorded take).</summary>
    public string? File => engine.Path;

    /// <summary>The frames of the sample that plays (0: none).</summary>
    public int Frames => engine.Sample?.Frames ?? 0;

    /// <summary>Whether the recorder is taking input now.</summary>
    public bool Recording => engine.Recording;

    /// <summary>The slice starts, in frames, as slice mode cuts the sample now.</summary>
    public IReadOnlyList<long> Slices => engine.SliceStarts();

    public bool IsSounding(int note) => engine.IsSounding(note);

    public void Prepare(float sampleRate, int maxFrames) => engine.Prepare(sampleRate, maxFrames);

    /// <summary>Control thread: makes room for a take of <see cref="RecordSeconds" /> (recorder mode needs it).</summary>
    public void PrepareRecorder() => engine.PrepareRecorder();

    public void SetParameter(int index, float value) => engine.SetParameter(index, value);

    public float GetParameter(int index) => engine.GetParameter(index);

    public void Feed(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => engine.Feed(left, right);

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) =>
        engine.Process(events, left, right);

    public void Reset() => engine.Reset();

    public bool LoadFile(string path) => engine.LoadFile(path);

    public byte[]? SaveState() => engine.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => engine.LoadState(state);

    public void Dispose() => engine.Dispose();

    sealed class Engine : PolyInstrument<QuickSamplerVoice> {
        const int MaxSlices = 128;
        static ReadOnlySpan<byte> TakeMagic => "QSTK"u8;

        readonly QuickSamplerControl control;
        readonly long[] slices = new long[MaxSlices + 1];
        QuickSample? take;

        public Engine() : this(new()) { }

        Engine(QuickSamplerControl control) : base(
            Info,
            QuickSampler.Polyphony,
            8,
            _ => new(control),
            QuickSamplerParameters.Volume
        ) {
            this.control = control;
        }

        public string? Path { get; private set; }

        public QuickSample? Sample { get; private set; }

        public bool Recording { get; private set; }

        QuickSamplerMode Mode => (QuickSamplerMode)(int)Parameters[QuickSamplerParameters.Mode];

        public override void Prepare(float sampleRate, int maxFrames) {
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            base.Prepare(sampleRate, maxFrames);
            if (Mode == QuickSamplerMode.Recorder) {
                PrepareRecorder();
            }
        }

        public void PrepareRecorder() {
            if (!Prepared) {
                return;
            }

            var capacity = (int)(RecordSeconds * SampleRate);
            if (take is null || take.Capacity < capacity || take.SampleRate != (int)SampleRate) {
                var length = capacity + QuickSample.PadBefore + QuickSample.PadAfter;
                take = new(new float[length], new float[length], 0, (int)SampleRate, []);
            }
        }

        public override bool LoadFile(string path) {
            QuickSample sample;
            WavData wav;
            try {
                var full = System.IO.Path.GetFullPath(path);
                wav = WavReader.Read(full);
                sample = QuickSample.From(wav.Channels, wav.SampleRate);
                path = full;
            } catch (IOException) {
                return false;
            } catch (InvalidDataException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            } catch (ArgumentException) {
                return false;
            }

            if (wav.RootNote >= 0) {
                Parameters.Set(QuickSamplerParameters.RootNote, wav.RootNote);
            }

            Sample = sample;
            Path = path;
            Reset();
            return true;
        }

        public override byte[]? SaveState() {
            var parameters = InstrumentState.Write(Id, Path, Info.Parameters, Parameters);
            var sample = Sample;
            if (Path is not null || sample is null || sample.Frames == 0) {
                return parameters;
            }

            // A recorded take goes in the state: its magic, rate, frames and channels as 32-bit floats.
            var frames = sample.Frames;
            var state = new byte[parameters.Length + 12 + frames * 8];
            parameters.CopyTo(state, 0);
            var at = parameters.Length;
            TakeMagic.CopyTo(state.AsSpan(at));
            BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(at + 4), sample.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(at + 8), frames);
            at += 12;
            for (var i = 0; i < frames; i++) {
                BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(at), sample.Left[i + QuickSample.PadBefore]);
                BinaryPrimitives.WriteSingleLittleEndian(
                    state.AsSpan(at + frames * 4),
                    sample.Right[i + QuickSample.PadBefore]
                );
                at += 4;
            }

            return state;
        }

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out var file, out var values, out var end)) {
                return false;
            }

            QuickSample? recorded = null;
            var rest = state[end..];
            if (rest.Length >= 12 && rest[..4].SequenceEqual(TakeMagic)) {
                var rate = BinaryPrimitives.ReadInt32LittleEndian(rest[4..]);
                var frames = BinaryPrimitives.ReadInt32LittleEndian(rest[8..]);
                if (rate <= 0 || frames <= 0 || rest.Length < 12 + (long)frames * 8) {
                    return false;
                }

                var left = new float[frames];
                var right = new float[frames];
                for (var i = 0; i < frames; i++) {
                    left[i] = BinaryPrimitives.ReadSingleLittleEndian(rest[(12 + i * 4)..]);
                    right[i] = BinaryPrimitives.ReadSingleLittleEndian(rest[(12 + frames * 4 + i * 4)..]);
                }

                var padded = QuickSample.From([left, right], rate);
                recorded = new(padded.Left, padded.Right, frames, rate, []);
            }

            var loaded = true;
            if (file is not null) {
                loaded = file == Path || LoadFile(file);
            } else {
                Path = null;
                Sample = recorded;
            }

            InstrumentState.Apply(values, Parameters);
            Reset();
            if (Mode == QuickSamplerMode.Recorder) {
                PrepareRecorder();
            }

            return loaded;
        }

        protected override void OnReset() {
            Recording = false;
            Compute();
        }

        protected override void ControlTick() => Compute();

        void Compute() {
            var p = Parameters;
            control.PitchBend = Bend * p[QuickSamplerParameters.BendRange];
        }

        /// <summary>The slice starts of the sample now, ending with its end (control thread or tests).</summary>
        public long[] SliceStarts() {
            var count = ComputeSlices();
            var list = new long[count];
            Array.Copy(slices, list, count);
            return list;
        }

        /// <summary>Fills <see cref="slices" /> with the slice starts and the end after them; the number of slices.</summary>
        int ComputeSlices() {
            var sample = Sample;
            if (sample is null || sample.Frames == 0) {
                return 0;
            }

            var p = Parameters;
            var (start, stop) = Bounds(sample);
            var mode = (int)p[QuickSamplerParameters.SliceMode];
            var count = 0;
            if (mode == 0 && sample.Transients.Length > 0) {
                slices[count++] = start;
                foreach (var t in sample.Transients) {
                    if (t > slices[count - 1] + 64 && t < stop && count < MaxSlices) {
                        slices[count++] = t;
                    }
                }
            } else {
                var parts = mode == 0 ? 16 : 4 << (mode - 1);
                for (var k = 0; k < parts; k++) {
                    slices[count++] = start + (stop - start) * k / parts;
                }
            }

            slices[count] = stop;
            return count;
        }

        (long Start, long Stop) Bounds(QuickSample sample) {
            var p = Parameters;
            var frames = sample.Frames;
            var start = (long)(p[QuickSamplerParameters.Start] / 100 * frames);
            var stop = (long)(p[QuickSamplerParameters.End] / 100 * frames);
            if (stop <= start) {
                stop = Math.Min(frames, start + 1);
            }

            return (Math.Clamp(start, 0, frames - 1), Math.Clamp(stop, 1, frames));
        }

        protected override void NoteOn(int note, int velocity) {
            var sample = Sample;
            if (sample is null || sample.Frames == 0 || Recording) {
                return;
            }

            var p = Parameters;
            var mode = Mode;
            var (start, stop) = Bounds(sample);
            var tune = p[QuickSamplerParameters.Tune] + p[QuickSamplerParameters.Fine] / 100.0;
            var loop = false;
            long loopStart = 0, loopEnd = 0;
            if (mode == QuickSamplerMode.Slice) {
                var k = note - (int)p[QuickSamplerParameters.SliceStartNote];
                var count = ComputeSlices();
                if (k < 0 || k >= count) {
                    return;
                }

                start = slices[k];
                stop = slices[k + 1];
            } else {
                if (p[QuickSamplerParameters.KeyFollow] >= 0.5f) {
                    tune += note - p[QuickSamplerParameters.RootNote];
                }

                if (mode != QuickSamplerMode.OneShot && p[QuickSamplerParameters.Loop] >= 0.5f) {
                    loopStart = (long)(p[QuickSamplerParameters.LoopStart] / 100 * sample.Frames);
                    loopEnd = (long)(p[QuickSamplerParameters.LoopEnd] / 100 * sample.Frames) - 1;
                    loopStart = Math.Clamp(loopStart, start, stop - 1);
                    loopEnd = Math.Clamp(loopEnd, loopStart, stop - 1);
                    loop = loopEnd - loopStart >= 16;
                }
            }

            var settings = mode == QuickSamplerMode.OneShot
                ? AdsrSettings.From(p[QuickSamplerParameters.Attack] * 0.001, 0, 0, 1, 0.005, SampleRate)
                : AdsrSettings.From(
                    p[QuickSamplerParameters.Attack] * 0.001,
                    0,
                    p[QuickSamplerParameters.Decay] * 0.001,
                    p[QuickSamplerParameters.Sustain] / 100.0,
                    p[QuickSamplerParameters.Release] * 0.001,
                    SampleRate
                );
            var voice = Allocate();
            voice.Start(
                sample,
                note,
                Pitch.Velocity(velocity, p[QuickSamplerParameters.VelocitySensitivity] / 100f),
                tune,
                start,
                stop,
                loop,
                loopStart,
                loopEnd,
                mode is QuickSamplerMode.OneShot or QuickSamplerMode.Slice,
                settings
            );
            Started(voice);
        }

        protected override void Render(Span<float> left, Span<float> right) {
            foreach (var voice in Voices) {
                if (voice.Active) {
                    voice.Render(left, right);
                }
            }
        }

        public void Feed(ReadOnlySpan<float> left, ReadOnlySpan<float> right) {
            var buffer = take;
            var armed = Parameters[QuickSamplerParameters.RecordArm] >= 0.5f;
            if (buffer is null || Mode != QuickSamplerMode.Recorder || !Prepared) {
                Recording = false;
                return;
            }

            if (!armed) {
                Recording = false;
                return;
            }

            var frames = Math.Min(left.Length, right.Length);
            var from = 0;
            if (!Recording) {
                var threshold = DecibelsToGain(Parameters[QuickSamplerParameters.RecordThreshold]);
                from = -1;
                for (var i = 0; i < frames; i++) {
                    if (Math.Abs(left[i]) >= threshold || Math.Abs(right[i]) >= threshold) {
                        from = i;
                        break;
                    }
                }

                if (from < 0) {
                    return;
                }

                // A take starts: what played goes silent, the take becomes the sample.
                foreach (var voice in Voices) {
                    voice.Kill();
                }

                Recording = true;
                buffer.Frames = 0;
                Path = null;
                Sample = buffer;
            }

            var room = buffer.Capacity - buffer.Frames;
            var count = Math.Min(frames - from, room);
            left.Slice(from, count).CopyTo(buffer.Left.AsSpan(QuickSample.PadBefore + buffer.Frames));
            right.Slice(from, count).CopyTo(buffer.Right.AsSpan(QuickSample.PadBefore + buffer.Frames));
            buffer.Frames += count;
            if (buffer.Frames >= buffer.Capacity) {
                Recording = false;
            }
        }
    }
}

/// <summary>The control-rate values every <see cref="QuickSamplerVoice" /> reads.</summary>
internal sealed class QuickSamplerControl {
    public double SampleRate;
    public double PitchBend;
    public float StealStep;
}

/// <summary>One voice of <see cref="QuickSampler" />: a stretch of the sample read with cubic interpolation.</summary>
internal sealed class QuickSamplerVoice(QuickSamplerControl control) : Voice {
    QuickSample? sample;
    AdsrSettings settings;
    Adsr amp;
    double position;
    double baseIncrement;
    double increment;
    long stop;
    long loopStart;
    long loopEnd;
    float gain;
    float fade = 1;
    bool looping;
    bool oneShot;
    int untilTick;
    double tune;

    public void Start(
        QuickSample s,
        int note,
        float velocityGain,
        double semitones,
        long from,
        long to,
        bool loop,
        long loopFrom,
        long loopTo,
        bool plays,
        AdsrSettings envelope
    ) {
        Begin(note);
        sample = s;
        position = from;
        stop = to;
        looping = loop;
        loopStart = loopFrom;
        loopEnd = loopTo;
        oneShot = plays;
        settings = envelope;
        gain = velocityGain;
        tune = semitones;
        baseIncrement = s.SampleRate / control.SampleRate;
        amp.Reset();
        amp.Trigger();
        fade = 1;
        untilTick = 0;
    }

    protected override void OnRelease() {
        if (oneShot) {
            return;
        }

        looping = false;
        amp.Release();
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        sample = null;
        fade = 1;
    }

    public void Render(Span<float> left, Span<float> right) {
        if (sample is not { } s) {
            Kill();
            return;
        }

        var l = s.Left;
        var r = s.Right;
        for (var i = 0; i < left.Length; i++) {
            if (untilTick == 0) {
                increment = baseIncrement * Math.Pow(2, (tune + control.PitchBend) / 12);
                untilTick = PolyInstrument<QuickSamplerVoice>.ControlInterval;
            }

            untilTick--;
            var index = (long)position;
            var t = (float)(position - index);
            float outLeft, outRight;
            if (!looping || index + 2 <= loopEnd) {
                var at = (int)index + QuickSample.PadBefore;
                outLeft = SamplerVoice.Cubic(l[at - 1], l[at], l[at + 1], l[at + 2], t);
                outRight = SamplerVoice.Cubic(r[at - 1], r[at], r[at + 1], r[at + 2], t);
            } else {
                var length = loopEnd - loopStart + 1;
                var a = Wrap(index - 1, length);
                var b = Wrap(index, length);
                var c = Wrap(index + 1, length);
                var d = Wrap(index + 2, length);
                outLeft = SamplerVoice.Cubic(l[a], l[b], l[c], l[d], t);
                outRight = SamplerVoice.Cubic(r[a], r[b], r[c], r[d], t);
            }

            var g = amp.Next(settings) * gain * fade;
            left[i] += outLeft * g;
            right[i] += outRight * g;
            position += increment;
            if (looping && position >= loopEnd + 1) {
                var length = loopEnd - loopStart + 1;
                position -= length * Math.Floor((position - loopEnd - 1) / length + 1);
            }

            if (amp.IsIdle || (!looping && position >= stop) || (Stolen && (fade -= control.StealStep) <= 0)) {
                Kill();
                return;
            }
        }
    }

    int Wrap(long index, long length) => (int)(index > loopEnd ? index - length : index) + QuickSample.PadBefore;
}
