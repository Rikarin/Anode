using Anode.Engine.Rendering;
using Anode.Engine.Streaming;
using Anode.Ipc;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anode.Engine;

// Rendering (docs/05-phase-4.md, "Rendering"): the master, stems (each track's post-fader output as it reaches its
// destination) or a freeze (one track's source and inserts before its fader), to 32-bit float stereo WAV. Bounce in
// place (docs/06-phase-5.md) is a freeze of several tracks, each isolated, optionally without its inserts or with its
// fader and pan; the server extends the range by the tail it wants. Offline, the
// render's thread takes the engine over from the device (whose output stays silent) and runs it as fast as the disk
// allows; in real time, the device plays it and the callback captures it. Either way each file drops the latency its
// path had at the start, so it lines up with the project.
public sealed partial class EngineController {
    /// <summary>Frames per offline render block.</summary>
    public const int RenderBlock = 1024;

    static readonly TimeSpan RenderPrimeTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    int rendering;
    Cycle cycle;

    /// <summary>Whether a render is running.</summary>
    public bool IsRendering => Volatile.Read(ref rendering) != 0;

    /// <summary>
    ///     Renders <paramref name="message" />'s range (see <see cref="RenderMessage" />) and returns the files written.
    ///     <paramref name="progress" /> hears the fraction done now and then. Cancelling deletes what was written and
    ///     throws <see cref="OperationCanceledException" />. Throws <see cref="InvalidOperationException" /> while
    ///     recording or rendering, or for an unknown track; <see cref="IOException" /> when a file cannot be written or
    ///     the disk does not keep up.
    /// </summary>
    public async Task<IReadOnlyList<string>> RenderAsync(
        RenderMessage message,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default
    ) {
        if (message.StartSample < 0 || message.EndSample <= message.StartSample) {
            throw new ArgumentException("A render needs 0 ≤ start < end.", nameof(message));
        }

        if (Interlocked.CompareExchange(ref rendering, 1, 0) != 0) {
            throw new InvalidOperationException("Another render is running.");
        }

        RenderCapture? capture = null;
        var writers = new List<(RenderTarget Target, FloatWavWriter Writer)>();
        var finished = false;
        try {
            capture = Prepare(message, writers);
            if (message.Realtime) {
                await RealtimeAsync(message, capture, writers, progress, cancellationToken).ConfigureAwait(false);
            } else {
                await Task.Factory.StartNew(
                        () => Offline(message, capture, writers, progress, cancellationToken),
                        cancellationToken,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default
                    )
                    .ConfigureAwait(false);
            }

            Complete(writers);
            finished = true;
            progress?.Invoke(1);
            return [..writers.Select(static w => w.Writer.Path)];
        } finally {
            Release(writers, !finished);
            Volatile.Write(ref rendering, 0);
        }
    }

    /// <summary>Completes the files; throws when a real-time render dropped frames.</summary>
    static void Complete(List<(RenderTarget Target, FloatWavWriter Writer)> writers) {
        foreach (var (target, writer) in writers) {
            writer.Complete();
            if (target.Dropped > 0) {
                throw new IOException($"{target.Path}: {target.Dropped} frames dropped (the writer fell behind).");
            }
        }
    }

    /// <summary>Closes the files and frees the rings; <paramref name="delete" /> removes the files (a failed render).</summary>
    void Release(List<(RenderTarget Target, FloatWavWriter Writer)> writers, bool delete) {
        foreach (var (target, writer) in writers) {
            writer.Dispose();
            target.Dispose();
            if (delete) {
                TryDelete(writer.Path);
            }
        }
    }

    /// <summary>The render's targets and files.</summary>
    RenderCapture Prepare(RenderMessage message, List<(RenderTarget, FloatWavWriter)> writers) {
        lock (gate) {
            if (takes.Length > 0 || recordingPass) {
                throw new InvalidOperationException("Cannot render while recording.");
            }

            var root = Engine.Root;
            var kind = message.Freeze ? RenderKind.Freeze
                : message.Tracks is null ? RenderKind.Master
                : RenderKind.Stems;
            if (kind == RenderKind.Freeze && message.Tracks is not { Count: > 0 }) {
                throw new ArgumentException("A freeze renders at least one track.", nameof(message));
            }

            var frames = message.EndSample - message.StartSample;
            var ring = message.Realtime ? 4 * Options.SampleRate : 2 * AudioEngine.MaxFrames;
            if (kind == RenderKind.Master) {
                var master = new RenderTarget(-1, Path.GetFullPath(message.Path), frames, ring);
                Open(master);
                return new(kind, master, []);
            }

            var tracks = new Dictionary<int, RenderTarget>();
            foreach (var id in message.Tracks!.Distinct()) {
                if (root.IndexOf(id) < 0) {
                    throw new InvalidOperationException($"No track {id}.");
                }

                var target = new RenderTarget(
                    id,
                    Path.Combine(Path.GetFullPath(message.Path), $"{id}.wav"),
                    frames,
                    ring
                );
                tracks[id] = target;
                Open(target);
            }

            return new(kind, null, tracks) {
                IncludeVolumePan = message.IncludeVolumePan, // both read only by a freeze
                BypassInserts = message.BypassInserts
            };
        }

        void Open(RenderTarget target) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
                writers.Add((target, new(target.Path, 2, Options.SampleRate)));
            } catch {
                target.Dispose();
                throw;
            }
        }
    }

    /// <summary>The render's thread runs the engine block by block (the device's callbacks stay silent meanwhile).</summary>
    unsafe void Offline(
        RenderMessage message,
        RenderCapture capture,
        List<(RenderTarget Target, FloatWavWriter Writer)> writers,
        Action<double>? progress,
        CancellationToken cancellationToken
    ) {
        var memory = (float*)NativeMemory.AllocZeroed(2 * RenderBlock, sizeof(float));
        var outputs = stackalloc float*[2];
        outputs[0] = memory;
        outputs[1] = memory + RenderBlock;
        var clock = Stopwatch.StartNew();
        var reported = TimeSpan.Zero;
        var observer = Plugins.Factory as Plugins.IAudioCycleObserver;
        observer?.SetOffline(true); // hosted plugins are waited for, not bypassed for being slow
        Engine.BeginOffline();
        try {
            Engine.BeginRender(message.StartSample);
            Engine.Rendering = capture;
            WaitPrimed(RenderBlock);
            Engine.StartRender();
            var frames = message.EndSample - message.StartSample;
            var limit = frames + 60L * Options.SampleRate; // the longest latency a render waits out
            long done = 0;
            while (!capture.Complete) {
                cancellationToken.ThrowIfCancellationRequested();
                if (done > limit) {
                    throw new InvalidOperationException("The render did not complete.");
                }

                WaitPrimed(RenderBlock);
                NativeMemory.Clear(memory, 2 * RenderBlock * sizeof(float));
                Engine.ProcessOffline(null, 0, outputs, 2, RenderBlock);
                done += RenderBlock;
                Drain(writers);
                if (progress is not null && clock.Elapsed - reported >= ProgressInterval) {
                    reported = clock.Elapsed;
                    progress(Fraction(writers));
                }
            }

            Drain(writers);
        } finally {
            Engine.EndRender();
            Engine.EndOffline();
            observer?.SetOffline(false);
            Streamer.Wake();
            NativeMemory.Free(memory);
        }
    }

    /// <summary>The device plays the range from its start while the callback captures it.</summary>
    async Task RealtimeAsync(
        RenderMessage message,
        RenderCapture capture,
        List<(RenderTarget Target, FloatWavWriter Writer)> writers,
        Action<double>? progress,
        CancellationToken cancellationToken
    ) {
        var saved = cycle;
        var position = Engine.Snapshot.Position;
        Stop();
        Engine.Rendering = capture; // captures from the first frame rolled
        try {
            SetCycle(false, saved.Start, saved.End);
            await PlayAsync(message.StartSample, cancellationToken).ConfigureAwait(false);
            var started = Stopwatch.StartNew();
            while (Engine.Snapshot.State == TransportState.Stopped && started.Elapsed < CommandTimeout) {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }

            while (!capture.Complete) {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                Drain(writers);
                progress?.Invoke(Fraction(writers));
                if (Engine.Snapshot.State == TransportState.Stopped && !capture.Complete) {
                    throw new OperationCanceledException("The transport stopped during a real-time render.");
                }
            }

            Drain(writers);
        } finally {
            Engine.Rendering = null;
            Stop();
            SetCycle(saved.Enabled, saved.Start, saved.End);
            Locate(position);
        }
    }

    static void Drain(List<(RenderTarget Target, FloatWavWriter Writer)> writers) {
        foreach (var (target, writer) in writers) {
            var ring = target.Ring;
            var available = ring.Available;
            if (available == 0) {
                continue;
            }

            ring.ReadRegions(available, out var first, out var second);
            writer.Write(first);
            writer.Write(second);
            ring.Skip(available);
        }
    }

    static double Fraction(List<(RenderTarget Target, FloatWavWriter Writer)> writers) {
        double total = 0, left = 0;
        foreach (var (target, _) in writers) {
            total += target.Frames;
            left += target.Remaining;
        }

        return total <= 0 ? 1 : Math.Clamp(1 - left / total, 0, 1);
    }

    /// <summary>
    ///     Offline render thread: waits until every track's stream holds the next <paramref name="frames" /> (pumping
    ///     the disk itself when its thread does not run, as in tests).
    /// </summary>
    void WaitPrimed(int frames) {
        var deadline = DateTime.UtcNow + RenderPrimeTimeout;
        var spin = 0;
        while (true) {
            if (!Streamer.IsRunning) {
                while (Streamer.Pass()) { }
            }

            if (RenderPrimed(frames)) {
                return;
            }

            if (DateTime.UtcNow > deadline) {
                throw new IOException("The disk did not keep up with the render: " + Unprimed(frames) + ".");
            }

            Streamer.Wake();
            if (++spin < 50) {
                Thread.Yield();
            } else {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>Which stream a render waits for, and where its fill stands (for the error).</summary>
    string Unprimed(int frames) {
        var root = Engine.Root;
        var (clock, anchor) = Engine.RenderTransport;
        for (var i = 0; i < root.Tracks.Length; i++) {
            var identity = TrackFeed.Identity(anchor, root.Generations[i]);
            var track = root.Tracks[i];
            foreach (var (stream, name) in new[] { (track.Stream, "stream"), (track.Crossfade, "crossfade stream") }) {
                if (stream is null || (name != "stream" && root.Lanes[i] is null)) {
                    continue;
                }

                if (stream.FillIdentity != identity || stream.FillCursor - clock < Math.Min(frames, stream.TargetFrames)) {
                    return $"track {i}'s {name} (fill {stream.FillIdentity:x}, wanted {identity:x}; "
                        + $"{stream.FillCursor - clock} of {Math.Min(frames, stream.TargetFrames)} frames ahead, "
                        + $"{stream.FreeFrames} free; {stream.Describe()}; render clock {clock})";
                }
            }
        }

        return "no stream";
    }

    bool RenderPrimed(int frames) {
        var root = Engine.Root;
        var (clock, anchor) = Engine.RenderTransport;
        for (var i = 0; i < root.Tracks.Length; i++) {
            var identity = TrackFeed.Identity(anchor, root.Generations[i]);
            var track = root.Tracks[i];
            if (!Holds(track.Stream)
                || (root.Lanes[i] is not null && track.Crossfade is { } second && !Holds(second))) {
                return false;
            }

            bool Holds(TrackFeed stream) =>
                stream.FillIdentity == identity && stream.FillCursor - clock >= Math.Min(frames, stream.TargetFrames);
        }

        return true;
    }

    void RefuseWhileRendering() {
        if (Engine.IsOffline) {
            throw new InvalidOperationException("An offline render is running.");
        }
    }
}
