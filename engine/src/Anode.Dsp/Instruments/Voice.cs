namespace Anode.Dsp.Instruments;

/// <summary>
///     What <see cref="PolyInstrument{TVoice}" /> knows of a voice: the key that plays it, whether the key (or the
///     sustain pedal) still holds it, when it started and was released, and whether it was stolen and is fading out.
/// </summary>
internal abstract class Voice {
    /// <summary>The MIDI note it plays, or −1 when idle.</summary>
    public int Note { get; protected set; } = -1;

    public bool Active => Note >= 0;

    /// <summary>The key is down.</summary>
    public bool Gate { get; protected set; }

    /// <summary>The key is up but the sustain pedal holds the note.</summary>
    public bool Sustained { get; set; }

    /// <summary>Stolen: fading out fast, no longer counted against the polyphony.</summary>
    public bool Stolen { get; protected set; }

    /// <summary>Released by its key (or pedal), not stolen: in its release tail.</summary>
    public bool Released => Active && !Gate && !Sustained && !Stolen;

    public long StartOrder { get; set; }

    public long ReleaseOrder { get; set; }

    /// <summary>The key went up: release, or keep sounding when <paramref name="pedal" /> is down.</summary>
    public void KeyUp(bool pedal, long order) {
        if (!Gate) {
            return;
        }

        Gate = false;
        if (pedal) {
            Sustained = true;
        } else {
            ReleaseOrder = order;
            OnRelease();
        }
    }

    /// <summary>The pedal went up on a note it was holding.</summary>
    public void PedalUp(long order) {
        if (Sustained) {
            Sustained = false;
            ReleaseOrder = order;
            OnRelease();
        }
    }

    /// <summary>Stolen for another note: a fast fade, then idle.</summary>
    public void Steal() {
        Stolen = true;
        Gate = false;
        Sustained = false;
        OnSteal();
    }

    /// <summary>Silent and idle at once.</summary>
    public void Kill() {
        Note = -1;
        Gate = false;
        Sustained = false;
        Stolen = false;
        OnKill();
    }

    protected abstract void OnRelease();

    protected abstract void OnSteal();

    protected abstract void OnKill();

    /// <summary>Marks the voice started on <paramref name="note" /> with its key down.</summary>
    protected void Begin(int note) {
        Note = note;
        Gate = true;
        Sustained = false;
        Stolen = false;
    }
}
