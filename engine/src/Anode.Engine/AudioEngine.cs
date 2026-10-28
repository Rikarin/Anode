using Anode.Audio;
using Anode.Engine.Plugins;
using Anode.Engine.Streaming;
using System.Diagnostics;

namespace Anode.Engine;

/// <summary>
///     The audio callback (docs/01-implementation-plan.md §3.2): applies transport commands, reads every track's next
///     frames from its stream, adds monitored input, renders instrument tracks from their MIDI
///     (<c>AudioEngine.Midi.cs</c>), runs each channel strip into the stereo bus, runs the master strip, adds the click,
///     feeds armed tracks' takes and publishes the transport. It never allocates, locks or blocks; the model and the
///     instruments it reads are swapped by the control thread with RCU (<see cref="Publish" />,
///     <see cref="IsGraceOver" />).
/// </summary>
public sealed unsafe partial class AudioEngine : IAudioProcessor {
    /// <summary>Largest callback a device delivers (CTH_MAX_FRAMES).</summary>
    public const int MaxFrames = 4096;

    static readonly double NanosecondsPerTick = 1e9 / Stopwatch.Frequency;

    readonly Transport transport = new();
    readonly SnapshotSeqlock published = new();
    readonly Metronome metronome;
    readonly float[] trackLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] trackRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] busLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] busRight = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] clicks = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] laneLeft = GC.AllocateArray<float>(MaxFrames, true);
    readonly float[] laneRight = GC.AllocateArray<float>(MaxFrames, true);

    ModelRoot root = ModelRoot.Empty;
    int offline;
    int inDevice;
    long started;
    long completed;
    long callbacks;
    long underruns;
    long commandsApplied;
    long hardFloor;
    long countInTotal;
    long countInRemaining;
    int countInBeats;
    int callbackFrames;
    bool metronomeEnabled;
    float metronomeGain = 1f;

    public AudioEngine(int sampleRate) {
        SampleRate = sampleRate;
        MasterChannel = new(Master);
        metronome = new(sampleRate);
        hardFloor = transport.Anchor.Id;
        PublishSnapshot(0, 0, 0, root);
    }

    public int SampleRate { get; }

    public Strip Master { get; } = new();

    public CommandRing Commands { get; } = new();

    public Metronome Metronome => metronome;

    /// <summary>The transport as of the last callback.</summary>
    public EngineSnapshot Snapshot => published.Read();

    /// <summary>The published model.</summary>
    public ModelRoot Root => Volatile.Read(ref root);

    /// <summary>Control thread: whether the model's clicks sound, and the click level (count-in clicks too).</summary>
    public void SetMetronome(bool enabled, float gain) {
        Volatile.Write(ref metronomeEnabled, enabled);
        Volatile.Write(ref metronomeGain, float.IsFinite(gain) ? Math.Max(0, gain) : 0);
    }

    /// <summary>
    ///     Control thread: installs <paramref name="next" />; returns the model it replaced and the grace token to wait
    ///     on (<see cref="IsGraceOver" />) before releasing anything only that model used.
    /// </summary>
    public (ModelRoot Previous, long Grace) Publish(ModelRoot next) {
        var previous = Interlocked.Exchange(ref root, next); // full fence: the read of `started` cannot move above it
        return (previous, Volatile.Read(ref started));
    }

    /// <summary>
    ///     Control thread, after replacing something the callback reads (an instrument): the grace token to wait on
    ///     before releasing the old one.
    /// </summary>
    public long Grace() {
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref started);
    }

    /// <summary>True once every callback that had started when <see cref="Publish" /> returned the token has finished.</summary>
    public bool IsGraceOver(long grace) => Volatile.Read(ref completed) >= grace;

    /// <summary>
    ///     The device's callback. While an offline render drives the engine (<see cref="BeginOffline" />) it leaves the
    ///     device's (cleared) buffers silent and touches nothing else.
    /// </summary>
    public void Process(float** inputs, int inputChannels, float** outputs, int outputChannels, int frames) {
        Interlocked.Exchange(ref inDevice, 1); // full fence before `offline` is read (pairs with BeginOffline)
        try {
            if (Volatile.Read(ref offline) == 0) {
                Run(inputs, inputChannels, outputs, outputChannels, frames);
            }
        } finally {
            Volatile.Write(ref inDevice, 0);
        }
    }

    /// <summary>
    ///     Control thread: from now on the device's callbacks are silent and touch nothing, and the caller's thread is
    ///     the engine's audio thread (<see cref="ProcessOffline" />) until <see cref="EndOffline" />. Returns once no
    ///     device callback is running the engine.
    /// </summary>
    public void BeginOffline() {
        Interlocked.Exchange(ref offline, 1); // full fence before `inDevice` is read
        var spin = new SpinWait();
        while (Volatile.Read(ref inDevice) != 0) {
            spin.SpinOnce(-1);
        }
    }

    /// <summary>The render's thread hands the engine back to the device.</summary>
    public void EndOffline() => Volatile.Write(ref offline, 0);

    /// <summary>Whether an offline render drives the engine.</summary>
    public bool IsOffline => Volatile.Read(ref offline) != 0;

    /// <summary>The callback, run by the thread that called <see cref="BeginOffline" /> (an offline render).</summary>
    public void ProcessOffline(float** inputs, int inputChannels, float** outputs, int outputChannels, int frames) =>
        Run(inputs, inputChannels, outputs, outputChannels, frames);

    void Run(float** inputs, int inputChannels, float** outputs, int outputChannels, int frames) {
        Interlocked.Increment(ref started); // full fence before the root is read
        try {
            var model = Volatile.Read(ref root);
            var now = (long)(Stopwatch.GetTimestamp() * NanosecondsPerTick);
            AudioThread.Mark();
            var observer = Volatile.Read(ref cycleObserver);
            observer?.BeginCycle(now, frames);
            previousCallbackNs = callbackNs;
            callbackNs = now;
            callbackFrames = frames;
            ApplyCommands();
            var clock = transport.Clock;
            var position = transport.Position;
            var offset = 0;
            while (offset < frames) {
                var count = Math.Min(frames - offset, MaxFrames);
                if (countInRemaining > 0) {
                    count = (int)Math.Min(count, countInRemaining); // recording starts on the count-in's last frame
                }

                RenderBlock(model, inputs, inputChannels, outputs, outputChannels, offset, count);
                offset += count;
            }

            callbacks++;
            observer?.EndCycle();
            PublishSnapshot(clock, position, now, model);
        } finally {
            AudioThread.Mark(false); // only while in the callback: offline renders run it on other threads
            Volatile.Write(ref completed, completed + 1);
        }
    }

    void ApplyCommands() {
        while (Commands.TryDequeue(out var command)) {
            switch (command.Kind) {
                case EngineCommandKind.Play:
                    if (transport.State == TransportState.Stopped && countInRemaining == 0) {
                        transport.Play();
                        flashbackRestart = true;
                        ReleaseOverrides();
                    }

                    break;
                case EngineCommandKind.Stop:
                    countInRemaining = 0;
                    if (transport.State == TransportState.Recording) {
                        Volatile.Write(ref recordEnd, transport.Position);
                    }

                    transport.Stop();
                    Panic();
                    break;
                case EngineCommandKind.Record:
                    Record(command.A, command.B);
                    break;
                case EngineCommandKind.Locate:
                    transport.Locate(Math.Max(0, command.A));
                    hardFloor = transport.Anchor.Id;
                    Panic();
                    ReleaseOverrides();
                    break;
                case EngineCommandKind.SetCycle:
                    transport.SetCycle(new(command.Flag, command.A, command.B));
                    break;
                case EngineCommandKind.SetPunch:
                    punch = new(command.Flag, command.A, command.B);
                    break;
            }

            commandsApplied++;
        }
    }

    /// <summary>
    ///     From a stop: a count-in of <paramref name="countIn" /> frames (if any) then recording; while playing: a
    ///     punch-in.
    /// </summary>
    void Record(long countIn, long beats) {
        if (transport.State == TransportState.Playing) {
            transport.Record(); // punch in, no count-in
            BeginRecording();
        } else if (transport.State == TransportState.Stopped && countInRemaining == 0 && countIn > 0) {
            countInTotal = countInRemaining = countIn;
            countInBeats = (int)Math.Clamp(beats, 1, 64);
            Volatile.Write(ref recordStart, -1);
            transport.PrepareRecord();
        } else if (transport.State == TransportState.Stopped && countInRemaining == 0) {
            transport.Record();
            BeginRecording();
        }
    }

    void RenderBlock(
        ModelRoot model,
        float** inputs,
        int inputChannels,
        float** outputs,
        int outputChannels,
        int offset,
        int frames
    ) {
        var countIn = countInRemaining > 0;
        var position = transport.Position;
        var clock = transport.Clock;
        BeginBlock(clock, position, frames, countIn);
        var outLeft = busLeft.AsSpan(0, frames);
        var outRight = busRight.AsSpan(0, frames);
        Mix(
            model,
            inputs,
            inputChannels,
            outputs,
            outputChannels,
            offset,
            frames,
            position,
            clock,
            countIn,
            outLeft,
            outRight
        );
        var click = clicks.AsSpan(0, frames);
        RenderClicks(model, countIn, position, click);
        WriteOutputs(outputs, outputChannels, offset, outLeft, outRight, click);

        if (countIn) {
            countInRemaining -= frames;
            if (countInRemaining == 0) {
                transport.Record(); // rolls from the next frame; the count-in's frames do not move the playhead
                BeginRecording();
            }
        } else {
            transport.Advance(frames);
        }
    }

    /// <summary>
    ///     The track's next frames from its streams while rolling (counting an underrun when short): its main stream,
    ///     plus its crossfade stream when its playlist has lanes, summed sample by sample.
    /// </summary>
    void ReadTrack(TrackState track, int generation, byte[]? lanes, Span<float> left, Span<float> right) {
        left.Clear();
        right.Clear();
        var identity = TrackFeed.Identity(transport.Anchor.Id, generation);
        var second = track.Crossfade;
        if (!transport.Rolling) {
            track.Stream.Align(transport.Clock, identity, hardFloor);
            second?.Align(transport.Clock, identity, hardFloor);
            return;
        }

        if (track.Stream.Read(transport.Clock, left.Length, identity, hardFloor, left, right) < left.Length) {
            underruns++;
        }

        if (second is null) {
            return;
        }

        if (lanes is null) {
            second.Align(transport.Clock, identity, hardFloor); // drops what an earlier compile left in it
            return;
        }

        var l2 = laneLeft.AsSpan(0, left.Length);
        var r2 = laneRight.AsSpan(0, left.Length);
        l2.Clear();
        r2.Clear();
        if (second.Read(transport.Clock, left.Length, identity, hardFloor, l2, r2) < left.Length
            && second.Started(identity, transport.Clock)) {
            underruns++;
        }

        for (var f = 0; f < left.Length; f++) {
            left[f] += l2[f];
        }

        if (track.Stereo) {
            for (var f = 0; f < right.Length; f++) {
                right[f] += r2[f];
            }
        }
    }

    /// <summary>
    ///     Adds the track's input when it monitors, feeds its take while recording and its Flashback memory while
    ///     playing. While recording with autopunch, an armed track plays back outside the punch range and hears its
    ///     input instead inside it (automatic input monitoring), switching on the exact frames.
    /// </summary>
    void InputTrack(
        TrackState track,
        float** inputs,
        int inputChannels,
        int offset,
        Span<float> left,
        Span<float> right,
        long position,
        long clock,
        bool countIn
    ) {
        var input = track.InputChannel;
        float* in0 = null, in1 = null;
        if ((uint)input < (uint)inputChannels) {
            in0 = inputs[input] + offset;
            in1 = track.Stereo && input + 1 < inputChannels ? inputs[input + 1] + offset : in0;
        }

        var recording = transport.State == TransportState.Recording && !countIn;
        if (recording && punch.Active && track.Armed) {
            ApplyPunch(left, right, track.Stereo, in0, in1, position);
        } else if (track.Monitoring) {
            Add(left, in0);
            Add(right, track.Stereo ? in1 : null);
        }

        Capture(track, in0, track.Stereo ? in1 : null, left.Length, clock, recording);
    }

    /// <summary>Feeds the track's input to its take while recording, and to its Flashback memory while playing.</summary>
    void Capture(TrackState track, float* in0, float* in1, int frames, long clock, bool recording) {
        if (recording && track.Take is { } take) {
            Record(take, in0, in1, frames, clock);
        }

        if (flashbackActive && track.Armed && track.Flashback is { } memory) {
            memory.Write(in0, in1, frames, clock, flashbackStart == clock);
        }
    }

    /// <summary>The count-in's clicks, or the model's while rolling with the metronome on.</summary>
    void RenderClicks(ModelRoot model, bool countIn, long position, Span<float> click) {
        click.Clear();
        metronome.Begin();
        if (countIn) {
            metronome.AddCountIn(countInTotal, countInBeats, countInTotal - countInRemaining, click.Length);
        } else if (transport.Rolling && Volatile.Read(ref metronomeEnabled)) {
            AddModelClicks(model, position, click.Length);
        }

        metronome.Render(click, Volatile.Read(ref metronomeGain));
    }


    void AddModelClicks(ModelRoot model, long position, int frames) {
        var order = transport.PlayOrder;
        var done = 0;
        while (done < frames) {
            var count = (int)Math.Min(frames - done, order.UntilJump(position));
            metronome.AddEvents(model.Metronome, position, count, done);
            done += count;
            position = order.Advance(position, count);
        }
    }

    static void Add(Span<float> destination, float* source) {
        if (source is null) {
            return;
        }

        for (var i = 0; i < destination.Length; i++) {
            destination[i] += source[i];
        }
    }

    static void WriteOutputs(
        float** outputs,
        int outputChannels,
        int offset,
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        ReadOnlySpan<float> click
    ) {
        if (outputChannels <= 0) {
            return;
        }

        var outLeft = outputs[0] + offset;
        if (outputChannels == 1) {
            for (var i = 0; i < left.Length; i++) {
                outLeft[i] += (left[i] + right[i]) * 0.5f + click[i];
            }

            return;
        }

        var outRight = outputs[1] + offset;
        for (var i = 0; i < left.Length; i++) {
            outLeft[i] += left[i] + click[i];
            outRight[i] += right[i] + click[i];
        }
    }

    void PublishSnapshot(long clock, long position, long now, ModelRoot model) =>
        published.Write(
            new() {
                Clock = clock,
                Position = position,
                State = countInRemaining > 0 ? TransportState.Recording : transport.State,
                CountIn = countInRemaining > 0,
                Anchor = transport.Anchor,
                HardFloor = hardFloor,
                HostTimeNs = now,
                ModelVersion = model.Version,
                Callbacks = callbacks,
                Underruns = underruns,
                CommandsApplied = commandsApplied
            }
        );
}
