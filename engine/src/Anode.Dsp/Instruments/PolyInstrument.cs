namespace Anode.Dsp.Instruments;

/// <summary>
///     What the built-in instruments share: the parameters, MIDI handling (notes, sustain pedal, mod wheel, pitch bend,
///     all sound off and all notes off), voice allocation and stealing, the sample-accurate render loop, and the output
///     volume.
/// </summary>
/// <remarks>
///     <para>
///         The render loop splits each buffer at its events and at a fixed control grid of
///         <see cref="ControlInterval" /> samples counted from <see cref="Reset" />; at each grid point the instrument
///         updates its control-rate state (<see cref="ControlTick" />: smoothed parameters, modulators). So an event
///         takes effect on its own frame, and the output does not depend on how the host divides time into buffers.
///     </para>
///     <para>
///         Polyphony is <see cref="Polyphony" /> sounding voices. A note beyond it steals the voice released longest
///         ago, else the voice started longest ago; the stolen voice fades out over <see cref="StealFadeSeconds" /> in
///         one of a few spare voices, so stealing does not click.
///     </para>
/// </remarks>
internal abstract class PolyInstrument<TVoice> : IInstrument where TVoice : Voice {
    public const int ControlInterval = 16;
    public const double StealFadeSeconds = 0.005;

    /// <summary>The volume's smoothing time constant.</summary>
    const double VolumeSmoothingSeconds = 0.01;

    readonly TVoice[] voices;
    protected readonly ParameterBank Parameters;
    readonly int volumeIndex;
    int untilControl;
    long order;
    float volume;
    float volumeCoefficient;

    protected PolyInstrument(
        InstrumentDescriptor descriptor,
        int polyphony,
        int spareVoices,
        Func<int, TVoice> createVoice,
        int volumeIndex
    ) {
        Descriptor = descriptor;
        Polyphony = polyphony;
        Parameters = new(descriptor.Parameters);
        this.volumeIndex = volumeIndex;
        voices = new TVoice[polyphony + spareVoices];
        for (var i = 0; i < voices.Length; i++) {
            voices[i] = createVoice(i);
        }
    }

    public InstrumentDescriptor Descriptor { get; }

    /// <summary>Every voice, the spares for stolen ones included.</summary>
    protected ReadOnlySpan<TVoice> Voices => voices;

    public int Polyphony { get; }

    public float SampleRate { get; private set; }

    public int MaxFrames { get; private set; }

    public bool Prepared => SampleRate > 0;

    /// <summary>The sustain pedal (CC 64) is down.</summary>
    protected bool Pedal { get; private set; }

    /// <summary>The mod wheel (CC 1), 0 … 1.</summary>
    protected float ModWheel { get; private set; }

    /// <summary>The pitch bend, −1 … 1.</summary>
    protected float Bend { get; private set; }

    public virtual void Prepare(float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        SampleRate = sampleRate;
        MaxFrames = maxFrames;
        volumeCoefficient = (float)(1 - Math.Exp(-1 / (VolumeSmoothingSeconds * sampleRate)));
        Reset();
    }

    public void SetParameter(int index, float value) => Parameters.Set(index, value);

    public float GetParameter(int index) => Parameters.Get(index);

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) {
        left.Clear();
        right.Clear();
        var frames = Math.Min(left.Length, right.Length);
        if (!Prepared) {
            return;
        }

        var target = DecibelsToGain(Parameters[volumeIndex]);
        if (ActiveVoices == 0) {
            volume = target; // nothing sounds: the volume can jump
        }

        var position = 0;
        var next = 0;
        while (position < frames) {
            while (next < events.Length && Math.Clamp(events[next].Frame, 0, frames - 1) <= position) {
                Handle(events[next++]);
            }

            if (untilControl == 0) {
                ControlTick();
                untilControl = ControlInterval;
            }

            var end = Math.Min(frames, position + untilControl);
            if (next < events.Length) {
                end = Math.Min(end, Math.Clamp(events[next].Frame, 0, frames - 1));
            }

            var count = end - position;
            Render(left.Slice(position, count), right.Slice(position, count));
            untilControl -= count;
            position = end;
        }

        for (var i = 0; i < frames; i++) {
            volume += (target - volume) * volumeCoefficient;
            left[i] *= volume;
            right[i] *= volume;
        }
    }

    public void Reset() {
        foreach (var voice in Voices) {
            voice.Kill();
        }

        // Stop and locate start from a clean slate: no pedal, wheel or bend left over from before.
        Pedal = false;
        ModWheel = 0;
        Bend = 0;
        untilControl = 0;
        volume = DecibelsToGain(Parameters[volumeIndex]);
        OnReset();
    }

    public abstract bool LoadFile(string path);

    public abstract byte[]? SaveState();

    public abstract bool LoadState(ReadOnlySpan<byte> state);

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }

    /// <summary>Whether a voice that is not stolen plays <paramref name="note" /> (tests and diagnostics).</summary>
    public bool IsSounding(int note) {
        foreach (var voice in Voices) {
            if (voice.Active && !voice.Stolen && voice.Note == note) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Voices sounding, stolen ones included.</summary>
    public int ActiveVoices {
        get {
            var count = 0;
            foreach (var voice in Voices) {
                if (voice.Active) {
                    count++;
                }
            }

            return count;
        }
    }

    public static float DecibelsToGain(float db) => MathF.Pow(10, db / 20);

    /// <summary>Control-rate work at every grid point.</summary>
    protected abstract void ControlTick();

    /// <summary>Adds every active voice's next <c>left.Length</c> samples into the buffers.</summary>
    protected abstract void Render(Span<float> left, Span<float> right);

    /// <summary>A key went down (velocity 1 … 127).</summary>
    protected abstract void NoteOn(int note, int velocity);

    /// <summary>After <see cref="Reset" /> has killed every voice: reset modulators and snap smoothers.</summary>
    protected abstract void OnReset();

    /// <summary>Marks <paramref name="voice" /> as started now, for the stealing order.</summary>
    protected void Started(TVoice voice) => voice.StartOrder = ++order;

    /// <summary>
    ///     A voice for a new note: an idle one while fewer than <see cref="Polyphony" /> sound, else the steal victim's
    ///     slot is freed by fading it out and an idle spare (or, with none left, the quietest stolen voice) is used.
    /// </summary>
    protected TVoice Allocate() {
        var sounding = 0;
        foreach (var voice in Voices) {
            if (voice.Active && !voice.Stolen) {
                sounding++;
            }
        }

        if (sounding >= Polyphony) {
            Victim().Steal();
        }

        TVoice? stolen = null;
        foreach (var voice in Voices) {
            if (!voice.Active) {
                return voice;
            }

            if (voice.Stolen && (stolen is null || voice.StartOrder < stolen.StartOrder)) {
                stolen = voice;
            }
        }

        // Every spare is still fading: cut the stolen voice that started first.
        stolen ??= Voices[0];
        stolen.Kill();
        return stolen;
    }

    /// <summary>The voice released longest ago, else the one started longest ago.</summary>
    TVoice Victim() {
        TVoice? released = null;
        TVoice? oldest = null;
        foreach (var voice in Voices) {
            if (!voice.Active || voice.Stolen) {
                continue;
            }

            if (voice.Released && (released is null || voice.ReleaseOrder < released.ReleaseOrder)) {
                released = voice;
            }

            if (oldest is null || voice.StartOrder < oldest.StartOrder) {
                oldest = voice;
            }
        }

        return released ?? oldest!;
    }

    void Handle(in MidiEvent e) {
        var status = e.Status & 0xF0;
        switch (status) {
            case 0x90 when e.Data2 > 0:
                NoteOn(e.Data1 & 0x7F, e.Data2 & 0x7F);
                break;
            case 0x80 or 0x90:
                NoteOff(e.Data1 & 0x7F);
                break;
            case 0xB0:
                Controller(e.Data1, e.Data2 & 0x7F);
                break;
            case 0xE0:
                var bend = ((e.Data2 & 0x7F) << 7 | (e.Data1 & 0x7F)) - 8192;
                Bend = bend >= 0 ? bend / 8191f : bend / 8192f;
                break;
        }
    }

    void NoteOff(int note) {
        foreach (var voice in Voices) {
            if (voice.Active && voice.Note == note && voice.Gate) {
                voice.KeyUp(Pedal, ++order);
            }
        }
    }

    void Controller(int controller, int value) {
        switch (controller) {
            case 1:
                ModWheel = value / 127f;
                break;
            case 64:
                var down = value >= 64;
                if (Pedal && !down) {
                    foreach (var voice in Voices) {
                        voice.PedalUp(++order);
                    }
                }

                Pedal = down;
                break;
            case 120: // all sound off
                foreach (var voice in Voices) {
                    voice.Kill();
                }

                break;
            case 121: // reset all controllers
                ModWheel = 0;
                Bend = 0;
                Controller(64, 0);
                break;
            case 123: // all notes off: the pedal lets go of them too
                Pedal = false;
                foreach (var voice in Voices) {
                    if (voice.Active) {
                        voice.KeyUp(false, ++order);
                        voice.PedalUp(++order);
                    }
                }

                break;
        }
    }
}
