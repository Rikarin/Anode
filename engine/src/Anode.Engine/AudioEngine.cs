using Anode.Audio;
using Anode.Engine.Streaming;
using System.Diagnostics;

namespace Anode.Engine;

/// <summary>
///     The audio callback (docs/01-implementation-plan.md §3.2): applies transport commands, reads every track's next
///     frames from its stream, adds monitored input, runs each channel strip into the stereo bus, runs the master strip,
///     adds the click, feeds armed tracks' takes and publishes the transport. It never allocates, locks or blocks; the
///     model it reads is swapped by the control thread with RCU (<see cref="Publish" />, <see cref="IsGraceOver" />).
/// </summary>
public sealed unsafe class AudioEngine : IAudioProcessor {
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

    ModelRoot root = ModelRoot.Empty;
    long started;
    long completed;
    long callbacks;
    long underruns;
    long commandsApplied;
    long hardFloor;
    long countInTotal;
    long countInRemaining;
    int countInBeats;
    bool metronomeEnabled;
    float metronomeGain = 1f;

    public AudioEngine(int sampleRate) {
        SampleRate = sampleRate;
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

    /// <summary>True once every callback that had started when <see cref="Publish" /> returned the token has finished.</summary>
    public bool IsGraceOver(long grace) => Volatile.Read(ref completed) >= grace;

    public void Process(float** inputs, int inputChannels, float** outputs, int outputChannels, int frames) {
        Interlocked.Increment(ref started); // full fence before the root is read
        try {
            var model = Volatile.Read(ref root);
            var now = (long)(Stopwatch.GetTimestamp() * NanosecondsPerTick);
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
            PublishSnapshot(clock, position, now, model);
        } finally {
            Volatile.Write(ref completed, completed + 1);
        }
    }

    void ApplyCommands() {
        while (Commands.TryDequeue(out var command)) {
            switch (command.Kind) {
                case EngineCommandKind.Play:
                    if (transport.State == TransportState.Stopped && countInRemaining == 0) {
                        transport.Play();
                    }

                    break;
                case EngineCommandKind.Stop:
                    countInRemaining = 0;
                    transport.Stop();
                    break;
                case EngineCommandKind.Record:
                    if (transport.State == TransportState.Stopped && countInRemaining == 0) {
                        if (command.A > 0) {
                            countInTotal = countInRemaining = command.A;
                            countInBeats = (int)Math.Clamp(command.B, 1, 64);
                            transport.PrepareRecord();
                        } else {
                            transport.Record();
                        }
                    } else if (transport.State == TransportState.Playing) {
                        transport.Record(); // punch in, no count-in
                    }

                    break;
                case EngineCommandKind.Locate:
                    transport.Locate(Math.Max(0, command.A));
                    hardFloor = transport.Anchor.Id;
                    break;
                case EngineCommandKind.SetCycle:
                    transport.SetCycle(new(command.Flag, command.A, command.B));
                    break;
            }

            commandsApplied++;
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
        var rolling = transport.Rolling;
        var recording = transport.State == TransportState.Recording;
        var clock = transport.Clock;
        var position = transport.Position;
        var anchor = transport.Anchor.Id;
        var bus = busLeft.AsSpan(0, frames);
        var busR = busRight.AsSpan(0, frames);
        bus.Clear();
        busR.Clear();

        var tracks = model.Tracks;
        var anySolo = false;
        foreach (var track in tracks) {
            anySolo |= track.Strip.Solo;
        }

        for (var i = 0; i < tracks.Length; i++) {
            var track = tracks[i];
            var left = trackLeft.AsSpan(0, frames);
            var right = trackRight.AsSpan(0, frames);
            left.Clear();
            right.Clear();
            var identity = TrackFeed.Identity(anchor, model.Generations[i]);
            if (rolling) {
                if (track.Stream.Read(clock, frames, identity, hardFloor, left, right) < frames) {
                    underruns++;
                }
            } else {
                track.Stream.Align(clock, identity, hardFloor);
            }

            float* in0 = null, in1 = null;
            var input = track.InputChannel;
            if ((uint)input < (uint)inputChannels) {
                in0 = inputs[input] + offset;
                in1 = track.Stereo && input + 1 < inputChannels ? inputs[input + 1] + offset : in0;
            }

            if (in0 is not null && track.Monitoring) {
                Add(left, in0);
                if (track.Stereo) {
                    Add(right, in1);
                }
            }

            if (recording && track.Take is { } take) {
                take.Push(in0, track.Stereo ? in1 : null, frames, position);
            }

            track.Strip.MixInto(left, track.Stereo ? right : left, track.Stereo, !anySolo || track.Strip.Solo, bus, busR);
        }

        Master.Process(bus, busR);

        var click = clicks.AsSpan(0, frames);
        click.Clear();
        metronome.Begin();
        if (countIn) {
            metronome.AddCountIn(countInTotal, countInBeats, countInTotal - countInRemaining, frames);
        } else if (rolling && Volatile.Read(ref metronomeEnabled)) {
            AddModelClicks(model, position, frames);
        }

        metronome.Render(click, Volatile.Read(ref metronomeGain));
        WriteOutputs(outputs, outputChannels, offset, bus, busR, click);

        if (countIn) {
            countInRemaining -= frames;
            if (countInRemaining == 0) {
                transport.Record(); // rolls from the next frame; the count-in's frames do not move the playhead
            }
        } else {
            transport.Advance(frames);
        }
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
