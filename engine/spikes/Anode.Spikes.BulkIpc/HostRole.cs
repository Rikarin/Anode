using System.Diagnostics;
using System.Net.Sockets;

namespace Anode.Spikes.BulkIpc;

/// <summary>
///     The anode-host side: serves one server connection on a Unix domain socket; maps, validates and publishes each
///     segment it is sent, and unmaps a segment once no published root uses it and the audio thread has moved on.
/// </summary>
sealed class HostRole(AudioThread audio) {
    readonly HashSet<MappedSegment> live = [];
    readonly Dictionary<string, MappedSegment> pooled = new(StringComparer.Ordinal);

    ModelRoot? current;
    TrackView[] stagedTracks = [];
    MappedSegment?[] stagedOwners = [];
    long stagedLength;
    long prefaultSum;

    public static int Run(string socketPath, int frames, int sampleRate) {
        Layout.AssertSizes();
        var audio = new AudioThread(frames, sampleRate);
        audio.Start();
        var host = new HostRole(audio);

        if (File.Exists(socketPath)) {
            File.Delete(socketPath);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        using var client = listener.Accept();
        using var stream = new NetworkStream(client, true);
        try {
            while (Framing.Read(stream) is { } message) {
                if (message is QuitRequest) {
                    break;
                }

                Framing.Write(
                    stream,
                    message switch {
                        LoadSegment load => host.Load(load),
                        ResetRequest => host.Reset(),
                        StatsRequest => host.Stats(),
                        _ => throw new InvalidDataException($"Unexpected {message.GetType().Name}.")
                    }
                );
                audio.Phase = 0;
            }
        } finally {
            host.Reset();
            audio.Stop();
            File.Delete(socketPath);
        }

        return 0;
    }

    SegmentLoaded Load(LoadSegment load) {
        var received = Stopwatch.GetTimestamp();
        audio.Phase = 1;
        MappedSegment segment;
        long mapped, prefaulted, verified, locked;
        try {
            if (load.Pooled && pooled.TryGetValue(load.Path, out var existing)) {
                segment = existing;
            } else {
                segment = MappedSegment.Map(load.SegmentId, load.Path, load.Length);
                if (load.Pooled) {
                    segment.Pooled = true;
                    pooled[load.Path] = segment;
                }
            }

            mapped = Stopwatch.GetTimestamp();
            try {
                segment.ValidateLayout();
                prefaultSum += segment.Prefault();
                prefaulted = Stopwatch.GetTimestamp();
                if (load.Verify) {
                    segment.Verify();
                }

                verified = Stopwatch.GetTimestamp();
                if (load.Lock && !segment.TryLock()) {
                    throw new InvalidOperationException("mlock failed.");
                }

                locked = Stopwatch.GetTimestamp();
            } catch {
                if (!segment.Pooled) {
                    segment.Dispose();
                }

                throw;
            }
        } catch (Exception e) when (e is IOException
                                        or InvalidDataException
                                        or InvalidOperationException
                                        or UnauthorizedAccessException) {
            return new(load.SegmentId, load.Version, received, 0, 0, 0, 0, 0, 0, 0, 0, 0, e.Message);
        }

        live.Add(segment);
        Stage(load, segment);
        if (!load.Commit) {
            return new(
                load.SegmentId,
                load.Version,
                received,
                mapped,
                prefaulted,
                verified,
                locked,
                0,
                0,
                0,
                0,
                0,
                null
            );
        }

        var root = new ModelRoot(load.Version, stagedLength, stagedTracks, stagedOwners);
        foreach (var owner in Distinct(root)) {
            owner.RootRefs++;
        }

        stagedTracks = (TrackView[])stagedTracks.Clone();
        stagedOwners = (MappedSegment?[])stagedOwners.Clone();
        var (previous, grace) = audio.Publish(root);
        audio.Phase = 2;
        var published = Stopwatch.GetTimestamp();
        current = root;

        var spin = new SpinWait();
        while (Volatile.Read(ref root.FirstRenderTicks) == 0) {
            spin.SpinOnce(-1);
        }

        audio.WaitForGrace(grace);
        audio.Phase = 3;
        var released = Retire(previous);
        audio.Phase = 0;
        return new(
            load.SegmentId,
            load.Version,
            received,
            mapped,
            prefaulted,
            verified,
            locked,
            published,
            root.FirstUseTicks,
            root.FirstRenderTicks,
            Stopwatch.GetTimestamp(),
            released,
            null
        );
    }

    unsafe void Stage(LoadSegment load, MappedSegment segment) {
        var header = segment.Header;
        if (load.Track < 0) {
            var count = header->TrackCount;
            stagedTracks = new TrackView[count];
            stagedOwners = new MappedSegment?[count];
            for (var i = 0; i < count; i++) {
                stagedTracks[i] = segment.Track(i);
                stagedOwners[i] = segment;
            }

            stagedLength = header->LengthSamples;
            return;
        }

        if (load.Track >= stagedTracks.Length) {
            Array.Resize(ref stagedTracks, load.Track + 1);
            Array.Resize(ref stagedOwners, load.Track + 1);
        }

        stagedTracks[load.Track] = segment.Track(0);
        stagedOwners[load.Track] = segment;
        stagedLength = Math.Max(stagedLength, header->LengthSamples);
    }

    /// <summary>Drops a retired root's references and unmaps what nothing published uses any more.</summary>
    int Retire(ModelRoot? previous) {
        if (previous is null) {
            return 0;
        }

        var released = 0;
        foreach (var owner in Distinct(previous)) {
            if (--owner.RootRefs == 0 && !owner.Pooled) {
                owner.Dispose();
                live.Remove(owner);
                released++;
            }
        }

        return released;
    }

    StatsReply Reset() {
        var (previous, grace) = audio.Publish(null);
        audio.WaitForGrace(grace);
        Retire(previous);
        current = null;
        stagedTracks = [];
        stagedOwners = [];
        stagedLength = 0;
        var leaked = live.Count;
        foreach (var segment in live) {
            segment.Dispose();
        }

        live.Clear();
        pooled.Clear();
        audio.ResetStats();
        return Stats() with { MappedSegments = leaked };
    }

    StatsReply Stats() {
        long bytes = 0;
        foreach (var segment in live) {
            bytes += segment.Length;
        }

        return new(
            audio.Callbacks,
            audio.LateCallbacks,
            audio.SlowCallbacks,
            audio.SlowByPhase,
            audio.MaxLatenessMicros,
            audio.MaxCallbackMicros,
            audio.AllocatedBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            live.Count,
            bytes,
            Environment.WorkingSet,
            OpenHandles(),
            audio.Realtime,
            audio.Sink + (current?.Version ?? 0) + prefaultSum
        );
    }

    static HashSet<MappedSegment> Distinct(ModelRoot root) {
        var set = new HashSet<MappedSegment>();
        foreach (var owner in root.Owners) {
            if (owner is not null) {
                set.Add(owner);
            }
        }

        return set;
    }

    static int OpenHandles() {
        if (OperatingSystem.IsWindows()) {
            using var self = Process.GetCurrentProcess();
            return self.HandleCount;
        }

        var directory = OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd";
        return Directory.GetFileSystemEntries(directory).Length;
    }
}
