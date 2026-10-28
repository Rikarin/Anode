using Anode.Dsp.Instruments;

namespace Anode.HostProbe;

/// <summary>
///     A managed stand-in instrument for the bench while the built-ins are not there: eight sine voices with a linear
///     release, a gain parameter, allocation-free. About the cost of a simple synth voice loop.
/// </summary>
sealed class ProbeSine : IInstrument {
    public const string Id = "probe.sine";
    const int Voices = 8;

    public static readonly InstrumentDescriptor Info = new(
        Id,
        "Probe Sine",
        "Anode",
        "1.0",
        [new(0, "gain", "Gain", 0, 1, 0.5f, string.Empty)],
        []
    );

    readonly int[] keys = new int[Voices];
    readonly double[] phases = new double[Voices];
    readonly double[] steps = new double[Voices];
    readonly float[] levels = new float[Voices];
    readonly float[] targets = new float[Voices];
    float gain = 0.5f;
    double rate = 48_000;
    int next;

    public InstrumentDescriptor Descriptor => Info;

    public void Prepare(float sampleRate, int maxFrames) {
        rate = sampleRate;
        Array.Fill(keys, -1);
    }

    public void SetParameter(int index, float value) {
        if (index == 0) {
            gain = Math.Clamp(value, 0, 1);
        }
    }

    public float GetParameter(int index) => index == 0 ? gain : 0;

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) {
        var e = 0;
        for (var i = 0; i < left.Length; i++) {
            while (e < events.Length && events[e].Frame <= i) {
                Handle(events[e++]);
            }

            var sum = 0f;
            for (var v = 0; v < Voices; v++) {
                if (levels[v] == 0 && targets[v] == 0) {
                    continue;
                }

                levels[v] += Math.Clamp(targets[v] - levels[v], -0.0005f, 0.01f);
                sum += (float)Math.Sin(phases[v]) * levels[v];
                phases[v] += steps[v];
                if (phases[v] > Math.Tau) {
                    phases[v] -= Math.Tau;
                }
            }

            left[i] = right[i] = sum * gain * 0.2f;
        }
    }

    void Handle(in MidiEvent e) {
        var key = e.Data1;
        switch (e.Status & 0xF0) {
            case 0x90 when e.Data2 > 0:
                var v = next++ % Voices;
                keys[v] = key;
                phases[v] = 0;
                steps[v] = Math.Tau * 440 * Math.Pow(2, (key - 69) / 12.0) / rate;
                targets[v] = e.Data2 / 127f;
                break;
            case 0x80:
            case 0x90:
                for (var i = 0; i < Voices; i++) {
                    if (keys[i] == key) {
                        targets[i] = 0;
                        keys[i] = -1;
                    }
                }

                break;
        }
    }

    public void Reset() {
        Array.Fill(keys, -1);
        Array.Clear(levels);
        Array.Clear(targets);
    }

    public bool LoadFile(string path) => false;

    public byte[]? SaveState() => null;

    public bool LoadState(ReadOnlySpan<byte> state) => false;

    public void Dispose() { }
}
