using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Surround;
using Anode.Dsp.Spatial;
using Anode.Engine.Mixing;
using Anode.Engine.Spatial;
using Anode.Ipc;

namespace Anode.Engine;

// Inserts that change their strip's channel format (docs/12-phase-11.md, workstream A: the Down Mixer, Binaural
// Post-Processing). Walking a strip's inserts in order, each runs in the format the one before it left: an ordinary
// insert gets its multichannel instances for it (Phase 10), a format insert a stage prepared from it to what it
// outputs at its parameters, which the inserts after it, the fader, the sends and the output then carry. A stage is
// replaced whole (and the plan rebuilt) when the format at its slot or its parameters change; the old one is disposed
// once the plan without it is published.
public sealed partial class EngineController {
    readonly Dictionary<string, HrtfSet?> hrtfs = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets an insert ready for <paramref name="format" />, the format at its slot; returns the format after it
    ///     (its own for an ordinary insert).
    /// </summary>
    SpeakerFormat Prepare(
        InsertSlot slot,
        int trackId,
        SpeakerFormat format,
        string? state,
        List<IDisposable> replaced
    ) {
        if (slot.IsFormat) {
            return EnsureFormat(slot, trackId, format, state, replaced);
        }

        EnsureMulti(slot, trackId, format, state, replaced);
        return format;
    }

    /// <summary>A strip's inserts, in order, ready for its input format <paramref name="input" /> and what each leaves.</summary>
    void PrepareInserts(ChannelConfig config, int trackId, SpeakerFormat input, List<IDisposable> replaced) {
        var format = input;
        foreach (var insert in config.Inserts) {
            format = Prepare(insert.Slot, trackId, format, null, replaced);
        }
    }

    /// <summary>
    ///     A new format insert: its slot, with its parameters (its stage is prepared once the format at its slot is
    ///     known). One whose plugin cannot be made becomes an empty slot that passes the signal through, and is reported.
    /// </summary>
    InsertSlot BuildFormatInsert(int trackId, EngineInsert spec) {
        IFormatEffect? probe = null;
        try {
            probe = Plugins.CreateFormatEffect(spec.PluginId);
            if (probe is null) {
                Problem(trackId, spec.Id, spec.PluginId, $"No effect '{spec.PluginId}'.");
                return new(spec.Id, spec.PluginId, null);
            }

            var slot = InsertSlot.ForFormat(spec.Id, spec.PluginId, probe.Descriptor, spec.File);
            foreach (var (id, value) in spec.Parameters) {
                slot.Initialise(id, value);
            }

            slot.FormatState = spec.State;
            return slot;
        } catch (Exception e) when (IsInstrumentFailure(e)) {
            Problem(trackId, spec.Id, spec.PluginId, e.Message);
            return new(spec.Id, spec.PluginId, null);
        } finally {
            probe?.Dispose();
        }
    }

    /// <summary>
    ///     A format insert's stage for input <paramref name="format" />: the one it has when it was prepared for that
    ///     format and its parameters have not changed since, else a new one (the old goes to
    ///     <paramref name="replaced" />). Returns the format it outputs (the input's when it has no stage).
    /// </summary>
    SpeakerFormat EnsureFormat(
        InsertSlot slot,
        int trackId,
        SpeakerFormat format,
        string? state,
        List<IDisposable> replaced
    ) {
        var old = slot.Format;
        if (old is not null && old.In == format && !slot.FormatStale) {
            return old.Out;
        }

        if (state is not null) {
            slot.FormatState = state;
        }

        var next = BuildStage(slot, trackId, format);
        slot.FormatStale = false;
        slot.Format = next;
        if (old is not null) {
            replaced.Add(old);
        }

        return next?.Out ?? format;
    }

    /// <summary>
    ///     A new instance of a format insert, its state and parameters set, prepared from <paramref name="input" /> to
    ///     what it outputs (null, reported, when it cannot be).
    /// </summary>
    FormatStage? BuildStage(InsertSlot slot, int trackId, SpeakerFormat input) {
        IFormatEffect? effect = null;
        try {
            effect = Plugins.CreateFormatEffect(slot.PluginId)
                ?? throw new InvalidOperationException($"No effect '{slot.PluginId}'.");
            if (slot.FormatState is { } state && !effect.LoadState(Convert.FromBase64String(state))) {
                Log?.Invoke($"{slot.PluginId}: the saved state did not load; it starts from its defaults");
            }

            foreach (var (id, value) in slot.Values) {
                if (slot.IndexOf(id) is var index and >= 0) {
                    effect.SetParameter(index, (float)value);
                }
            }

            if (effect is BinauralInsert binaural) {
                binaural.UseHrtfs(
                    Hrtfs(slot.File ?? monitoring?.HrtfPath)
                    ?? throw new InvalidOperationException("Binaural Post-Processing needs an HRTF set.")
                );
            }

            var output = effect.OutputFormat(input);
            var from = SpeakerLayout.Of(input);
            var to = SpeakerLayout.Of(output);
            effect.Prepare(from, to, Options.SampleRate, AudioEngine.MaxFrames);
            return new(effect, input, output, Formats.Conversion(from, to, Engine.Spatial));
        } catch (Exception e) when (IsInstrumentFailure(e) || e is FormatException) {
            effect?.Dispose();
            Problem(trackId, slot.Id, slot.PluginId, e.Message);
            return null;
        }
    }

    /// <summary>
    ///     A format insert's parameter (<c>setInsertParameter</c>): structure, as its output format may follow it; the
    ///     stage is rebuilt and the plan published with it.
    /// </summary>
    bool SetFormatParameter(int trackId, InsertSlot slot, string parameterId, double value) {
        if (slot.IndexOf(parameterId) < 0 || !double.IsFinite(value)) {
            return false;
        }

        var root = Engine.Root;
        var channel = ChannelOf(root, trackId)!;
        slot.Values[parameterId] = value;
        slot.FormatStale = true;
        var replaced = new List<IDisposable>();
        PrepareInserts(channel.Config, trackId, channel.Config.InputFormat, replaced);
        PublishPlan();
        foreach (var old in replaced) {
            old.Dispose();
        }

        Hold(root, trackId, AutomationKind.Insert, slot.Id, parameterId);
        return true;
    }

    /// <summary>The HRTF set at <paramref name="path" />, read once (null: none, or it cannot be read).</summary>
    HrtfSet? Hrtfs(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return null;
        }

        if (!hrtfs.TryGetValue(path, out var set)) {
            try {
                set = HrtfSet.Load(path);
            } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
                Log?.Invoke($"the HRTF set {path} cannot be read: {e.Message}");
            }

            hrtfs[path] = set;
        }

        return set;
    }
}
