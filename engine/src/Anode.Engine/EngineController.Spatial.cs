using Anode.Dsp.Effects;
using Anode.Dsp.Spatial;
using Anode.Engine.Mixing;
using Anode.Engine.Spatial;
using Anode.Ipc;

namespace Anode.Engine;

// Surround on the control thread (docs/11-phase-10.md, workstream A): strips' formats and panners into the channel
// configurations and plans, the extra instances and delays of inserts on multichannel strips, the monitoring the
// device stage applies (rebuilt when the master's format changes) and the listen-in tap.
public sealed partial class EngineController {
    SetMonitoringMessage? monitoring;
    SpeakerFormat? monitoredFormat;

    /// <summary>
    ///     The spatial DSP the engine uses: workstream D's (<see cref="DspSpatial" />) unless a test hands it doubles. Set
    ///     before the first model that needs it.
    /// </summary>
    public ISpatialFactory SpatialFactory {
        get => Engine.Spatial.Factory;
        set {
            lock (gate) {
                Engine.Spatial = new(value, Options.SampleRate) { Log = message => Log?.Invoke(message) };
                monitoredFormat = null;
                ApplyMonitoring();
            }
        }
    }

    /// <summary>
    ///     Monitoring (<c>setMonitoring</c>): binaural through the HRTF file, or a down-mix, and the 0-based speaker map;
    ///     prepared here and swapped into the device stage. Throws <see cref="ArgumentException" /> for binaural without
    ///     an HRTF file and <see cref="InvalidDataException" /> or <see cref="IOException" /> when the file cannot be read
    ///     (the previous monitoring stays).
    /// </summary>
    public void SetMonitoring(SetMonitoringMessage message) {
        lock (gate) {
            if (message.Binaural && string.IsNullOrEmpty(message.HrtfPath)) {
                throw new ArgumentException("Binaural monitoring needs an HRTF file.", nameof(message));
            }

            var previous = monitoring;
            monitoring = message;
            monitoredFormat = null;
            try {
                ApplyMonitoring(true);
            } catch {
                monitoring = previous;
                monitoredFormat = null;
                ApplyMonitoring();
                throw;
            }
        }
    }

    /// <summary>Whether the callback writes the master tap ring (<c>setMasterTap</c>).</summary>
    public void SetMasterTap(bool enabled) => Engine.SetMasterTap(enabled && Engine.MasterTap is not null);

    /// <summary>
    ///     Builds the device stage's monitoring for the current master's format, when it or the settings changed (the
    ///     binaural renderer and the down-mix are prepared here, off the audio thread). What cannot be made throws
    ///     <see cref="InvalidOperationException" /> with <paramref name="strict" /> (a <c>setMonitoring</c>); otherwise
    ///     (a new master format) it is logged and the master is sent as it is.
    /// </summary>
    void ApplyMonitoring(bool strict = false) {
        var format = Engine.Root.Mix.MasterFormat;
        if (monitoredFormat == format) {
            return;
        }

        monitoredFormat = format;
        if (monitoring is not { } message) {
            Engine.Monitoring = null;
            return;
        }

        var layout = SpeakerLayout.Of(format);
        var spatial = Engine.Spatial;
        IBinaural? binaural = null;
        IDownmix? downmix = null;
        var channels = layout.ChannelCount;
        if (message.Binaural) {
            binaural = spatial.Try(
                () => spatial.Factory.CreateBinaural(message.HrtfPath!, layout, Options.SampleRate, AudioEngine.MaxFrames),
                $"binaural monitoring through {message.HrtfPath}"
            );
            if (binaural is null && strict) {
                throw new InvalidOperationException($"Binaural monitoring through {message.HrtfPath} is not available.");
            }

            channels = binaural is null ? channels : 2;
        } else if (message.DownmixTo is { } to
                   && SpeakerLayout.Of(Formats.Of(to)) is var target
                   && target.ChannelCount < layout.ChannelCount) {
            downmix = spatial.Try(() => spatial.Factory.CreateDownmix(layout, target), "the monitoring down-mix");
            if (downmix is null && strict) {
                throw new InvalidOperationException($"The down-mix to {to} is not available.");
            }

            channels = downmix is null ? channels : target.ChannelCount;
        }

        var setup = new MonitorSetup {
            From = format, Binaural = binaural, Downmix = downmix, Channels = channels, Map = [..message.SpeakerMap]
        };
        Engine.Monitoring = setup.IsIdentity ? null : setup;
    }

    /// <summary>
    ///     Gives an insert on a strip of <paramref name="format" /> what it needs there: on a multichannel strip a
    ///     <see cref="MultiInsert" /> for that format (a per-channel effect's extra instances built like the insert's own,
    ///     with its state, file and current parameters); none on a mono or stereo strip. What it replaces goes to
    ///     <paramref name="replaced" />.
    /// </summary>
    void EnsureMulti(
        InsertSlot slot,
        int trackId,
        SpeakerFormat format,
        string? state,
        List<IDisposable> replaced
    ) {
        var old = slot.Multi;
        if (SpeakerLayout.Of(format).ChannelCount <= 2 || slot.Effect is null) {
            if (old is not null) {
                slot.Multi = null;
                replaced.Add(old);
            }

            return;
        }

        if (old?.Format == format) {
            return;
        }

        var policy = MultichannelEffects.PolicyOf(slot.PluginId);
        var pairs = Formats.Pairs(SpeakerLayout.Of(format)).Length;
        var extras = new IAudioEffect?[policy == MultichannelPolicy.PerChannel ? pairs - 1 : 0];
        for (var p = 0; p < extras.Length; p++) {
            extras[p] = BuildExtra(slot, trackId, state);
        }

        slot.Multi = new(format, policy, extras);
        if (old is not null) {
            replaced.Add(old);
        }
    }

    /// <summary>One more instance of a per-channel insert, as the insert's own was built (null when it cannot be).</summary>
    IAudioEffect? BuildExtra(InsertSlot slot, int trackId, string? state) {
        IAudioEffect? effect = null;
        try {
            effect = Plugins.CreateEffect(
                slot.PluginId,
                new(
                    trackId,
                    slot.Id,
                    slot.PluginId,
                    Options.SampleRate,
                    AudioEngine.MaxFrames,
                    new PluginCallbacks(this, trackId, slot.Id, slot.PluginId)
                )
            );
            if (effect is null) {
                return null;
            }

            effect.Prepare(Options.SampleRate, AudioEngine.MaxFrames);
            if (state is not null) {
                effect.LoadState(Convert.FromBase64String(state));
            }

            if (slot.File is not null && effect is IFileEffect files) {
                files.LoadFile(slot.File);
            }

            foreach (var (id, value) in slot.Values) {
                if (slot.IndexOf(id) is var index and >= 0) {
                    effect.SetParameter(index, (float)value);
                }
            }

            return effect;
        } catch (Exception e) when (IsInstrumentFailure(e)) {
            effect?.Dispose();
            Log?.Invoke($"track {trackId}: {slot.PluginId} (insert {slot.Id}): a surround channel's instance: {e.Message}");
            return null;
        }
    }

    /// <summary>
    ///     A strip's formats or panner mode changed through <c>setStrip</c> (no inserts in it): the configuration takes
    ///     them, its inserts are prepared for the format, and the plan is rebuilt. False when nothing structural changed.
    /// </summary>
    bool ApplyFormats(ChannelState channel, int trackId, EngineStrip strip, List<IDisposable> replaced) {
        var config = channel.Config;
        var input = Formats.Of(strip.InputFormat);
        var output = Formats.Of(strip.OutputFormat);
        var mode = strip.Panner?.Mode ?? EnginePannerMode.Stereo;
        if (config.InputFormat == input && config.OutputFormat == output && config.PannerMode == mode) {
            return false;
        }

        foreach (var insert in config.Inserts) {
            EnsureMulti(insert.Slot, trackId, input, null, replaced);
        }

        channel.Config = config with { InputFormat = input, OutputFormat = output, PannerMode = mode };
        return true;
    }

    /// <summary>A control move of a panner parameter holds its automation, as a pan move does.</summary>
    void HoldPanner(ModelRoot root, int trackId, EnginePanner? before, EnginePanner? after) {
        if (after is null) {
            return;
        }

        var s0 = before?.Surround;
        var s1 = after.Surround;
        Changed(AutomationKind.Surround, "angle", s0?.Angle, s1.Angle);
        Changed(AutomationKind.Surround, "diversity", s0?.Diversity, s1.Diversity);
        Changed(AutomationKind.Surround, "elevation", s0?.Elevation, s1.Elevation);
        Changed(AutomationKind.Surround, "centre", s0?.CentreGain, s1.CentreGain);
        Changed(AutomationKind.Surround, "lfe", s0?.LfeGain, s1.LfeGain);
        Changed(AutomationKind.Surround, "spread", s0?.Spread, s1.Spread);
        var o0 = before?.Object;
        var o1 = after.Object;
        Changed(AutomationKind.Object, "azimuth", o0?.Azimuth, o1.Azimuth);
        Changed(AutomationKind.Object, "elevation", o0?.Elevation, o1.Elevation);
        Changed(AutomationKind.Object, "distance", o0?.Distance, o1.Distance);
        Changed(AutomationKind.Object, "size", o0?.Size, o1.Size);

        void Changed(AutomationKind kind, string parameter, float? was, float now) {
            if (was is { } w && w != now) {
                Hold(root, trackId, kind, null, parameter);
            }
        }
    }

    /// <summary>
    ///     Summing points wider than stereo get their channels, objects their buffers, before a plan with them is
    ///     published.
    /// </summary>
    void EnsureBuffers(MixPlan plan) {
        foreach (var d in plan.UsedDestinations) {
            if (plan.DestinationWidths[d] > 2) {
                Engine.EnsureDestination(d, plan.DestinationWidths[d]);
            }
        }

        Engine.EnsureObjects(plan.Objects.Length);
    }

    /// <summary>
    ///     The channels a render target of <paramref name="trackId" /> gets: a stem has its destination's (an object's
    ///     its own one or two), a master render the master's (or what the monitoring sends).
    /// </summary>
    static (int Channels, SpeakerFormat? Format) StemFormat(MixPlan plan, int trackId) {
        foreach (var node in plan.Order) {
            if (node.TrackId != trackId) {
                continue;
            }

            return node.OutputEdge switch {
                null when node.Width > 2 => (node.Width, node.Format),
                null or { Kind: EdgeKind.Stereo or EdgeKind.DownmixStereo } => (2, null),
                { Kind: EdgeKind.Mono } => (1, null),
                { Kind: EdgeKind.Object } => (Math.Min(node.Width, 2), null),
                var edge => (edge.Width, edge.Format)
            };
        }

        return (2, null);
    }
}
