namespace Anode.Dsp.Instruments.Sampler;

/// <summary>
///     <c>anode.sampler</c>: plays an SFZ instrument (the subset <see cref="SfzParser" /> reads) or a single WAV mapped
///     chromatically across the keyboard around its root (its <c>smpl</c> chunk's unity note, else middle C), with 32
///     voices. Samples are read whole into memory by <see cref="LoadFile" />. Round robin follows SFZ: every region
///     counts the note-ons that match its keys and velocities, and plays on those where the count modulo
///     <c>seq_length</c> is <c>seq_position − 1</c>.
/// </summary>
/// <remarks>
///     The parameters act on every region: Volume and Tune on top of each region's own, Attack and Release added to
///     each region's envelope times, and Velocity Sensitivity scaling each region's <c>amp_veltrack</c> (a velocity
///     curve of <c>(v/127)²</c> at 100 %). Pitch bend reaches ±2 semitones.
/// </remarks>
public sealed class AnodeSampler : IInstrument {
    public const string Id = "anode.sampler";
    public const int Polyphony = 32;
    public const int Volume = 0;
    public const int Tune = 1;
    public const int Attack = 2;
    public const int Release = 3;
    public const int VelocitySensitivity = 4;
    public const double BendRange = 2;

    readonly Engine engine = new();

    public static IReadOnlyList<InstrumentParameter> Parameters { get; } = [
        new(Volume, "volume", "Volume", -60, 12, 0, "dB", Macro: 5),
        new(Tune, "tune", "Tune", -24, 24, 0, "st", Macro: 1),
        new(Attack, "attack", "Attack", 0, 10_000, 0, "ms", Macro: 2),
        new(Release, "release", "Release", 0, 10_000, 0, "ms", Macro: 3),
        new(VelocitySensitivity, "velocitySensitivity", "Velocity Sensitivity", 0, 100, 100, "%", Macro: 4)
    ];

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Anode Sampler", "Anode", "1.0.0", Parameters, [".sfz", ".wav"]);

    public InstrumentDescriptor Descriptor => Info;

    /// <summary>The file loaded, or null.</summary>
    public string? File => engine.Program?.Path;

    internal bool IsSounding(int note) => engine.IsSounding(note);

    internal int ActiveVoices => engine.ActiveVoices;

    /// <summary>The regions sounding (tests).</summary>
    internal IEnumerable<SfzRegion> SoundingRegions => engine.SoundingRegions;

    public void Prepare(float sampleRate, int maxFrames) => engine.Prepare(sampleRate, maxFrames);

    public void SetParameter(int index, float value) => engine.SetParameter(index, value);

    public float GetParameter(int index) => engine.GetParameter(index);

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) =>
        engine.Process(events, left, right);

    public void Reset() => engine.Reset();

    public bool LoadFile(string path) => engine.LoadFile(path);

    public byte[]? SaveState() => engine.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => engine.LoadState(state);

    public void Dispose() => engine.Dispose();

    sealed class Engine : PolyInstrument<SamplerVoice> {
        /// <summary>The Tune smoothing's time constant.</summary>
        const double SmoothingSeconds = 0.01;

        readonly SamplerControl control;
        int[] sequence = [];
        float tune;
        double smoothing;

        public Engine() : this(new()) { }

        Engine(SamplerControl control) : base(Info, AnodeSampler.Polyphony, 8, _ => new(control), Volume) {
            this.control = control;
        }

        public SamplerProgram? Program { get; private set; }

        public IEnumerable<SfzRegion> SoundingRegions {
            get {
                var regions = new List<SfzRegion>();
                foreach (var voice in Voices) {
                    if (voice is { Active: true, Stolen: false, Zone: { } zone }) {
                        regions.Add(zone.Region);
                    }
                }

                return regions;
            }
        }

        public override void Prepare(float sampleRate, int maxFrames) {
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            smoothing = 1 - Math.Exp(-ControlInterval / (SmoothingSeconds * sampleRate));
            base.Prepare(sampleRate, maxFrames);
        }

        public override bool LoadFile(string path) {
            SamplerProgram program;
            try {
                program = SamplerProgram.Load(path);
            } catch (IOException) {
                return false;
            } catch (InvalidDataException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            } catch (ArgumentException) {
                return false;
            }

            Program = program;
            sequence = new int[program.Zones.Length];
            Reset();
            return true;
        }

        public override byte[]? SaveState() => InstrumentState.Write(Id, Program?.Path, Info.Parameters, Parameters);

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out var file, out var values)) {
                return false;
            }

            InstrumentState.Apply(values, Parameters);
            tune = Parameters[Tune];
            if (file is null) {
                Program = null;
                sequence = [];
                Reset();
                return true;
            }

            return file == Program?.Path || LoadFile(file);
        }

        protected override void OnReset() {
            tune = Parameters[Tune];
            Array.Clear(sequence);
            Compute();
        }

        protected override void NoteOn(int note, int velocity) {
            var program = Program;
            if (program is null) {
                return;
            }

            if (ActiveVoices == 0) {
                tune = Parameters[Tune]; // from silence, a note starts on the current tuning
                Compute();
            }

            var p = Parameters;
            var zones = program.Zones;
            for (var z = 0; z < zones.Length; z++) {
                var region = zones[z].Region;
                if (!region.Matches(note, velocity)) {
                    continue;
                }

                if (region.SeqLength > 1 && sequence[z]++ % region.SeqLength != region.SeqPosition - 1) {
                    continue;
                }

                var voice = Allocate();
                voice.Start(
                    zones[z],
                    note,
                    velocity,
                    p[VelocitySensitivity] / 100f,
                    p[Attack] * 0.001,
                    p[Release] * 0.001
                );
                Started(voice);
            }
        }

        protected override void Render(Span<float> left, Span<float> right) {
            foreach (var voice in Voices) {
                if (voice.Active) {
                    voice.Render(left, right);
                }
            }
        }

        protected override void ControlTick() {
            var target = Parameters[Tune];
            tune = ActiveVoices == 0 ? target : tune + (float)((target - tune) * smoothing);
            Compute();
        }

        void Compute() => control.PitchRatio = Math.Pow(2, (tune + Bend * BendRange) / 12);
    }
}
