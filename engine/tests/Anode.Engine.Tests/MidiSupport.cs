using Anode.Audio.Midi;
using Anode.Dsp.Instruments;
using Anode.Engine.Plugins;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     The instrument the engine tests host instead of the built-ins (workstream D's, not landed when these were
///     written): a sine per held note, a gain parameter and a state. It logs what it was asked to do into
///     preallocated arrays, so the allocation test can run it too.
/// </summary>
sealed class TestInstrument : IInstrument {
    public const string Id = "test.sine";
    public const int LogCapacity = 8192;

    public static readonly InstrumentDescriptor Info = new(
        Id,
        "Test Sine",
        "Anode tests",
        "1.0",
        [
            new(0, "gain", "Gain", 0, 1, 0.5f, string.Empty),
            new(1, "shape", "Shape", 0, 2, 0, string.Empty, 2, ["sine", "square", "saw"])
        ],
        [".txt"]
    );

    readonly float[] parameters = [0.5f, 0];
    readonly double[] phases = new double[128];
    readonly bool[] held = new bool[128];
    double rate = 48_000;
    long blocks;

    public InstrumentDescriptor Descriptor => Info;

    public bool Prepared { get; private set; }

    public bool Disposed { get; private set; }

    public string? File { get; private set; }

    public byte[]? State { get; set; }

    public int Resets { get; private set; }

    public int Processed { get; private set; }

    /// <summary>Every event it received: the block it came in (counting from 0), its frame and bytes.</summary>
    public (long Block, MidiEvent Event)[] Log { get; } = new (long, MidiEvent)[LogCapacity];

    public int Logged { get; private set; }

    public IEnumerable<MidiEvent> Events => Log.Take(Logged).Select(static e => e.Event);

    /// <summary>Notes sounding now.</summary>
    public int Sounding => held.Count(static h => h);

    public void Prepare(float sampleRate, int maxFrames) {
        rate = sampleRate;
        Prepared = true;
    }

    public void SetParameter(int index, float value) {
        if ((uint)index < (uint)parameters.Length) {
            parameters[index] = Math.Clamp(value, Info.Parameters[index].Min, Info.Parameters[index].Max);
        }
    }

    public float GetParameter(int index) => (uint)index < (uint)parameters.Length ? parameters[index] : 0;

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) {
        var next = 0;
        for (var i = 0; i < left.Length; i++) {
            while (next < events.Length && events[next].Frame <= i) {
                Handle(events[next++]);
            }

            double sum = 0;
            for (var n = 0; n < 128; n++) {
                if (!held[n]) {
                    continue;
                }

                sum += Math.Sin(phases[n]);
                phases[n] += 2 * Math.PI * 440 * Math.Pow(2, (n - 69) / 12.0) / rate;
            }

            left[i] = right[i] = (float)(sum * parameters[0] * 0.2);
        }

        while (next < events.Length) {
            Handle(events[next++]);
        }

        blocks++;
        Processed++;
    }

    void Handle(in MidiEvent e) {
        if (Logged < LogCapacity) {
            Log[Logged++] = (blocks, e);
        }

        switch (e.Status & 0xF0) {
            case 0x90 when e.Data2 > 0:
                held[e.Data1] = true;
                phases[e.Data1] = 0;
                break;
            case 0x80:
            case 0x90:
                held[e.Data1] = false;
                break;
        }
    }

    public void Reset() {
        Array.Clear(held);
        Resets++;
    }

    public bool LoadFile(string path) {
        File = path;
        return true;
    }

    public byte[]? SaveState() => State;

    public bool LoadState(ReadOnlySpan<byte> state) {
        State = [..state];
        return true;
    }

    public void ClearLog() => Logged = 0;

    public void Dispose() => Disposed = true;
}

/// <summary>A MIDI backend with ports and input the test sets, recording what is sent.</summary>
sealed class FakeMidi : IMidiBackend {
    readonly Lock gate = new();
    readonly Queue<MidiInputMessage> input = new();
    List<MidiPortInfo> ports = [new("in-a", "Keys A", true), new("in-b", "Keys B", true), new("out-a", "Synth", false)];

    public bool SchedulesOutput { get; set; } = true;

    public int Opens { get; private set; }

    public List<(string Uid, byte[] Message, long TimeNs)> Sent { get; } = [];

    /// <summary>When each message of <see cref="Sent" /> was handed over, on the same clock as its time.</summary>
    public List<long> SentAt { get; } = [];

    public void SetPorts(IEnumerable<MidiPortInfo> list) {
        lock (gate) {
            ports = [..list];
        }
    }

    public void Play(int source, byte status, byte data1, byte data2, long timeNs = 0) {
        lock (gate) {
            input.Enqueue(new(timeNs, status, data1, data2, source));
        }
    }

    public int PortCount() {
        lock (gate) {
            return ports.Count;
        }
    }

    public IReadOnlyList<MidiPortInfo> Ports() {
        lock (gate) {
            return [..ports];
        }
    }

    public bool Open() {
        Opens++;
        return true;
    }

    public int Read(Span<MidiInputMessage> into) {
        lock (gate) {
            var n = 0;
            while (n < into.Length && input.TryDequeue(out var m)) {
                into[n++] = m;
            }

            return n;
        }
    }

    public bool Send(string uid, ReadOnlySpan<byte> message, long timeNs) {
        lock (gate) {
            Sent.Add((uid, message.ToArray(), timeNs));
            SentAt.Add((long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e9 / System.Diagnostics.Stopwatch.Frequency)));
        }

        return true;
    }

    public void ClearSent() {
        lock (gate) {
            Sent.Clear();
            SentAt.Clear();
        }
    }

    public (string Uid, byte[] Message, long TimeNs)[] SentNow() {
        lock (gate) {
            return [..Sent];
        }
    }

    public void Dispose() { }
}

static class MidiFixtures {
    public static PlaybackEvent Note(long position, int note, bool on, int velocity = 100, int channel = 0) =>
        new() {
            SamplePos = position,
            Track = 0,
            Kind = on ? PlaybackEventKind.NoteOn : PlaybackEventKind.NoteOff,
            Data1 = (byte)note,
            Data2 = (byte)(on ? velocity : 0),
            Channel = (byte)channel
        };

    /// <summary>Notes as region events: (start, length, pitch), note-offs before note-ons at equal samples.</summary>
    public static PlaybackEvent[] Notes(params (long Start, long Length, int Pitch)[] notes) => [
        ..notes.SelectMany(static n => new[] { Note(n.Start, n.Pitch, true), Note(n.Start + n.Length, n.Pitch, false) })
            .OrderBy(static e => e.SamplePos)
            .ThenBy(static e => e.Kind == PlaybackEventKind.NoteOn ? 1 : 0)
    ];

    public static EngineInstrument Instrument(
        string plugin = TestInstrument.Id,
        double gain = 0.5,
        string? file = null,
        string? state = null
    ) =>
        new(plugin, new Dictionary<string, double> { ["gain"] = gain, ["shape"] = 0 }, file, state);

    public static EngineTrack InstrumentTrack(
        int id,
        EngineInstrument? instrument = null,
        bool armed = false,
        EngineMidiIo? midi = null
    ) =>
        new(id, -1, true, armed, false, TestAudio.Unity, instrument ?? Instrument(), midi);

    public static EngineTrack ExternalTrack(int id, string output, int channel, bool armed = false) =>
        new(id, -1, false, armed, false, TestAudio.Unity, null, new(null, 0, output, channel));

    /// <summary>A registry whose test instrument instances are kept for the test to inspect.</summary>
    public static (PluginRegistry Registry, List<TestInstrument> Created) Registry() {
        var created = new List<TestInstrument>();
        var registry = new PluginRegistry();
        registry.AddBuiltin(
            TestInstrument.Info,
            () => {
                var instrument = new TestInstrument();
                lock (created) {
                    created.Add(instrument);
                }

                return instrument;
            }
        );
        return (registry, created);
    }

    /// <summary>engine/artifacts/native/clap, where native/build.sh puts the test CLAP plugin.</summary>
    public static string ClapFolder {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "native"))) {
                dir = dir.Parent;
            }

            return dir is null
                ? throw new InvalidOperationException("engine/artifacts/native not found: run engine/native/build.sh")
                : Path.Combine(dir.FullName, "artifacts", "native", "clap");
        }
    }

    public const string ClapId = "org.anode.test-sine";
}
