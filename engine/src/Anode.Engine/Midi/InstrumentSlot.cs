using Anode.Dsp.Instruments;

namespace Anode.Engine.Midi;

/// <summary>
///     A track's instrument as the engine runs it: the prepared instance, the plugin and file it was built from, the
///     parameter values the control thread last set (by parameter id) and the queue that carries parameter changes to
///     the audio thread, which applies them before rendering. A new instrument is a new slot, swapped in whole (RCU);
///     the control thread disposes the old one once the audio thread has let go of it.
/// </summary>
public sealed class InstrumentSlot : IDisposable {
    readonly Dictionary<string, int> indexById = new(StringComparer.Ordinal);

    public InstrumentSlot(IInstrument instrument, string pluginId, string? file, string? loadedState) {
        Instrument = instrument;
        PluginId = pluginId;
        File = file;
        LoadedState = loadedState;
        foreach (var p in instrument.Descriptor.Parameters) {
            indexById[p.Id] = p.Index;
            Values[p.Id] = p.Default;
        }

        Parameters = new(Math.Max(256, 2 * indexById.Count));
    }

    public IInstrument Instrument { get; }

    public string PluginId { get; }

    /// <summary>The file it loaded (absolute), if any.</summary>
    public string? File { get; }

    /// <summary>The state (base64) it was built with, if any.</summary>
    public string? LoadedState { get; }

    /// <summary>Control thread: each parameter's value as last set, by parameter id.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Parameter changes on their way to the audio thread.</summary>
    public SpscChannel<ParameterChange> Parameters { get; }

    /// <summary>The parameter's index, or −1 when the instrument has no parameter with that id.</summary>
    public int IndexOf(string parameterId) => indexById.GetValueOrDefault(parameterId, -1);

    /// <summary>
    ///     Control thread, before the slot is published: sets the parameter on the instance directly. False for an
    ///     unknown id.
    /// </summary>
    public bool Initialise(string parameterId, double value) {
        var index = IndexOf(parameterId);
        if (index < 0 || !double.IsFinite(value)) {
            return false;
        }

        Values[parameterId] = value;
        Instrument.SetParameter(index, (float)value);
        return true;
    }

    /// <summary>Control thread, once published: queues the change for the audio thread. False for an unknown id.</summary>
    public bool Set(string parameterId, double value) {
        var index = IndexOf(parameterId);
        if (index < 0 || !double.IsFinite(value)) {
            return false;
        }

        Values[parameterId] = value;
        return Parameters.TryEnqueue(new(index, (float)value));
    }

    /// <summary>Audio thread: applies the queued parameter changes.</summary>
    public void ApplyParameters() {
        while (Parameters.TryDequeue(out var change)) {
            Instrument.SetParameter(change.Index, change.Value);
        }
    }

    public void Dispose() => Instrument.Dispose();
}
