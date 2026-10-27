namespace Anode.Audio;

/// <summary>
///     What a device calls on its real-time thread: planar float channels, the outputs zeroed on entry. An
///     implementation must not allocate, lock, block or throw.
/// </summary>
public unsafe interface IAudioProcessor {
    void Process(float** inputs, int inputChannels, float** outputs, int outputChannels, int frames);
}

/// <summary>
///     A running source of audio callbacks: a device stream (<see cref="Native.NativeAudioSession" />) or the null
///     device's real-time clock (<see cref="NullAudioDevice" />).
/// </summary>
public interface IAudioDevice : IDisposable {
    /// <summary><c>coreaudio</c>, <c>wasapi</c>, <c>alsa</c>, <c>jack</c> or <c>null</c>.</summary>
    string Backend { get; }

    int SampleRate { get; }

    int BufferFrames { get; }

    int InputChannels { get; }

    int OutputChannels { get; }

    /// <summary>Input and output latency in frames, each including one buffer, as the driver reports them.</summary>
    (int Input, int Output) Latency { get; }

    /// <summary>Callbacks so far; read without allocating.</summary>
    long Callbacks { get; }

    /// <summary>Driver-reported overloads (dropouts), or late ticks of the null clock; read without allocating.</summary>
    long Overloads { get; }

    /// <summary>Whether the stream is still the one the engine was prepared for; read without allocating.</summary>
    Native.AudioDeviceStatus Status { get; }

    /// <summary>Starts calling <paramref name="processor" />.</summary>
    void Start(IAudioProcessor processor);

    void Stop();
}
