using Anode.Dsp.MidiEffects;
using Anode.Dsp.Tests.Support;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Tests.Scripter;

/// <summary>Scheduled events land on the sample: by time at any tempo, by beat through tempo ramps and jumps.</summary>
public sealed class ScripterTimingTests {
    const double Rate = Scripts.Rate;

    /// <summary>The script of the Phase 7 exit criterion: each note plus the note an interval above, 100 ms later.</summary>
    public const string OctaveEcho = """
                                     var PluginParameters = [
                                       { name: "Interval", type: "lin", minValue: -24, maxValue: 24, numberOfSteps: 48, defaultValue: 12 }
                                     ];

                                     function HandleMIDI(event) {
                                       event.send();
                                       if (event instanceof Note) {
                                         var echo = event instanceof NoteOn ? new NoteOn(event) : new NoteOff(event);
                                         echo.pitch = MIDI.normalizeData(event.pitch + GetParameter("Interval"));
                                         echo.sendAfterMilliseconds(100);
                                         Trace("echo " + echo.pitch);
                                       }
                                     }
                                     """;

    [Theory]
    [InlineData(60, 64)]
    [InlineData(120, 64)]
    [InlineData(137.5, 100)]
    [InlineData(200, 512)]
    [InlineData(93, 1)]
    public void SendAfterMilliseconds_lands_on_the_sample_at_any_tempo(double bpm, int block) {
        using var effect = Scripts.Create(
            """
            var delays = [0, 1, 10.5, 250, 1000];
            function HandleMIDI(e) {
              for (var i = 0; i < delays.length; i++) {
                var on = new NoteOn(e); on.pitch = 40 + i; on.sendAfterMilliseconds(delays[i]);
              }
            }
            """
        );
        var output = Scripts.Run(effect, 60_000, [TimedEvent.On(777, 60)], bpm, block: block);
        Assert.Equal(
            [777, 777 + 48, 777 + 504, 777 + 12_000, 777 + 48_000],
            output.OrderBy(static e => e.Data1).Select(static e => e.Frame)
        );
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(174)]
    public void The_exit_criterion_script_echoes_an_octave_up_100_ms_later(double bpm) {
        using var effect = Scripts.Create(OctaveEcho);
        var output = Scripts.Run(effect, 48_000, [TimedEvent.On(1_000, 60, 90), TimedEvent.Off(30_000, 60)], bpm);
        Assert.Equal(
            [
                new(1_000, 0x90, 60, 90), new(5_800, 0x90, 72, 90), new(30_000, 0x80, 60, 0),
                new(34_800, 0x80, 72, 0)
            ],
            output
        );
        Assert.Equal(["echo 72", "echo 72"], Scripts.Trace(effect));

        effect.SetParameter(0, 7); // the slider
        output = Scripts.Run(effect, 9_600, [TimedEvent.On(10, 50)], bpm);
        Assert.Equal([new(10, 0x90, 50, 100), new(4_810, 0x90, 57, 100)], output);
    }

    [Fact]
    public void SendAtBeat_lands_on_the_first_sample_at_or_after_the_beat_through_a_tempo_ramp() {
        using var effect = Scripts.Create(
            """
            var beats = [1.5, 2, 3.5, 5, 6.25, 9.125];
            var sent = false;
            function ProcessMIDI() {
              if (sent) return;
              sent = true;
              for (var i = 0; i < beats.length; i++) {
                var on = new NoteOn; on.pitch = 60 + i; on.sendAtBeat(beats[i]);
              }
            }
            """
        );
        var contexts = MidiRun.Contexts(5 * 48_000, static b => 100 + b * 0.5, block: 64);
        var output = MidiRun.Run([effect], contexts, []);
        double[] beats = [1.5, 2, 3.5, 5, 6.25, 9.125];
        Assert.Equal(
            beats.Select(b => FirstSampleAt(contexts, b - 1)),
            output.OrderBy(static e => e.Data1).Select(static e => e.Frame)
        );
        Assert.Equal(0, effect.PendingEvents);
    }

    [Fact]
    public void SendAfterBeats_counts_from_the_event() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              e.send();
              var later = new NoteOn(e); later.pitch += 1; later.sendAfterBeats(0.5);
              var fresh = new NoteOn; fresh.pitch = 10; fresh.sendAfterBeats(1); // new events start at the handled one
            }
            """
        );
        var output = Scripts.Run(effect, 48_000, [TimedEvent.On(1_000, 60)]);
        Assert.Equal([new(1_000, 0x90, 60, 100), new(13_000, 0x90, 61, 100), new(25_000, 0x90, 10, 100)], output);
    }

    [Fact]
    public void While_stopped_beats_run_at_the_tempo() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              var later = new NoteOn(e); later.sendAfterBeats(1);
              var past = new NoteOn(e); past.pitch = 1; past.sendAtBeat(0.5);
            }
            """
        );
        var output = Scripts.Run(effect, 48_000, [TimedEvent.On(100, 60)], 120, false);
        // A beat already past plays at once: at the start of the block that sent it.
        Assert.Equal([new(64, 0x90, 1, 100), new(24_100, 0x90, 60, 100)], output.OrderBy(static e => e.Frame));
    }

    [Fact]
    public void A_jump_keeps_what_waits_for_a_beat_at_its_distance_in_time() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) { e.send(); var off = new NoteOff(e); off.sendAfterBeats(1); }
            """
        );
        // 120 bpm: 64 frames = 1/375 of a quarter. Blocks 0 … 9 from quarter 0, then a cycle jump back to 0.
        var contexts = new List<TransportContext>();
        var qps = 120 / (60 * Rate);
        for (var b = 0; b < 600; b++) {
            var quarter = (b < 10 ? b : b - 10) * 64 * qps;
            contexts.Add(new(true, b * 64, quarter, 120, 4, 4, 0, Rate, 64));
        }

        var output = MidiRun.Run([effect], contexts, [TimedEvent.On(512, 60)]);
        Assert.Equal([new(512, 0x90, 60, 100), new(512 + 24_000, 0x80, 60, 100)], output);
    }

    [Fact]
    public void Scheduled_events_wait_across_blocks_and_a_reset_forgets_them() {
        using var effect = Scripts.Create("function HandleMIDI(e) { e.sendAfterMilliseconds(500); }");
        var output = new MidiEventBuffer();
        effect.Process(Scripts.Block(), [new(0, 0x90, 60, 100)], output);
        Assert.Equal(0, output.Count);
        Assert.Equal(1, effect.PendingEvents);
        effect.Reset();
        Assert.Equal(0, effect.PendingEvents);
        var after = Scripts.Run(effect, 48_000, []);
        Assert.Empty(after);
    }

    /// <summary>The first sample at or after quarter note <paramref name="quarter" /> across the contexts.</summary>
    static long FirstSampleAt(List<TransportContext> contexts, double quarter) {
        foreach (var c in contexts) {
            var offset = Math.Ceiling((quarter - c.Quarter) / c.QuartersPerSample - 1e-6);
            if (offset < c.Frames) {
                return c.Sample + (long)Math.Max(0, offset);
            }
        }

        throw new InvalidOperationException("past the contexts");
    }
}
