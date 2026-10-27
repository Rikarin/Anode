using Anode.Audio.Native;
using System.Runtime.InteropServices;

namespace Anode.Audio;

/// <summary>
///     A device-less backend: a real-time thread that calls the processor every buffer period, to absolute deadlines,
///     with silent inputs (or a test tone) and discarded outputs. Used on machines without a sound card (CI, the
///     Playwright golden path) and for soak tests.
/// </summary>
/// <remarks>
///     Spike S2 found that a plain high-priority thread ticking at 1.3 ms was preempted for up to 6.5 ms, so the
///     thread takes the time-constraint policy (macOS) or SCHED_FIFO (Linux) through the native shim and sleeps with
///     <c>mach_wait_until</c> / <c>clock_nanosleep</c>. A tick that starts more than a period late is counted in
///     <see cref="Overloads" /> and the clock realigns instead of bursting to catch up.
/// </remarks>
public sealed unsafe class NullAudioDevice : IAudioDevice {
    readonly float* memory;
    readonly float** inputs;
    readonly float** outputs;
    readonly bool inputTone;
    Thread? thread;
    IAudioProcessor? processor;
    volatile bool running;
    long callbacks;
    long late;
    long exceptions;
    double tonePhase;

    /// <param name="sampleRate">Frames per second.</param>
    /// <param name="bufferFrames">Frames per callback.</param>
    /// <param name="inputChannels">Input channels the processor sees.</param>
    /// <param name="outputChannels">Output channels the processor writes (and the device discards).</param>
    /// <param name="inputTone">Feeds a 440 Hz sine at −20 dBFS on every input instead of silence.</param>
    public NullAudioDevice(
        int sampleRate,
        int bufferFrames,
        int inputChannels = 2,
        int outputChannels = 2,
        bool inputTone = false
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferFrames, 4096);
        SampleRate = sampleRate;
        BufferFrames = bufferFrames;
        InputChannels = Math.Max(0, inputChannels);
        OutputChannels = Math.Max(1, outputChannels);
        this.inputTone = inputTone;
        var channels = InputChannels + OutputChannels;
        memory = (float*)NativeMemory.AlignedAlloc((nuint)(channels * bufferFrames * sizeof(float)), 64);
        NativeMemory.Clear(memory, (nuint)(channels * bufferFrames * sizeof(float)));
        inputs = (float**)NativeMemory.Alloc((nuint)Math.Max(1, InputChannels), (nuint)sizeof(float*));
        outputs = (float**)NativeMemory.Alloc((nuint)OutputChannels, (nuint)sizeof(float*));
        for (var c = 0; c < InputChannels; c++) {
            inputs[c] = memory + c * bufferFrames;
        }

        for (var c = 0; c < OutputChannels; c++) {
            outputs[c] = memory + (InputChannels + c) * bufferFrames;
        }
    }

    public string Backend => "null";

    public int SampleRate { get; }

    public int BufferFrames { get; }

    public int InputChannels { get; }

    public int OutputChannels { get; }

    public (int Input, int Output) Latency => (0, 0);

    public long Callbacks => Interlocked.Read(ref callbacks);

    public long Overloads => Interlocked.Read(ref late);

    public long CallbackExceptions => Interlocked.Read(ref exceptions);

    /// <summary>True once the clock thread runs with a real-time policy.</summary>
    public bool IsRealtime { get; private set; }

    public AudioDeviceStatus Status => AudioDeviceStatus.Ok;

    public void Start(IAudioProcessor processor) {
        if (running) {
            throw new InvalidOperationException("Already running.");
        }

        this.processor = processor;
        running = true;
        thread = new(Run) { IsBackground = true, Name = "anode null audio", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    public void Stop() {
        running = false;
        thread?.Join();
        thread = null;
    }

    public void Dispose() {
        Stop();
        NativeMemory.AlignedFree(memory);
        NativeMemory.Free(inputs);
        NativeMemory.Free(outputs);
    }

    void Run() {
        IsRealtime = Realtime.TryPromoteCurrentThread(BufferFrames, SampleRate);
        var period = (long)(BufferFrames * 1e9 / SampleRate);
        var deadline = Realtime.NowNanoseconds() + period;
        var processor = this.processor!;
        while (running) {
            Realtime.SleepUntil(deadline);
            var now = Realtime.NowNanoseconds();
            if (now - deadline > period) {
                Interlocked.Increment(ref late);
                deadline = now; // a device would have dropped out; realign rather than burst
            }

            deadline += period;
            if (inputTone) {
                FillTone();
            }

            for (var c = 0; c < OutputChannels; c++) {
                new Span<float>(outputs[c], BufferFrames).Clear();
            }

            try {
                processor.Process(inputs, InputChannels, outputs, OutputChannels, BufferFrames);
            }
#pragma warning disable CA1031 // The clock keeps running, as a driver's thread would.
            catch (Exception) {
#pragma warning restore CA1031
                Interlocked.Increment(ref exceptions);
            }

            Interlocked.Increment(ref callbacks);
        }
    }

    void FillTone() {
        var step = 2 * Math.PI * 440 / SampleRate;
        for (var i = 0; i < BufferFrames; i++) {
            var sample = (float)(0.1 * Math.Sin(tonePhase));
            tonePhase += step;
            for (var c = 0; c < InputChannels; c++) {
                inputs[c][i] = sample;
            }
        }

        tonePhase %= 2 * Math.PI;
    }
}
