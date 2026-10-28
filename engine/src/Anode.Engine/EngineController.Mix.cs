using Anode.Dsp.Effects;
using Anode.Engine.Mixing;
using Anode.Engine.Plugins;
using Anode.Ipc;
using Anode.Plugins;

namespace Anode.Engine;

// The mixer on the control thread (docs/05-phase-4.md, workstream A): channel structures (setChannel), keeping insert
// instances by id; insert parameters, send levels and mixer settings; the plan the audio thread runs, rebuilt and
// published with the model (RCU) whenever the structure, the tracks or the automation targets change; delay lines for
// compensation; plugin editors; and what hosted plugins report (parameterChanged, pluginProblem).
public sealed partial class EngineController {
    /// <summary>The smallest delay line a channel gets once any path has latency, in frames.</summary>
    const int MinDelayFrames = 2048;

    Timer? maintenance;

    /// <summary>A plugin's own editor changed a parameter (any thread): the host relays it to the server.</summary>
    public Action<ParameterChangedMessage>? ParameterChanged { get; set; }

    /// <summary>A plugin failed to load or crashed and is bypassed (any thread): the host relays it to the server.</summary>
    public Action<PluginProblemMessage>? PluginProblem { get; set; }

    /// <summary>
    ///     A channel's structure changed: inserts (kept by id while their plugin is the same), bypass, sidechains,
    ///     sends and output; also its fader. −1 is the master. False for an unknown track.
    /// </summary>
    public bool SetChannel(int trackId, EngineStrip strip) {
        lock (gate) {
            var channel = ChannelOf(Engine.Root, trackId);
            if (channel is null) {
                return false;
            }

            SetFader(trackId, channel.Strip, strip);
            var replaced = new List<InsertSlot>();
            ApplyChannel(channel, trackId, strip, replaced);
            PublishPlan();
            foreach (var slot in replaced) {
                slot.Dispose(); // the swap's grace period covered them
            }

            return true;
        }
    }

    /// <summary>Queues a change of an insert's parameter; false for an unknown track, insert or parameter.</summary>
    public bool SetInsertParameter(int trackId, string insertId, string parameterId, double value) {
        lock (gate) {
            var root = Engine.Root;
            if (FindInsert(root, trackId, insertId) is not { } slot || !slot.Set(parameterId, value)) {
                return false;
            }

            Hold(root, trackId, AutomationKind.Insert, insertId, parameterId);
            GrowDelays(root.Mix);
            return true;
        }
    }

    /// <summary>A send's level and pan; false for an unknown track or send.</summary>
    public bool SetSendLevel(int trackId, string sendId, float gain, float pan) {
        lock (gate) {
            var root = Engine.Root;
            var channel = ChannelOf(root, trackId);
            var send = channel?.Config.Sends.FirstOrDefault(s => s.Slot.Id == sendId);
            if (send is null) {
                return false;
            }

            if (send.Slot.Gain != gain) {
                Hold(root, trackId, AutomationKind.Send, sendId);
            }

            send.Slot.Set(gain, pan);
            return true;
        }
    }

    /// <summary>
    ///     Delay compensation (<c>all</c>, <c>tracks</c>, <c>off</c>) and low-latency mode with its limit in
    ///     milliseconds. Throws <see cref="ArgumentException" /> for another mode.
    /// </summary>
    public void SetMixer(string pdc, bool lowLatency, double lowLatencyLimitMs) {
        var mode = pdc switch {
            "all" => DelayCompensation.All,
            "tracks" => DelayCompensation.Tracks,
            "off" => DelayCompensation.Off,
            _ => throw new ArgumentException($"Unknown delay compensation '{pdc}'.", nameof(pdc))
        };
        var limit = double.IsFinite(lowLatencyLimitMs) ? Math.Max(0, lowLatencyLimitMs) : 0;
        lock (gate) {
            Engine.SetMixer(mode, lowLatency, (int)Math.Round(limit * Options.SampleRate / 1000));
            GrowDelays(Engine.Root.Mix);
        }
    }

    /// <summary>
    ///     Opens or closes a plugin's own editor: insert <paramref name="insertId" />, or the track's instrument when
    ///     null. False when there is no such plugin or it has no editor.
    /// </summary>
    public bool OpenPluginEditor(int trackId, string? insertId, bool open) {
        lock (gate) {
            var root = Engine.Root;
            object? instance = insertId is null
                ? root.Tracks.FirstOrDefault(t => t.Id == trackId)?.Instrument?.Instrument
                : FindInsert(root, trackId, insertId)?.Effect;
            return instance switch {
                IPluginEditor editor => editor.SetEditorOpen(open),
                IHostedPlugin { HasEditor: true } hosted => hosted.OpenEditor(open) is var window
                    && (open ? window > 0 : window >= 0),
                _ => false
            };
        }
    }

    /// <summary>Every insert's opaque state (base64, null when it keeps none), keyed by track and insert id.</summary>
    List<InstrumentState> InsertStates(ModelRoot root) {
        var states = new List<InstrumentState>();
        foreach (var (trackId, channel) in Channels(root)) {
            foreach (var insert in channel.Config.Inserts) {
                if (insert.Slot.Effect is not { } effect) {
                    continue;
                }

                string? state = null;
                try {
                    state = effect.SaveState() is { } bytes ? Convert.ToBase64String(bytes) : null;
                } catch (Exception e) when (IsInstrumentFailure(e)) {
                    Log?.Invoke($"track {trackId}: saving insert {insert.Slot.Id}'s state failed: {e.Message}");
                }

                states.Add(new(trackId, state, insert.Slot.Id));
            }
        }

        return states;
    }

    /// <summary>
    ///     Every insert's reading that is a number (track −1: the master), read without the gate: the published model
    ///     and each channel's configuration are swapped whole, and a reading is a single float the audio thread writes.
    /// </summary>
    public List<InsertReadingEntry> InsertReadings() {
        var readings = new List<InsertReadingEntry>();
        foreach (var (trackId, channel) in Channels(Engine.Root)) {
            foreach (var insert in channel.Config.Inserts) {
                if (insert.Slot.Effect is { } effect
                    && !insert.Slot.Failed
                    && effect.Reading is var value
                    && !float.IsNaN(value)) {
                    readings.Add(new(trackId, insert.Slot.Id, value));
                }
            }
        }

        return readings;
    }

    IEnumerable<(int TrackId, ChannelState Channel)> Channels(ModelRoot root) {
        foreach (var track in root.Tracks) {
            yield return (track.Id, track.Channel);
        }

        yield return (-1, Engine.MasterChannel);
    }

    ChannelState? ChannelOf(ModelRoot root, int trackId) =>
        trackId == -1 ? Engine.MasterChannel : Find(root, trackId)?.Channel;

    InsertSlot? FindInsert(ModelRoot root, int trackId, string insertId) =>
        ChannelOf(root, trackId)?.Config.Inserts.FirstOrDefault(i => i.Slot.Id == insertId)?.Slot;

    /// <summary>Sets a fader; a moved value of an automated volume, pan or mute holds it against its lane.</summary>
    void SetFader(int trackId, Strip fader, EngineStrip strip) {
        var root = Engine.Root;
        if (fader.Gain != strip.Gain) {
            Hold(root, trackId, AutomationKind.Volume);
        }

        if (fader.Pan != strip.Pan) {
            Hold(root, trackId, AutomationKind.Pan);
        }

        if (fader.Mute != strip.Mute) {
            Hold(root, trackId, AutomationKind.Mute);
        }

        fader.Set(strip);
    }

    /// <summary>A control move of an automated target: it follows the control until the next play or locate.</summary>
    void Hold(ModelRoot root, int trackId, AutomationKind kind, string? id = null, string? parameterId = null) {
        var target = root.Automation.Find(trackId, kind, id, parameterId);
        if (target >= 0 && root.Automation.Lane(target) is not null) {
            Engine.Overrides.Hold(target);
        }
    }

    /// <summary>
    ///     Brings <paramref name="channel" />'s configuration in line with <paramref name="strip" />: an insert whose id
    ///     and plugin are unchanged keeps its instance (its parameters updated where they differ), another is built
    ///     (prepared, state loaded, parameters set); sends keep their slots by id. The instances it no longer uses go
    ///     to <paramref name="replaced" />, to dispose once the plan without them is published.
    /// </summary>
    void ApplyChannel(ChannelState channel, int trackId, EngineStrip strip, List<InsertSlot> replaced) {
        var old = channel.Config;
        var claimed = new HashSet<InsertSlot>();
        var inserts = new List<ChannelInsert>();
        foreach (var spec in strip.Inserts ?? []) {
            var slot = KeepInsert(old, spec, claimed) ?? BuildInsert(trackId, spec);
            claimed.Add(slot);
            inserts.Add(new(slot, spec.Bypassed, spec.SidechainTrack, spec.SidechainBus));
        }

        var sends = new List<ChannelSend>();
        var claimedSends = new HashSet<SendSlot>();
        foreach (var spec in strip.Sends ?? []) {
            var slot = old.Sends.Select(static s => s.Slot)
                    .FirstOrDefault(s => s.Id == spec.Id && !claimedSends.Contains(s))
                ?? new SendSlot(spec.Id);
            claimedSends.Add(slot);
            slot.Set(spec.Gain, spec.Pan);
            sends.Add(new(slot, spec.Bus, spec.PreFader));
        }

        channel.Config = new([..inserts], [..sends], strip.Output);
        replaced.AddRange(old.Inserts.Select(static i => i.Slot).Where(s => !claimed.Contains(s)));
    }

    /// <summary>The instance of <paramref name="spec" />'s id and plugin, its parameters brought up to date, or null.</summary>
    static InsertSlot? KeepInsert(ChannelConfig old, EngineInsert spec, HashSet<InsertSlot> claimed) {
        var slot = old.Inserts.Select(static i => i.Slot)
            .FirstOrDefault(s => s.Id == spec.Id && s.PluginId == spec.PluginId && !claimed.Contains(s));
        if (slot is null) {
            return null;
        }

        foreach (var (id, value) in spec.Parameters) {
            if (!slot.Values.TryGetValue(id, out var known) || Math.Abs(known - value) > 1e-9) {
                slot.Set(id, value);
            }
        }

        return slot;
    }

    /// <summary>
    ///     A new insert: the effect created (built-in, or by the plugin factory), prepared, its state loaded and its
    ///     parameters set. One that cannot be loaded becomes an empty slot that passes audio through, and is reported.
    /// </summary>
    InsertSlot BuildInsert(int trackId, EngineInsert spec) {
        var callbacks = new PluginCallbacks(this, trackId, spec.Id, spec.PluginId);
        IAudioEffect? effect = null;
        try {
            effect = Plugins.CreateEffect(
                spec.PluginId,
                new(trackId, spec.Id, spec.PluginId, Options.SampleRate, AudioEngine.MaxFrames, callbacks)
            );
            if (effect is null) {
                Problem(trackId, spec.Id, spec.PluginId, $"No effect '{spec.PluginId}'.");
                return new(spec.Id, spec.PluginId, null);
            }

            effect.Prepare(Options.SampleRate, AudioEngine.MaxFrames);
            if (spec.State is not null && !effect.LoadState(Convert.FromBase64String(spec.State))) {
                Log?.Invoke($"{spec.PluginId}: the saved state did not load; it starts from its defaults");
            }

            var slot = new InsertSlot(spec.Id, spec.PluginId, effect);
            foreach (var (id, value) in spec.Parameters) {
                slot.Initialise(id, value);
            }

            callbacks.Slot = slot;
            return slot;
        } catch (Exception e) when (IsInstrumentFailure(e)) {
            effect?.Dispose();
            Problem(trackId, spec.Id, spec.PluginId, e.Message);
            return new(spec.Id, spec.PluginId, null);
        }
    }

    void Problem(int trackId, string? insertId, string pluginId, string message) {
        Log?.Invoke(
            $"track {trackId}: {pluginId}{(insertId is null ? string.Empty : $" (insert {insertId})")}: {message}"
        );
        PluginProblem?.Invoke(new(trackId, insertId, pluginId, message));
    }

    /// <summary>Publishes the current model with a plan rebuilt from the tracks' configurations.</summary>
    void PublishPlan() {
        var current = Engine.Root;
        Swap(current.With(mix: BuildPlan(current.Tracks, current.Automation)));
    }

    /// <summary>
    ///     The plan for <paramref name="tracks" />, with what it needs allocated before it is published: post-fader
    ///     buffers of sidechain sources, previous-block copies of buses read late, and delay lines.
    /// </summary>
    MixPlan BuildPlan(TrackState[] tracks, AutomationSet automation) {
        Engine.CycleObserver = Plugins.Factory as IAudioCycleObserver;
        var plan = MixPlan.Build(tracks, Engine.MasterChannel, automation);
        foreach (var bus in plan.SnapshotBuses) {
            Engine.EnsureBusSnapshot(bus);
        }

        GrowDelays(plan);
        return plan;
    }

    /// <summary>
    ///     Gives every channel a delay line long enough for any path's latency (the sum of every insert's latency
    ///     bounds it, doubled for headroom), once any insert has latency and compensation is on.
    /// </summary>
    void GrowDelays(MixPlan plan) {
        Engine.TakeLatencyOverflow();
        if (Engine.Compensation == DelayCompensation.Off) {
            return;
        }

        long bound = 0;
        foreach (var node in plan.Order.Append(plan.Master)) {
            foreach (var insert in node.Config.Inserts) {
                if (insert.Slot.Effect is { } effect) {
                    bound += Math.Max(0, effect.LatencySamples);
                }
            }
        }

        if (bound == 0) {
            return;
        }

        var frames = (int)Math.Min(int.MaxValue / 4, Math.Max(MinDelayFrames, 2 * bound));
        foreach (var node in plan.Order.Append(plan.Master)) {
            if (node.Channel.Delay is not { } line || line.MaxDelay < bound) {
                node.Channel.Delay = new(frames);
            }
        }
    }

    /// <summary>Starts the timer that grows delay lines when a latency outgrew them (a plugin changed its latency).</summary>
    void StartMaintenance() =>
        maintenance = new(
            _ => {
                if (!Engine.TakeLatencyOverflow()) {
                    return;
                }

                lock (gate) {
                    GrowDelays(Engine.Root.Mix);
                }
            },
            null,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250)
        );

    /// <summary>A hosted plugin's reports, for the instance on track <c>TrackId</c>, insert <c>InsertId</c>.</summary>
    sealed class PluginCallbacks(EngineController owner, int trackId, string? insertId, string pluginId)
        : IPluginCallbacks {
        /// <summary>The insert's slot once built (a problem bypasses it).</summary>
        public InsertSlot? Slot { get; set; }

        public void ParameterChanged(string parameterId, double value) =>
            owner.ParameterChanged?.Invoke(new(trackId, insertId, parameterId, value));

        public void Problem(string message) {
            Slot?.MarkFailed();
            owner.Problem(trackId, insertId, pluginId, message);
        }

        public void LatencyChanged() =>
            ThreadPool.QueueUserWorkItem(
                static o => {
                    lock (o.gate) {
                        o.GrowDelays(o.Engine.Root.Mix);
                    }
                },
                owner,
                false
            );
    }
}
