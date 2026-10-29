namespace Anode.Dsp.Instruments;

/// <summary>What tests and diagnostics read of a built-in polyphonic instrument's voices.</summary>
internal interface IPolyphonic {
    /// <summary>The most voices that sound at once (stolen voices fading out aside).</summary>
    int MaxVoices { get; }

    /// <summary>Voices sounding, stolen ones included.</summary>
    int ActiveVoices { get; }

    /// <summary>Whether a voice that has not been stolen plays <paramref name="note" />.</summary>
    bool IsSounding(int note);
}
