using Anode.Contracts;
using System.Runtime.InteropServices;

namespace Anode.Media.Opus;

/// <summary>
///     Turns the master tap's stereo (at the project's rate, with transport positions) into listen-in frames
///     (docs/11-phase-10.md, workstream E): resampled to 48 kHz when the project runs at another rate
///     (<see cref="StreamResampler" />, which delays the stream by its look-ahead but keeps positions exact), cut into
///     20 ms frames, Opus-encoded, each prefixed with the <see cref="ListenIn" /> header: the sequence (0, 1, … from
///     construction), the transport sample of the frame's first sample at the project's rate (from the latest
///     <see cref="Write" /> at or before it, −1 while that was stopped), <see cref="ListenIn.FlagPlaying" /> while
///     playing, and <see cref="ListenIn.FlagDiscontinuity" /> on the first frame and the first after a write marked as a
///     discontinuity (whose earlier audio, not yet a whole frame, is dropped). One per listener connection; not
///     thread-safe.
/// </summary>
public sealed class ListenInFramer : IDisposable {
    const int Channels = ListenIn.Channels;
    const int FrameSamples = ListenIn.FrameSamples;

    readonly int projectSampleRate;
    readonly OpusEncoder encoder = new();

    /// <summary>Encoded-rate audio not yet framed, interleaved; its first frame is output sample <see cref="pendingStart" />.</summary>
    readonly List<float> pending = [];

    /// <summary>Where each write's audio starts in the input (frames since the last reset) and its transport position.</summary>
    readonly List<Stamp> stamps = [];

    StreamResampler resampler;
    long pendingStart;
    long inputFrames;
    uint sequence;
    bool discontinuity = true;

    public ListenInFramer(int projectSampleRate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectSampleRate);
        this.projectSampleRate = projectSampleRate;
        resampler = new(projectSampleRate, ListenIn.SampleRate);
        Format = new(ListenIn.SampleRate, Channels, FrameSamples, ListenIn.Bitrate, projectSampleRate);
    }

    /// <summary>The first (text) message of the socket.</summary>
    public ListenInFormat Format { get; }

    /// <summary>
    ///     Adds tap audio: <paramref name="interleaved" /> stereo frames whose first is project sample
    ///     <paramref name="transportSample" /> (−1 while stopped); <paramref name="discontinuity" /> after a locate or
    ///     an overrun.
    /// </summary>
    public void Write(ReadOnlySpan<float> interleaved, long transportSample, bool playing, bool discontinuity) {
        if (interleaved.Length % Channels != 0) {
            throw new ArgumentException("Stereo frames: an even number of samples.", nameof(interleaved));
        }

        if (discontinuity) {
            resampler = new(projectSampleRate, ListenIn.SampleRate);
            pending.Clear();
            stamps.Clear();
            pendingStart = 0;
            inputFrames = 0;
            this.discontinuity = true;
        }

        stamps.Add(new(inputFrames, playing ? transportSample : -1, playing && transportSample >= 0));
        inputFrames += interleaved.Length / Channels;
        resampler.Process(interleaved, pending);
    }

    /// <summary>
    ///     The next complete frame (header and packet) into <paramref name="frame" /> (at least
    ///     <see cref="ListenIn.HeaderBytes" /> + <see cref="OpusEncoder.MaxPacketBytes" />); its length, or 0 when fewer
    ///     than 20 ms are buffered.
    /// </summary>
    public int Read(Span<byte> frame) {
        ArgumentOutOfRangeException.ThrowIfLessThan(frame.Length, ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes);
        if (pending.Count < FrameSamples * Channels) {
            return 0;
        }

        // The frame's first sample sits at input time pendingStart · from / to.
        var position = (double)pendingStart * projectSampleRate / ListenIn.SampleRate;
        var at = (long)Math.Floor(position + 1e-9);
        var stamp = StampAt(at);
        var transport = stamp.Transport < 0 ? -1 : stamp.Transport + (at - stamp.Input);
        var flags =
            (byte)((stamp.Playing ? ListenIn.FlagPlaying : 0) | (discontinuity ? ListenIn.FlagDiscontinuity : 0));
        var samples = CollectionsMarshal.AsSpan(pending)[..(FrameSamples * Channels)];
        var length = encoder.Encode(samples, frame[ListenIn.HeaderBytes..]);
        ListenIn.WriteHeader(frame, sequence, transport, length, flags);
        sequence++;
        discontinuity = false;
        pending.RemoveRange(0, FrameSamples * Channels);
        pendingStart += FrameSamples;
        return ListenIn.HeaderBytes + length;
    }

    public void Dispose() => encoder.Dispose();

    /// <summary>The latest stamp at or before input frame <paramref name="input" />; older ones are dropped.</summary>
    Stamp StampAt(long input) {
        var index = 0;
        while (index + 1 < stamps.Count && stamps[index + 1].Input <= input) {
            index++;
        }

        if (index > 0) {
            stamps.RemoveRange(0, index);
        }

        return stamps[0];
    }

    readonly record struct Stamp(long Input, long Transport, bool Playing);
}
