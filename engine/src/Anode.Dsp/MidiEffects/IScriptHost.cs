using Anode.Dsp.Instruments;

namespace Anode.Dsp.MidiEffects;

/// <summary>
///     A MIDI effect whose parameter list is defined at run time (Phase 7: Scripter's <c>PluginParameters</c>), not by
///     its <see cref="MidiEffectDescriptor" />, whose <c>Parameters</c> it leaves empty. The engine reads the list on
///     its control thread only: after <see cref="IScriptHost.Compile" />, and whenever
///     <see cref="TakeParametersChanged" /> says it changed (polled about every 50 ms). It then addresses the
///     parameters by id (<c>setInsertParameter</c>, <c>midiEffect</c> automation) and reports the list to the server
///     (<c>slotParameters</c>).
/// </summary>
public interface IRuntimeParameters {
    /// <summary>
    ///     Control thread only: the current list. Each parameter's <see cref="InstrumentParameter.Index" /> is what
    ///     <see cref="IMidiEffect.SetParameter" /> and <see cref="IMidiEffect.GetParameter" /> take; its
    ///     <see cref="InstrumentParameter.Id" /> is stable for "the same" parameter across changes of the list (Scripter:
    ///     derived from its name), so values, automation and Smart Control mappings follow it.
    /// </summary>
    IReadOnlyList<InstrumentParameter> RuntimeParameters { get; }

    /// <summary>
    ///     Control thread: whether the list changed since the previous call, clearing that. Any thread may raise it
    ///     (the audio thread, when a script calls <c>UpdatePluginParameters</c>); a compile need not, as the engine
    ///     reads the list after each <see cref="IScriptHost.Compile" />.
    /// </summary>
    bool TakeParametersChanged();
}

/// <summary>A script's error: what went wrong and the line of the script it happened on (0: unknown).</summary>
public readonly record struct ScriptError(string Message, int Line);

/// <summary>
///     A MIDI effect that runs a script (Phase 7: <c>anode.scripter</c>, docs/adr/0009-scripter.md). The engine owns its
///     life cycle around these members:
///     <list type="bullet">
///         <item>
///             A new source (<c>setScript</c>, or <c>EngineInsert.Source</c> in a model or a strip) is compiled into a
///             <b>new instance</b>, on the control thread, while the old instance keeps playing; the new one is then
///             swapped in between blocks (as <c>setChannel</c> swaps instances), taking over the old one's parameter
///             values by id, and the old one is disposed once the audio thread no longer uses it. Notes the old
///             instance left sounding are released at the swap.
///         </item>
///         <item>
///             <see cref="Faulted" /> (a throw, a budget overrun, out of arena, or a failed compile) bypasses the
///             slot: from the block the engine sees it, the block's input passes through unchanged (what
///             <see cref="IMidiEffect.Process" /> wrote in that block is discarded) and the notes the effect left
///             sounding are released. The engine reports <see cref="TakeError" />'s error as <c>pluginProblem</c>
///             (<c>line N: message</c>) and as a log line. A new source re-enables the slot.
///         </item>
///         <item>
///             Trace lines are drained on the control thread about every 50 ms and pushed to the server at most 50 a
///             second per slot; the rest are counted as dropped.
///         </item>
///     </list>
/// </summary>
public interface IScriptHost : IRuntimeParameters {
    /// <summary>
    ///     Control thread, after <see cref="IMidiEffect.Prepare" /> (and <see cref="IMidiEffect.LoadState" />, when
    ///     there is state) and before the instance first processes: compiles <paramref name="source" /> and runs its top
    ///     level, so <see cref="IRuntimeParameters.RuntimeParameters" /> is the script's. Returns null when it compiled,
    ///     else its error, and the instance is then <see cref="Faulted" />. Called at most once per instance.
    /// </summary>
    ScriptError? Compile(string source);

    /// <summary>
    ///     Read on the audio thread after each <see cref="IMidiEffect.Process" />: the script stopped (it threw, ran past
    ///     its budget or out of arena) or did not compile. Once true it stays true for the instance.
    /// </summary>
    bool Faulted { get; }

    /// <summary>
    ///     Control thread: the error that stopped the script while it ran (behind <see cref="Faulted" />), once; null when
    ///     there is none not yet taken. A compile's error is <see cref="Compile" />'s result, not this.
    /// </summary>
    ScriptError? TakeError();

    /// <summary>
    ///     Control thread: appends the trace lines queued since the previous call (oldest first) to
    ///     <paramref name="lines" />, and returns how many lines the trace ring dropped since then (it was full).
    /// </summary>
    int DrainTrace(List<string> lines);

    /// <summary>
    ///     Control thread, about every 50 ms while the instance is in a chain: Logic's <c>Idle</c> callback and the
    ///     garbage collector's turn. It may run while the audio thread is in <see cref="IMidiEffect.Process" />; the
    ///     effect serialises its own runtime without ever making the audio thread wait (it may skip the work, and do it
    ///     on a later call).
    /// </summary>
    void Idle();
}
