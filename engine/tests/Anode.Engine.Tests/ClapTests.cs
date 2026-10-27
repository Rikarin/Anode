using Anode.Dsp.Instruments;
using Anode.Engine.Plugins;
using Anode.Engine.Plugins.Clap;
using System.Buffers.Binary;

namespace Anode.Engine.Tests;

/// <summary>
///     CLAP hosting against the test plugin native/build.sh builds (engine/native/an_testclap): the scan, parameters,
///     note and MIDI events at their frames, state, threading as the plugin checks it, and an instrument track playing
///     it in the engine.
/// </summary>
public sealed class ClapTests : IDisposable {
    const int Rate = 48_000;
    readonly List<string> log = [];
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    void Log(string message) {
        lock (log) {
            log.Add(message);
        }
    }

    string[] Misbehaving() {
        lock (log) {
            return [..log.Where(static m => m.Contains("misbehaving", StringComparison.Ordinal))];
        }
    }

    static string Plugin => Path.Combine(MidiFixtures.ClapFolder, "anode-test-sine.clap");

    [Fact]
    public void The_scan_finds_the_test_plugin_and_reads_its_parameters() {
        Assert.Contains(Plugin, PluginRegistry.ClapFiles([MidiFixtures.ClapFolder]));
        var registry = new PluginRegistry { Log = Log };
        registry.ScanClap([MidiFixtures.ClapFolder, Path.Combine(dir, "missing")]);
        var entry = Assert.Single(registry.Catalog(), static p => p.Format == "clap");
        Assert.Equal(
            (MidiFixtures.ClapId, "Anode Test Sine", "Anode", "1.0.0", true),
            (entry.Id, entry.Name, entry.Vendor, entry.Version, entry.IsInstrument)
        );
        Assert.Equal(
            [("1", "Gain", 0.0, 1.0, 0.5, 0), ("7", "Octave", -2.0, 2.0, 0.0, 4)],
            entry.Parameters.Select(static p => (p.Id, p.Name, p.Min, p.Max, p.Default, p.Steps))
        );
        Assert.Empty(Misbehaving());
    }

    [Fact]
    public void A_clap_instrument_plays_notes_at_their_frames_with_its_parameters() {
        using var clap = ClapInstrument.Create(Plugin, MidiFixtures.ClapId, Log);
        Assert.Equal(["1", "7"], clap.Descriptor.Parameters.Select(static p => p.Id));
        clap.Prepare(Rate, 4096);
        clap.SetParameter(0, 0.8f); // before activation: flushed by Activate
        clap.Activate();
        Assert.Equal(0.8f, clap.GetParameter(0));
        Assert.Equal(0, clap.LatencyFrames);
        var left = new float[512];
        var right = new float[512];
        AudioThread.Mark();
        try {
            clap.Process([new(10, 0x90, 69, 127)], left, right);
            Assert.All(left[..10], static s => Assert.Equal(0f, s));
            Assert.True(left[11..].Max() > 0.1f);
            Assert.Equal(left, right);

            // 440 Hz: about 88 zero crossings in a tenth of a second.
            var tenth = Render(clap, 4_800);
            Assert.InRange(Crossings(tenth), 86, 90);
            var level = tenth.Max();

            // Gain through the audio thread's queue; Octave +1 doubles the next note's pitch.
            clap.SetParameter(0, 0.4f);
            clap.SetParameter(1, 1f);
            clap.Process([new(0, 0x80, 69, 0), new(0, 0x90, 69, 127)], left, right);
            var octave = Render(clap, 4_800);
            Assert.InRange(Crossings(octave), 174, 178);
            Assert.InRange(octave.Max(), level * 0.45f, level * 0.55f);
            Assert.Equal(1f, clap.GetParameter(1));

            // Raw MIDI reaches it too: All Notes Off silences it.
            clap.Process([new(0, 0xB0, 123, 0)], left, right);
            Assert.True(Render(clap, 512).Max() == 0f);
        } finally {
            AudioThread.Mark(false);
        }

        Assert.Equal(0, clap.AudioThreadLogs);
        Assert.Empty(Misbehaving());
    }

    [Fact]
    public void State_round_trips_and_threading_is_as_clap_asks() {
        byte[] saved;
        using (var clap = ClapInstrument.Create(Plugin, MidiFixtures.ClapId, Log)) {
            clap.Prepare(Rate, 4096);
            clap.Activate();
            AudioThread.Mark();
            try {
                clap.SetParameter(0, 0.25f);
                clap.SetParameter(1, -1f);
                clap.Process([new(0, 0x90, 60, 100), new(5, 0x90, 64, 100), new(9, 0x90, 67, 100)], new float[64], new float[64]);
            } finally {
                AudioThread.Mark(false);
            }

            saved = clap.SaveState()!;
            Assert.Equal(0x53544E41u, BinaryPrimitives.ReadUInt32LittleEndian(saved));
            Assert.Equal(0.25, BinaryPrimitives.ReadDoubleLittleEndian(saved.AsSpan(8)));
            Assert.Equal(-1.0, BinaryPrimitives.ReadDoubleLittleEndian(saved.AsSpan(16)));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(saved.AsSpan(24))); // notes played
        }

        using (var restored = ClapInstrument.Create(Plugin, MidiFixtures.ClapId, Log)) {
            restored.Prepare(Rate, 4096);
            Assert.True(restored.LoadState(saved));
            Assert.Equal((0.25f, -1f), (restored.GetParameter(0), restored.GetParameter(1)));
            restored.Activate();
            Assert.Equal(saved, restored.SaveState());
            Assert.False(restored.LoadState([1, 2, 3]));
        }

        // Every call came on the thread CLAP wants it on, and destroying stopped processing first.
        Assert.Empty(Misbehaving());
    }

    [Fact]
    public void An_instrument_track_plays_the_clap_plugin_and_reports_its_state() {
        var registry = new PluginRegistry { Log = Log };
        registry.ScanClap([MidiFixtures.ClapFolder]);
        using var controller = new EngineController(new(Rate, 64), registry) { Log = Log };
        using (var segment = TestAudio.Segment([new(1, MidiFixtures.Notes((0, 48_000, 69)), [])], 1)) {
            controller.LoadModel(
                TestAudio.Load(
                    segment,
                    1,
                    true,
                    [
                        MidiFixtures.InstrumentTrack(
                            1,
                            new(MidiFixtures.ClapId, new Dictionary<string, double> { ["1"] = 1.0, ["7"] = 0 })
                        )
                    ],
                    [],
                    dir
                )
            );
        }

        using var driver = new OfflineDriver(controller, 64);
        driver.PlayFrom(0);
        try {
            var (left, _) = driver.Render(4_800);
            Assert.InRange(Crossings(left), 86, 90);
            Assert.InRange(left.Max(), 0.17f, 0.22f); // gain 1 × velocity 100/127 × 0.25
            Assert.True(controller.SetParameter(1, "1", 0.5));
            Assert.False(controller.SetParameter(1, "2", 0.5));
            driver.Render(64);
            var (quieter, _) = driver.Render(4_800);
            Assert.InRange(quieter.Max(), left.Max() * 0.45f, left.Max() * 0.55f);
        } finally {
            AudioThread.Mark(false); // the offline driver ran the callback on this thread
        }

        var state = Assert.Single(controller.InstrumentStates());
        var bytes = Convert.FromBase64String(state.State!);
        Assert.Equal(0.5, BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Empty(Misbehaving());
    }

    static float[] Render(IInstrument instrument, int frames) {
        var left = new float[frames];
        var right = new float[frames];
        instrument.Process([], left, right);
        return left;
    }

    static int Crossings(float[] samples) {
        var count = 0;
        for (var i = 1; i < samples.Length; i++) {
            if ((samples[i - 1] < 0) != (samples[i] < 0)) {
                count++;
            }
        }

        return count;
    }
}
