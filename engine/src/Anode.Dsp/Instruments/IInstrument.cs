using Anode.Dsp.Transport;

namespace Anode.Dsp.Instruments;

/// <summary>A MIDI message <paramref name="Frame" /> frames into the buffer being rendered.</summary>
public readonly record struct MidiEvent(int Frame, byte Status, byte Data1, byte Data2);

/// <summary>
///     A parameter as an instrument numbers it (<paramref name="Index" />) and names it (<paramref name="Id" />, stable
///     across versions, stored in projects). Values are in the parameter's own unit, <paramref name="Min" /> …
///     <paramref name="Max" />; <paramref name="Steps" /> &gt; 0 makes it discrete. <paramref name="Macro" /> is its
///     macro hint (Phase 6): automatic Smart Controls mapping takes a plugin's hinted parameters in ascending order
///     (null: not a macro). <paramref name="Kind" /> is the control that shows it (null: told by its shape, see
///     <see cref="ShownAs" />).
/// </summary>
public sealed record InstrumentParameter(
    int Index,
    string Id,
    string Name,
    float Min,
    float Max,
    float Default,
    string Unit,
    int Steps = 0,
    IReadOnlyList<string>? ValueNames = null,
    int? Macro = null,
    InstrumentParameterKind? Kind = null
) {
    /// <summary>
    ///     The control that shows it: its <see cref="Kind" />, else a menu when its values are named, a checkbox when it
    ///     is a 0…1 switch of two values, and a slider otherwise.
    /// </summary>
    public InstrumentParameterKind ShownAs =>
        Kind ?? InstrumentParameterKinds.Of(Steps, Min, Max, ValueNames);
}

/// <summary>The control a parameter shows as (a Scripter <c>PluginParameters</c> type, or a built-in's shape).</summary>
public enum InstrumentParameterKind {
    Slider,
    Menu,
    Checkbox,
    Momentary,
    Text
}

public static class InstrumentParameterKinds {
    /// <summary>A parameter's kind by its shape: named values a menu, a two-valued 0…1 switch a checkbox, else a slider.</summary>
    public static InstrumentParameterKind Of(int steps, double min, double max, IReadOnlyList<string>? valueNames) =>
        valueNames is { Count: > 0 } ? InstrumentParameterKind.Menu
        : steps == 1 && min == 0 && max == 1 ? InstrumentParameterKind.Checkbox
        : InstrumentParameterKind.Slider;

    /// <summary>As the IPC and the REST contracts spell it: <c>slider</c>, <c>menu</c>, <c>checkbox</c>, …</summary>
    public static string Name(InstrumentParameterKind kind) =>
        kind switch {
            InstrumentParameterKind.Menu => "menu",
            InstrumentParameterKind.Checkbox => "checkbox",
            InstrumentParameterKind.Momentary => "momentary",
            InstrumentParameterKind.Text => "text",
            _ => "slider"
        };
}

/// <summary>What an instrument is: its stable id (<c>anode.synth</c>), names, parameters and loadable file types.</summary>
public sealed record InstrumentDescriptor(
    string Id,
    string Name,
    string Vendor,
    string Version,
    IReadOnlyList<InstrumentParameter> Parameters,
    IReadOnlyList<string> FileTypes
);

/// <summary>
///     A software instrument: MIDI in, stereo audio out. Built-in instruments implement it directly; the engine wraps
///     hosted plugins (CLAP) in it too.
///     <para>
///         Threading: <see cref="Prepare" />, <see cref="LoadFile" />, <see cref="LoadState" />,
///         <see cref="SaveState" /> and <see cref="IDisposable.Dispose" /> run on the engine's control thread while the
///         instance is not processing (the engine prepares a new instance and swaps it in). <see cref="SetParameter" />,
///         <see cref="Process" /> and <see cref="Reset" /> run on the audio thread and must not allocate, lock or block.
///     </para>
/// </summary>
public interface IInstrument : IDisposable {
    InstrumentDescriptor Descriptor { get; }

    /// <summary>Allocates everything processing needs, for buffers of up to <paramref name="maxFrames" />.</summary>
    void Prepare(float sampleRate, int maxFrames);

    /// <summary>Sets parameter <paramref name="index" /> (clamped to its range); smoothing is the instrument's job.</summary>
    void SetParameter(int index, float value);

    /// <summary>The current value of parameter <paramref name="index" />.</summary>
    float GetParameter(int index);

    /// <summary>
    ///     Renders <c>left.Length</c> frames (equal to <c>right.Length</c>, at most the prepared maximum) into the
    ///     buffers, overwriting them, playing <paramref name="events" /> (sorted by frame) at their frames.
    /// </summary>
    void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right);

    /// <summary>
    ///     The transport for the block about to be rendered (docs/07-phase-6.md), on the audio thread once per block
    ///     before <see cref="Process" />. Tempo-synced instruments keep what they need of it; the default ignores it.
    /// </summary>
    void SetTransport(in TransportContext transport) { }

    /// <summary>Silences every voice at once (stop, locate): no release tails.</summary>
    void Reset();

    /// <summary>Loads a file of one of <see cref="InstrumentDescriptor.FileTypes" />; false when it cannot.</summary>
    bool LoadFile(string path);

    /// <summary>State beyond the parameters (null when there is none), opaque to the engine.</summary>
    byte[]? SaveState();

    /// <summary>Restores what <see cref="SaveState" /> returned; false when it cannot.</summary>
    bool LoadState(ReadOnlySpan<byte> state);
}
