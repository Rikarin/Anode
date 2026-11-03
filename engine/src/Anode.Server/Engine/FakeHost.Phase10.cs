using Anode.Ipc;

namespace Anode.Server.Engine;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): the fake host keeps the monitoring it is sent and announces a master tap ring in
///     its hello; while the tap is on, its telemetry loop writes the master into it as the audio callback would: a
///     1 kHz sine at <see cref="TapAmplitude" /> times the master's gain while playing (silence while stopped), stamped
///     with the transport position, in real time.
/// </summary>
public sealed partial class FakeHost {
    /// <summary>The tap's test tone (the listen-in golden path's 1 kHz).</summary>
    public const double TapFrequency = 1000;

    public const float TapAmplitude = 0.25f;

    MasterTapWriter? tapWriter;
    SetMonitoringMessage? monitoring;
    bool tapping;
    long tapLastTimestamp;
    long tapLastPosition = -1;

    /// <summary>The master tap ring's name (the hello's).</summary>
    public string? MasterTapName => tapWriter?.Name;

    /// <summary>The monitoring last sent, or null.</summary>
    public SetMonitoringMessage? Monitoring {
        get {
            lock (gate) {
                return monitoring;
            }
        }
    }

    /// <summary>Whether the server has the tap on.</summary>
    public bool Tapping {
        get {
            lock (gate) {
                return tapping;
            }
        }
    }

    void CreateTap() {
        try {
            tapWriter = MasterTapWriter.Create(
                "an" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..3]) + "m",
                0,
                SampleRate
            );
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
            tapWriter = null; // no ring: listen-in hears nothing from this host
        }
    }

    /// <summary>The Phase 10 messages; null for any other message, else what it answers (nothing).</summary>
    IpcMessage[]? HandlePhase10(IpcMessage message) {
        switch (message) {
            case SetMonitoringMessage set:
                lock (gate) {
                    monitoring = set;
                }

                return [];
            case SetMasterTapMessage tap:
                lock (gate) {
                    tapping = tap.Enabled;
                    tapLastTimestamp = 0;
                }

                return [];
            default:
                return null;
        }
    }

    /// <summary>
    ///     The channels a render file has (Phase 10): the master's format for the mix; for a stem, its output format, or
    ///     its input format (mono or stereo) for an object, whose stem is its signal before the renderer; stereo for a
    ///     freeze.
    /// </summary>
    int RenderChannels(RenderMessage render, (EngineTrack Track, float Gain)[] sources) {
        lock (gate) {
            var format = render switch {
                { Freeze: true } => EngineChannelFormat.Stereo,
                { Tracks: null } => master.OutputFormat,
                _ when sources is [var (track, _)] => track.Strip.Panner?.Mode == EnginePannerMode.Object
                    ? track.Strip.InputFormat
                    : track.Strip.OutputFormat,
                _ => EngineChannelFormat.Stereo
            };
            return Contracts.SpeakerLayouts.ChannelCount((Contracts.ChannelFormat)format);
        }
    }

    /// <summary>
    ///     A block of <paramref name="channels" />: left and right in the first two (their average for mono), silence in
    ///     the others.
    /// </summary>
    static void Interleave(float[] block, int channels, ReadOnlySpan<float> left, ReadOnlySpan<float> right, float gain) {
        for (var i = 0; i < left.Length; i++) {
            var frame = block.AsSpan(i * channels, channels);
            frame.Clear();
            if (channels == 1) {
                frame[0] = (left[i] + right[i]) * 0.5f * gain;
            } else {
                frame[0] = left[i] * gain;
                frame[1] = right[i] * gain;
            }
        }
    }

    /// <summary>Writes the tap's audio since the last call (from the telemetry loop, under the gate).</summary>
    void WriteTap() {
        if (tapWriter is null || !tapping) {
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (tapLastTimestamp == 0) {
            tapLastTimestamp = now;
            return;
        }

        var frames = (int)Math.Min(
            SampleRate / 4,
            System.Diagnostics.Stopwatch.GetElapsedTime(tapLastTimestamp, now).TotalSeconds * SampleRate
        );
        if (frames <= 0) {
            return;
        }

        tapLastTimestamp = now;
        var playing = state != Ipc.Telemetry.TelemetryTransportState.Stopped;
        var end = Position();
        var start = playing ? Math.Max(0, end - frames) : -1;
        var discontinuity = playing && tapLastPosition >= 0 && start != tapLastPosition;
        tapLastPosition = playing ? end : -1;
        var gain = master.Mute ? 0 : master.Gain;
        var block = new float[frames * 2];
        for (var i = 0; i < frames && playing; i++) {
            var value = (float)(TapAmplitude * gain * Math.Sin(2 * Math.PI * TapFrequency * (start + i) / SampleRate));
            block[i * 2] = value;
            block[i * 2 + 1] = value;
        }

        tapWriter.Write(block, start, playing, discontinuity);
    }
}
