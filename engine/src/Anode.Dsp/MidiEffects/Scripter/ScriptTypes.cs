namespace Anode.Dsp.MidiEffects.Scripter;

/// <summary>Why a Scripter instance stopped running its script.</summary>
public enum ScriptProblemKind {
    /// <summary>The native library (an_script) is missing: the instance passes MIDI through.</summary>
    Unavailable,

    /// <summary>A syntax error, or an exception the script did not catch.</summary>
    Error,

    /// <summary>A call ran past its time budget (ADR-0009: 25 % of the block period by default).</summary>
    Interrupted,

    /// <summary>The script's arena is full.</summary>
    OutOfMemory
}

/// <summary>
///     A script's problem, as the engine reports it (<c>pluginProblem</c>): what happened, the message and the
///     1-based line in the script (0 when unknown). While it stands, the instance is bypassed: its input passes
///     through.
/// </summary>
public sealed record ScriptProblem(ScriptProblemKind Kind, string Message, int Line) {
    public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
}

/// <summary>A <c>PluginParameters</c> entry's type.</summary>
public enum ScriptParameterKind {
    /// <summary><c>lin</c>: a slider from min to max, discrete when it has steps.</summary>
    Linear,

    /// <summary><c>log</c>: as <see cref="Linear" />, shown on a logarithmic scale.</summary>
    Logarithmic,

    /// <summary><c>menu</c>: the index of one of its value strings.</summary>
    Menu,

    /// <summary><c>checkbox</c>: 0 or 1.</summary>
    Checkbox,

    /// <summary><c>momentary</c>: a button, 1 while pressed.</summary>
    Momentary,

    /// <summary><c>text</c>: a label, with no value (always 0, never automated).</summary>
    Text
}

/// <summary>
///     One of a script's run-time parameters (its <c>PluginParameters</c>), at <paramref name="Index" /> in that array
///     (the index the script's <c>GetParameter</c>/<c>ParameterChanged</c> use). <paramref name="Name" /> is its id:
///     unique within the script (a repeated name gains " 2", " 3" …).
/// </summary>
public sealed record ScriptParameter(
    int Index,
    string Name,
    ScriptParameterKind Kind,
    float Min,
    float Max,
    float Default,
    int Steps,
    IReadOnlyList<string>? ValueNames,
    string Unit,
    bool Automatable
);

/// <summary>
///     A <c>TargetEvent</c> the script sent in the last block: its frame, target (an index into
///     <see cref="ScripterEffect.TargetNames" />) and value (0 … 1).
/// </summary>
public readonly record struct ScriptTargetEvent(int Frame, int Target, float Value);
