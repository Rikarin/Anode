using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Anode.Audio.Native.NativeMethods;

namespace Anode.Audio.Native;

/// <summary>Native callback timing (around the whole managed call) as a percentage of the buffer period.</summary>
public sealed record CallbackTiming(ulong Callbacks, ulong OverBudget, double MaxPercent, double P99Percent);

/// <summary>
///     A device stream over <c>cth_audio</c> (docs/adr/0002-native-audio-backend.md): opens a device (null or empty
///     for the default output, paired with the default input when it has none), and calls an
///     <see cref="IAudioProcessor" /> on the driver's real-time thread with planar channels.
/// </summary>
public sealed unsafe class NativeAudioSession : IAudioDevice {
    /// <summary>A started stream whose callback has not run for this long is reported as lost.</summary>
    public static readonly TimeSpan CallbackStall = TimeSpan.FromSeconds(2);

    readonly Lock watchdog = new();
    GCHandle self;
    nint stream;
    IAudioProcessor? processor;
    long exceptions;
    Exception? callbackException;
    bool running;
    ulong lastCallbacks;
    long lastCallbackAt;

    NativeAudioSession() { }

    public string Backend => NativeAudio.Backend;

    public int SampleRate { get; private set; }

    public int BufferFrames { get; private set; }

    public int InputChannels { get; private set; }

    public int OutputChannels { get; private set; }

    /// <summary>Linear gain on every output channel. 0 mutes the device (unattended runs); timing is unchanged.</summary>
    public float OutputGain { get; set; } = 1f;

    /// <summary>Exceptions the processor threw on the audio thread; each silenced one callback.</summary>
    public long CallbackExceptions => Interlocked.Read(ref exceptions);

    public (int Input, int Output) Latency {
        get {
            NativeMethods.Stats(stream, out var s);
            return (s.InputLatencyFrames, s.OutputLatencyFrames);
        }
    }

    public long Callbacks {
        get {
            NativeMethods.Stats(stream, out var s);
            return (long)s.Callbacks;
        }
    }

    public long Overloads {
        get {
            NativeMethods.Stats(stream, out var s);
            return (long)s.Overloads;
        }
    }

    /// <summary>
    ///     What the backend reports (<c>cth_audio_status</c>), or <see cref="AudioDeviceStatus.Lost" /> when a started
    ///     stream's callback has stalled for <see cref="CallbackStall" /> (the backstop for a backend that cannot tell).
    /// </summary>
    public AudioDeviceStatus Status {
        get {
            var status = (AudioDeviceStatus)NativeMethods.Status(stream);
            if (status != AudioDeviceStatus.Ok) {
                return status;
            }

            lock (watchdog) {
                if (!running) {
                    return status;
                }

                NativeMethods.Stats(stream, out var s);
                var now = Stopwatch.GetTimestamp();
                if (s.Callbacks != lastCallbacks) {
                    lastCallbacks = s.Callbacks;
                    lastCallbackAt = now;
                    return status;
                }

                return Stopwatch.GetElapsedTime(lastCallbackAt, now) >= CallbackStall ? AudioDeviceStatus.Lost : status;
            }
        }
    }

    /// <summary>
    ///     Opens <paramref name="deviceUid" /> (null or empty: the default output) at <paramref name="sampleRate" /> and
    ///     <paramref name="bufferFrames" /> with up to <paramref name="inputChannels" /> inputs, without starting it.
    ///     The device may settle on other values; read them back.
    /// </summary>
    public static NativeAudioSession Open(string? deviceUid, int sampleRate, int bufferFrames, int inputChannels) {
        var session = new NativeAudioSession();
        session.self = GCHandle.Alloc(session);
        var config = new StreamConfigNative {
            SampleRate = sampleRate, BufferFrames = bufferFrames, InputChannels = inputChannels
        };
        if (NativeMethods.Open(
                string.IsNullOrEmpty(deviceUid) ? null : deviceUid,
                ref config,
                &Callback,
                (void*)GCHandle.ToIntPtr(session.self),
                out session.stream
            )
            != 0) {
            session.self.Free();
            throw new InvalidOperationException($"Could not open the audio device: {LastErrorMessage()}");
        }

        session.SampleRate = (int)Math.Round(config.SampleRate);
        session.BufferFrames = config.BufferFrames;
        session.InputChannels = config.InputChannels;
        session.OutputChannels = config.OutputChannels;
        return session;
    }

    public void Start(IAudioProcessor processor) {
        Volatile.Write(ref this.processor, processor);
        if (NativeMethods.Start(stream) != 0) {
            throw new InvalidOperationException($"Could not start the audio device: {LastErrorMessage()}");
        }

        lock (watchdog) {
            NativeMethods.Stats(stream, out var s);
            lastCallbacks = s.Callbacks;
            lastCallbackAt = Stopwatch.GetTimestamp();
            running = true;
        }
    }

    public void Stop() {
        lock (watchdog) {
            running = false;
        }

        _ = NativeMethods.Stop(stream);
    }

    /// <summary>The native timing histogram since the last reset.</summary>
    public CallbackTiming Timing() {
        TimingNative raw;
        NativeMethods.Timing(stream, &raw);
        ulong count = 0;
        for (var i = 0; i < TimingBins; i++) {
            count += raw.Bins[i];
        }

        var threshold = (ulong)Math.Ceiling(0.99 * count);
        ulong running = 0;
        double p99 = 0;
        for (var i = 0; i < TimingBins && count > 0; i++) {
            running += raw.Bins[i];
            if (running >= threshold) {
                p99 = i + 1;
                break;
            }
        }

        return new(count, raw.OverBudget, raw.MaxPercent, p99);
    }

    public void ResetTiming() => NativeMethods.ResetTiming(stream);

    /// <summary>The first exception the processor threw since the previous call, or null.</summary>
    public Exception? TakeCallbackException() => Interlocked.Exchange(ref callbackException, null);

    public void Dispose() {
        if (stream != 0) {
            Stop();
            Close(stream);
            stream = 0;
        }

        if (self.IsAllocated) {
            self.Free();
        }
    }

    [UnmanagedCallersOnly]
    static void Callback(void* user, float** inputs, int inputChannels, float** outputs, int outputChannels, int frames) {
        var session = Unsafe.As<NativeAudioSession>(GCHandle.FromIntPtr((nint)user).Target!);
        try {
            var processor = Volatile.Read(ref session.processor);
            processor?.Process(inputs, inputChannels, outputs, outputChannels, frames);
            var gain = session.OutputGain;
            if (gain != 1f) {
                for (var c = 0; c < outputChannels; c++) {
                    var channel = outputs[c];
                    for (var i = 0; i < frames; i++) {
                        channel[i] *= gain;
                    }
                }
            }
        }
#pragma warning disable CA1031 // An exception must never unwind into the driver's thread.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref session.exceptions);
            Interlocked.CompareExchange(ref session.callbackException, e, null);
            for (var c = 0; c < outputChannels; c++) {
                new Span<float>(outputs[c], frames).Clear();
            }
        }
    }
}
