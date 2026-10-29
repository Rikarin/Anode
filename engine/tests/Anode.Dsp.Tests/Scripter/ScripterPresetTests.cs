using Anode.Dsp.MidiEffects.Scripter;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Scripter;

/// <summary>The factory scripts: each runs cleanly, and does what its name says.</summary>
public sealed class ScripterPresetTests {
    public static TheoryData<string> Ids => [..ScripterPresets.All.Select(static p => p.Id)];

    static readonly List<TimedEvent> Phrase = [
        TimedEvent.On(100, 60, 90), TimedEvent.On(100, 64, 80), TimedEvent.On(100, 67, 70), TimedEvent.Cc(2_000, 1, 10),
        TimedEvent.Cc(30_000, 1, 110), TimedEvent.Off(40_000, 64), TimedEvent.Off(40_000, 60),
        TimedEvent.Off(40_000, 67),
        TimedEvent.On(50_000, 48), TimedEvent.Bend(51_000, 2000), TimedEvent.Off(70_000, 48)
    ];

    [Fact]
    public void The_list_has_ten_with_unique_ids() {
        Assert.Equal(10, ScripterPresets.All.Count);
        Assert.Equal(10, ScripterPresets.All.Select(static p => p.Id).Distinct().Count());
        Assert.All(ScripterPresets.All, static p => Assert.StartsWith("factory.", p.Id, StringComparison.Ordinal));
        Assert.Same(ScripterPresets.Chord, ScripterPresets.Find("factory.chord"));
        Assert.Null(ScripterPresets.Find("nope"));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Each_runs_cleanly_and_ends_every_note_it_starts(string id) {
        using var effect = Scripts.Create(ScripterPresets.Find(id)!.Source);
        var output = Scripts.Run(effect, 4 * 48_000, Phrase);
        Assert.True(effect.Running, effect.Problem?.ToString());
        Assert.Equal(0, effect.DroppedEvents);
        var open = new Dictionary<int, int>();
        foreach (var e in output.Where(static e => e.IsNoteOn || e.IsNoteOff)) {
            var key = ((e.Status & 0x0F) << 7) | e.Data1;
            open[key] = open.GetValueOrDefault(key) + (e.IsNoteOn ? 1 : -1);
        }

        Assert.All(open, static kv => Assert.True(kv.Value <= 0, $"note {kv.Key & 0x7F} hangs"));
        Assert.True(output.Count > 0);
    }

    [Fact]
    public void Transposer_moves_notes_and_ends_them_where_they_started() {
        using var effect = Scripts.Create(ScripterPresets.Transposer.Source);
        effect.SetParameter(0, 5);
        var output = new List<OutputEvent>(Scripts.Run(effect, 640, [TimedEvent.On(10, 60)]));
        effect.SetParameter(0, -3);
        output.AddRange(Scripts.Run(effect, 640, [TimedEvent.Off(10, 60), TimedEvent.On(20, 60)]));
        Assert.Equal([new(10, 0x90, 65, 100), new(10, 0x80, 65, 0), new(20, 0x90, 57, 100)], output);
    }

    [Fact]
    public void Delay_notes_delays_by_its_milliseconds() {
        using var effect = Scripts.Create(ScripterPresets.DelayNotes.Source);
        Assert.Equal([new(4_810, 0x90, 60, 100)], Scripts.Run(effect, 9_600, [TimedEvent.On(10, 60)]));
    }

    [Fact]
    public void Chord_plays_the_shape_and_ends_it() {
        using var effect = Scripts.Create(ScripterPresets.Chord.Source);
        effect.SetParameter(0, 1); // Minor
        var output = Scripts.Run(effect, 640, [TimedEvent.On(10, 60), TimedEvent.Off(300, 60)]);
        Assert.Equal(
            [
                new(10, 0x90, 60, 100), new(10, 0x90, 63, 100), new(10, 0x90, 67, 100), new(300, 0x80, 60, 0),
                new(300, 0x80, 63, 0), new(300, 0x80, 67, 0)
            ],
            output
        );
    }

    [Fact]
    public void Guitar_strummer_spreads_a_chord() {
        using var effect = Scripts.Create(ScripterPresets.GuitarStrummer.Source);
        var output = Scripts.Run(effect, 9_600, [TimedEvent.On(10, 64), TimedEvent.On(10, 60), TimedEvent.On(10, 67)]);
        // Strum 25 ms = 1200 samples apart, low first, from the block's events.
        Assert.Equal([new(10, 0x90, 60, 100), new(1_210, 0x90, 64, 100), new(2_410, 0x90, 67, 100)], output);
    }

    [Fact]
    public void Arpeggio_plays_held_notes_on_the_grid() {
        using var effect = Scripts.Create(ScripterPresets.Arpeggio.Source);
        var output = Scripts.Run(effect, 24_000, [TimedEvent.On(0, 60), TimedEvent.On(0, 64), TimedEvent.On(0, 67)]);
        var ons = output.Where(static e => e.IsNoteOn).ToList();
        // 1/16 at 120 bpm: a step every 6000 samples, from the bar's start.
        Assert.Equal([0L, 6_000, 12_000, 18_000], ons.Select(static e => e.Frame));
        Assert.Equal([60, 64, 67, 60], ons.Select(static e => (int)e.Data1));
        Assert.Equal(4_800, output.First(static e => e.IsNoteOff).Frame); // gate 80 %
    }

    [Fact]
    public void Cc_smoother_ramps_the_controller() {
        using var effect = Scripts.Create(ScripterPresets.CcSmoother.Source);
        var output = Scripts.Run(
            effect,
            9_600,
            [TimedEvent.Cc(0, 1, 0), TimedEvent.Cc(64, 1, 120), TimedEvent.Cc(70, 7, 3)]
        );
        var mod = output.Where(static e => e.Data1 == 1).ToList();
        Assert.Equal(13, mod.Count); // the first value, then 12 steps of 5 ms over 60 ms
        Assert.Equal([0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120], mod.Select(static e => (int)e.Data2));
        Assert.Equal(64 + 11 * 240, mod[^1].Frame);
        Assert.Contains(output, static e => e is { Data1: 7, Data2: 3, Frame: 70 });
    }

    [Fact]
    public void Trace_demo_traces() {
        using var effect = Scripts.Create(ScripterPresets.TraceDemo.Source);
        var compiled = Scripts.Trace(effect);
        Assert.Equal(["Trace Demo ready", "Show Events = 1", "Show Timing = 1"], compiled);
        Scripts.Run(effect, 48_000 * 2 + 64, [TimedEvent.On(10, 60)]);
        var lines = Scripts.Trace(effect);
        Assert.Contains("[NoteOn channel:1 pitch:60 [C3] velocity:100]", lines);
        Assert.Equal(3, lines.Count(static l => l.StartsWith("beat ", StringComparison.Ordinal)));
    }
}
