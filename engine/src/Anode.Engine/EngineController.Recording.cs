using Anode.Engine.Recording;
using Anode.Engine.Streaming;
using Anode.Ipc;
using System.Globalization;

namespace Anode.Engine;

// Phase 3's recording modes and the preview channel on the control thread (docs/04-phase-3.md, workstream A):
// autopunch, Flashback Capture (its buffers' lifetime, and writing what they kept as takes) and preview files.
public sealed partial class EngineController {
    string? previewPath;
    float previewGain;
    PreviewSync previewSync = PreviewSync.None;

    /// <summary>
    ///     How far ahead of the oldest frame a Flashback Capture starts (at most a quarter of the buffer), so the audio
    ///     thread, which keeps writing while the capture copies, cannot overwrite what is being copied.
    /// </summary>
    const double FlashbackMarginSeconds = 5;

    /// <summary>
    ///     Autopunch: while recording, armed tracks record and hear their input only in [<paramref name="inSample" />,
    ///     <paramref name="outSample" />), and play back elsewhere.
    /// </summary>
    public void SetPunch(bool enabled, long inSample, long outSample) =>
        Send(new(EngineCommandKind.SetPunch, Math.Max(0, inSample), Math.Max(0, outSample), enabled));

    /// <summary>
    ///     Flashback Capture: writes what every armed audio track's input and every MIDI track's live MIDI did since
    ///     playback last started (or up to the stop) as takes at the positions they were played, cycle wraps becoming
    ///     passes. Empty when nothing was played since.
    /// </summary>
    public RecordingResult FlashbackCapture() {
        lock (gate) {
            var (start, end) = Engine.FlashbackWindow;
            if (start < 0 || end <= start) {
                return new([], []);
            }

            if (string.IsNullOrEmpty(RecordDirectory)) {
                throw new InvalidOperationException("No model loaded, so there is no directory to capture into.");
            }

            var history = Engine.History.Since(start); // before the start: linear back from it
            if (history.Count == 0) {
                return new([], []);
            }

            Directory.CreateDirectory(RecordDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            var takes = new List<RecordedTake>();
            foreach (var track in Engine.Root.Tracks) {
                if (track.Flashback is { } memory
                    && Capture(track.Id, memory, start, end, history, stamp) is { } take) {
                    takes.Add(take);
                }
            }

            var (low, high) = Range(history, start, end);
            var close = PositionHistory.PositionAt(history, end - 1) + 1;
            var midi = BuildMidiTakes(Midi.History(start, end), low, high, Math.Clamp(close, low, high));
            return new(takes, midi);
        }
    }

    /// <summary>
    ///     Writes one track's Flashback window [<paramref name="start" />, <paramref name="end" />) (as far as its
    ///     buffer reaches) to a 24-bit WAV take; null when it holds nothing of it or the audio thread overtook the copy.
    /// </summary>
    RecordedTake? Capture(
        int trackId,
        FlashbackBuffer memory,
        long start,
        long end,
        List<PositionHistory.Breakpoint> history,
        string stamp
    ) {
        var latency = Engine.RecordLatency;
        var runStart = memory.RunStart;
        var to = Math.Min(memory.End, end);
        var margin = Math.Min((long)(FlashbackMarginSeconds * Options.SampleRate), memory.Capacity / 4);
        var from = Math.Max(Math.Max(start, runStart), memory.End - memory.Capacity + margin);
        if (runStart < 0 || to <= from) {
            return null;
        }

        // Frames played before the project's zero are dropped (a take that starts within a latency of it).
        var first = PositionHistory.PositionAt(history, from - latency);
        if (first < 0) {
            from = Math.Min(to, from - first);
        }

        // Passes: one per run of the history inside the window.
        var positions = new List<long> { PositionHistory.PositionAt(history, from - latency) };
        var offsets = new List<long> { 0 };
        foreach (var b in history) {
            var at = b.Clock + latency;
            if (at > from && at < to && b.Position != PositionHistory.PositionAt(history, b.Clock - 1) + 1) {
                positions.Add(b.Position);
                offsets.Add(at - from);
            }
        }

        var path = Path.Combine(RecordDirectory, $"{trackId}-{stamp}-flashback.wav");
        for (var n = 1; File.Exists(path); n++) {
            path = Path.Combine(RecordDirectory, $"{trackId}-{stamp}-flashback-{n}.wav");
        }

        var channels = memory.Channels;
        using (var writer = new WavWriter(path, channels, Options.SampleRate)) {
            var chunk = scratch.Length / channels;
            for (var clock = from; clock < to; clock += chunk) {
                var frames = (int)Math.Min(chunk, to - clock);
                var block = scratch.AsSpan(0, frames * channels);
                if (!memory.Read(clock, frames, block)) {
                    writer.Complete();
                    TryDelete(path);
                    Log?.Invoke($"flashback for track {trackId}: playback overtook the capture");
                    return null;
                }

                writer.Write(block);
            }
        }

        return RecordTake.Describe(
            trackId,
            path,
            channels,
            Options.SampleRate,
            to - from,
            [..positions],
            [..offsets],
            positions.Count
        );
    }

    /// <summary>
    ///     The lowest and highest positions the history visited at clocks [<paramref name="start" />,
    ///     <paramref name="end" />).
    /// </summary>
    static (long Low, long High) Range(List<PositionHistory.Breakpoint> history, long start, long end) {
        long low = long.MaxValue, high = long.MinValue;
        for (var i = 0; i < history.Count; i++) {
            var from = Math.Max(start, history[i].Clock);
            var to = Math.Min(end, i + 1 < history.Count ? history[i + 1].Clock : end);
            if (from >= to) {
                continue;
            }

            var position = history[i].Position + (from - history[i].Clock);
            low = Math.Min(low, position);
            high = Math.Max(high, position + (to - from));
        }

        return low > high ? (0, 0) : (Math.Max(0, low), Math.Max(0, high));
    }

    /// <summary>
    ///     How the preview channel plays from now on (Phase 6, <c>setPreviewSync</c>): at <paramref name="tempoRatio" />
    ///     of the file's speed, transposed by <paramref name="transposeSemitones" />, from the next bar while playing
    ///     when <paramref name="startOnBar" />. A file playing now restarts that way.
    /// </summary>
    public void SetPreviewSync(double tempoRatio, int transposeSemitones, bool startOnBar) {
        if (!(tempoRatio > 0) || !double.IsFinite(tempoRatio)) {
            throw new ArgumentException($"Tempo ratio {tempoRatio} is not a positive number.", nameof(tempoRatio));
        }

        lock (gate) {
            previewSync = new(tempoRatio, Math.Clamp(transposeSemitones, -48, 48), startOnBar);
            if (previewPath is not null && Engine.Preview is { Ended: false }) {
                Preview(previewPath, previewGain);
            }
        }
    }

    /// <summary>
    ///     Plays <paramref name="path" /> through the preview channel at linear <paramref name="gain" />, replacing what
    ///     plays there; null stops it. Throws <see cref="IOException" /> or <see cref="InvalidDataException" /> for a
    ///     file it cannot stream (and leaves the current preview playing).
    /// </summary>
    public void Preview(string? path, float gain) {
        lock (gate) {
            previewPath = path;
            previewGain = gain;
            PreviewSource? next = null;
            if (path is not null) {
                var file = new StreamFile(-1, path);
                if (file.Channels > PlaylistRenderer.MaxFileChannels) {
                    file.Dispose();
                    throw new InvalidDataException($"{path}: {file.Channels} channels");
                }

                if (file.Format.SampleRate != Options.SampleRate) {
                    Log?.Invoke($"preview {path}: {file.Format.SampleRate} Hz on a {Options.SampleRate} Hz engine");
                }

                try {
                    next = new(file, gain, Options.SampleRate, previewSync);
                } catch (InvalidOperationException) {
                    file.Dispose(); // the Stretch renderer could not be made
                    throw;
                }
            }

            var previous = Engine.SetPreview(next);
            Streamer.Wake();
            if (previous is null) {
                return;
            }

            var grace = Engine.Grace();
            var diskGrace = Streamer.Grace;
            var spin = new SpinWait();
            var deadline = DateTime.UtcNow + CommandTimeout;
            while ((!Engine.IsGraceOver(grace) || !Streamer.IsGraceOver(diskGrace)) && DateTime.UtcNow < deadline) {
                spin.SpinOnce(-1);
            }

            previous.Dispose();
        }
    }

    /// <summary>
    ///     Gives every armed audio track a Flashback buffer (its first lap faulted in by the writer thread as the audio
    ///     thread nears it) and releases those of tracks no longer armed. Under the gate.
    /// </summary>
    void SyncFlashback() {
        var released = new List<FlashbackBuffer>();
        var changed = false;
        foreach (var track in Engine.Root.Tracks) {
            var channels = track.Stereo ? 2 : 1;
            var memory = track.Flashback;
            var wanted = track.Armed && !track.IsMidi;
            if (wanted && memory?.Channels != channels) {
                track.Flashback = new(channels, flashbackFrames, Options.SampleRate);
                changed = true;
            } else if (!wanted && memory is not null) {
                track.Flashback = null;
                changed = true;
            } else {
                continue;
            }

            if (memory is not null) {
                released.Add(memory);
            }
        }

        if (changed || released.Count > 0) {
            ReleaseFlashback(released);
        }
    }

    /// <summary>
    ///     Points the writer thread at the current tracks' Flashback buffers, then frees <paramref name="released" />
    ///     once neither it nor the audio thread can still touch them.
    /// </summary>
    void ReleaseFlashback(List<FlashbackBuffer> released) {
        var active = new List<FlashbackBuffer>();
        foreach (var track in Engine.Root.Tracks) {
            if (track.Flashback is { } memory) {
                active.Add(memory);
            }
        }

        Writer.Touch([..active]);
        if (released.Count == 0) {
            return;
        }

        WaitGrace();
        foreach (var memory in released) {
            memory.Dispose();
        }
    }

    void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Log?.Invoke($"cannot delete {path}: {e.Message}");
        }
    }
}
