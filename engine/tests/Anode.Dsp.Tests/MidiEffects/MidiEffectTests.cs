using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.Tests.Support;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Tests.MidiEffects;

/// <summary>The built-in MIDI effects (docs/07-phase-6.md, workstream E): the contract, and each one event-exact.</summary>
public sealed class MidiEffectTests {
    const double Rate = MidiRun.Rate;

    public static TheoryData<string> Ids => [.. BuiltinMidiEffects.All.Select(static d => d.Id)];

    static List<OutputEvent> Ons(IEnumerable<OutputEvent> events) => [.. events.Where(static e => e.IsNoteOn)];

    static List<OutputEvent> Offs(IEnumerable<OutputEvent> events) => [.. events.Where(static e => e.IsNoteOff)];

    /// <summary>The first sample at or after quarter note <paramref name="quarter" /> at a constant tempo.</summary>
    static long SampleOf(double quarter, double bpm) => (long)Math.Ceiling(quarter * 60 * Rate / bpm - 1e-6);

    [Fact]
    public void Descriptors_are_consistent_and_creatable() {
        Assert.Equal(8, BuiltinMidiEffects.All.Count);
        Assert.Contains(BuiltinMidiEffects.All, static d => d.Id == BuiltinMidiEffects.ArpeggiatorId);
        Assert.Null(BuiltinMidiEffects.Create("nope"));
        Assert.Equal(8, BuiltinMidiEffects.All.Select(static d => d.Id).Distinct().Count());
        foreach (var descriptor in BuiltinMidiEffects.All) {
            Assert.StartsWith("anode.", descriptor.Id, StringComparison.Ordinal);
            using var effect = BuiltinMidiEffects.Create(descriptor.Id);
            Assert.NotNull(effect);
            Assert.Same(descriptor, effect.Descriptor);
            Assert.Contains(descriptor.Parameters, static p => p.Macro is not null);
            Assert.Equal(descriptor.Parameters.Count, descriptor.Parameters.Select(static p => p.Id).Distinct().Count());
            for (var i = 0; i < descriptor.Parameters.Count; i++) {
                var p = descriptor.Parameters[i];
                Assert.Equal(i, p.Index);
                Assert.InRange(p.Default, p.Min, p.Max);
                Assert.Equal(p.Default, effect.GetParameter(i));
                if (p.ValueNames is not null) {
                    Assert.Equal(p.Steps + 1, p.ValueNames.Count);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Processing_allocates_nothing(string id) {
        using var effect = MidiRun.Create(id);
        var contexts = MidiRun.Contexts(64 * 200, b => 100 + b * 0.3);
        var output = new MidiEventBuffer();
        var chord = new MidiEvent[] {
            new(0, 0x90, 60, 100), new(3, 0x90, 64, 90), new(10, 0xB0, 1, 100), new(20, 0xE0, 0, 80),
            new(30, 0x91, 67, 80), new(63, 0x90, 72, 127)
        };
        var offs = new MidiEvent[] { new(0, 0x80, 64, 0), new(5, 0x81, 67, 0), new(9, 0x90, 60, 0), new(60, 0x80, 72, 0) };
        var allocated = AllocationProbe.Measure(() => {
                for (var b = 0; b < contexts.Count; b++) {
                    output.Clear();
                    effect.SetParameter(effect.Descriptor.Parameters.Count - 1, b % 3);
                    ReadOnlySpan<MidiEvent> input = (b % 40) switch {
                        0 => chord,
                        30 => offs,
                        _ => []
                    };
                    effect.Process(contexts[b], input, output);
                }

                effect.Reset();
            }
        );
        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void State_round_trips(string id) {
        using var original = MidiRun.Create(id);
        var parameters = original.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            original.SetParameter(i, p.Min + (p.Max - p.Min) * (0.3f + 0.1f * (i % 4)));
        }

        var state = original.SaveState();
        Assert.NotNull(state);
        using var restored = MidiRun.Create(id);
        Assert.True(restored.LoadState(state));
        for (var i = 0; i < parameters.Count; i++) {
            Assert.Equal(original.GetParameter(i), restored.GetParameter(i));
        }

        Assert.Equal(state, restored.SaveState());
        Assert.False(restored.LoadState([1, 2, 3]));
        using var other = MidiRun.Create(id == Transposer.Id ? Randomizer.Id : Transposer.Id);
        Assert.False(restored.LoadState(other.SaveState()));
        Assert.Equal(state, restored.SaveState());
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Output_stays_inside_the_block_and_other_events_pass(string id) {
        using var effect = MidiRun.Create(id);
        var events = new List<TimedEvent> {
            TimedEvent.On(10, 60), TimedEvent.Cc(500, 7, 90), TimedEvent.Off(9_000, 60), TimedEvent.On(9_100, 62),
            TimedEvent.Off(20_000, 62)
        };
        var output = MidiRun.Run(effect, 48_000, events); // Run asserts every frame is inside its block
        if (id != Modifier.Id) {
            Assert.Contains(output, static e => e is { Frame: 500, Status: 0xB0, Data1: 7, Data2: 90 });
        }

        // Every note that starts ends.
        var open = new Dictionary<int, int>();
        foreach (var e in output.Where(static e => e.IsNoteOn || e.IsNoteOff)) {
            open[e.Data1] = open.GetValueOrDefault(e.Data1) + (e.IsNoteOn ? 1 : -1);
        }

        Assert.All(open.Values, static v => Assert.True(v <= 0));
    }

    // Arpeggiator.

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(137.5)]
    [InlineData(174)]
    public void Arpeggiator_steps_land_on_the_sample(double bpm) {
        using var arp = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId);
        var frames = 4 * 48_000;
        var output = MidiRun.Run(arp, frames, [TimedEvent.On(0, 60, 99), TimedEvent.Off(frames, 60)], bpm);
        var ons = Ons(output);
        var expected = Enumerable.Range(0, 1_000).Select(k => SampleOf(k * 0.25, bpm)).TakeWhile(s => s < frames);
        Assert.Equal(expected, ons.Select(static e => e.Frame));
        Assert.All(ons, static e => Assert.Equal((60, 99), (e.Data1, e.Data2)));

        // Each step lasts 80 % of a 16th.
        var gate = 0.8 * 0.25 * 60 * Rate / bpm;
        var offs = Offs(output);
        Assert.Equal(ons.Take(offs.Count).Select(e => (long)Math.Floor(e.Frame + gate)), offs.Select(static e => e.Frame));
    }

    [Fact]
    public void Arpeggiator_follows_a_tempo_ramp() {
        // 100 → 160 bpm over 3 s, one tempo a 64-frame block, as the engine computes contexts across a ramp.
        var frames = 3 * 48_000;
        var blocks = frames / 64;
        var contexts = MidiRun.Contexts(frames, b => 100 + 60.0 * b / blocks);
        using var arp = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId, (Arpeggiator.Rate, 7)); // 1/8
        var ons = Ons(MidiRun.Run([arp], contexts, [TimedEvent.On(0, 60)]));

        var expected = new List<long>();
        for (var k = 0;; k++) {
            var quarter = k * 0.5;
            var context = contexts.LastOrDefault(c => c.Quarter <= quarter + 1e-9);
            if (quarter >= contexts[^1].Quarter + contexts[^1].Frames * contexts[^1].QuartersPerSample) {
                break;
            }

            expected.Add(context.Sample + (long)Math.Ceiling((quarter - context.Quarter) / context.QuartersPerSample - 1e-6));
        }

        Assert.True(expected.Count > 10);
        Assert.Equal(expected, ons.Select(static e => e.Frame));
        // The steps get closer as the tempo rises.
        Assert.True(expected[^1] - expected[^2] < expected[1] - expected[0]);
    }

    [Theory]
    [InlineData(0, new[] { 60, 64, 67, 72, 76, 79, 60, 64 })]
    [InlineData(1, new[] { 79, 76, 72, 67, 64, 60, 79, 76 })]
    [InlineData(2, new[] { 60, 64, 67, 72, 76, 79, 76, 72, 67, 64, 60, 64 })]
    [InlineData(4, new[] { 67, 60, 64, 79, 72, 76, 67, 60 })]
    public void Arpeggiator_orders_the_notes(int mode, int[] notes) {
        using var arp = MidiRun.Create(
            BuiltinMidiEffects.ArpeggiatorId,
            (Arpeggiator.Mode, mode),
            (Arpeggiator.Octaves, 2)
        );
        var output = MidiRun.Run(
            arp,
            notes.Length * 6_000,
            [TimedEvent.On(0, 67), TimedEvent.On(0, 60), TimedEvent.On(0, 64)]
        );
        Assert.Equal(notes, Ons(output).Select(static e => (int)e.Data1));
    }

    [Fact]
    public void Arpeggiator_random_mode_is_repeatable_and_stays_in_the_chord() {
        int[] Run() {
            using var arp = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId, (Arpeggiator.Mode, 3), (Arpeggiator.Octaves, 3));
            return [.. Ons(MidiRun.Run(arp, 64 * 6_000, [TimedEvent.On(0, 60), TimedEvent.On(0, 63)])).Select(static e => (int)e.Data1)];
        }

        var a = Run();
        Assert.Equal(a, Run());
        Assert.All(a, static n => Assert.Contains(n, new[] { 60, 63, 72, 75, 84, 87 }));
        Assert.True(a.Distinct().Count() >= 5);
    }

    [Fact]
    public void Arpeggiator_waits_for_the_grid_while_playing_and_starts_at_once_stopped() {
        using var playing = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId);
        var ons = Ons(MidiRun.Run(playing, 24_000, [TimedEvent.On(1_000, 60)]));
        Assert.Equal([6_000, 12_000, 18_000], ons.Select(static e => e.Frame));

        using var stopped = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId);
        ons = Ons(MidiRun.Run(stopped, 24_000, [TimedEvent.On(1_000, 60)], playing: false));
        Assert.Equal([1_000, 7_000, 13_000, 19_000], ons.Select(static e => e.Frame));
    }

    [Fact]
    public void Arpeggiator_swing_delays_every_second_step() {
        using var arp = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId, (Arpeggiator.Swing, 50));
        var ons = Ons(MidiRun.Run(arp, 24_000, [TimedEvent.On(0, 60)]));
        // Half a 16th (3000 samples at 120 bpm) times 50 %.
        Assert.Equal([0, 7_500, 12_000, 19_500], ons.Select(static e => e.Frame));
    }

    [Fact]
    public void Arpeggiator_stops_with_the_keys_unless_latched() {
        using var plain = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId);
        var events = new List<TimedEvent> { TimedEvent.On(0, 60), TimedEvent.Off(13_000, 60) };
        Assert.Equal(3, Ons(MidiRun.Run(plain, 48_000, events)).Count);

        using var latched = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId, (Arpeggiator.Latch, 1));
        events.Add(TimedEvent.On(30_100, 72)); // a new chord after all keys went up replaces the latched one
        var ons = Ons(MidiRun.Run(latched, 48_000, events));
        Assert.Equal(8, ons.Count);
        Assert.All(ons.Take(6), static e => Assert.Equal(60, e.Data1));
        Assert.All(ons.Skip(6), static e => Assert.Equal(72, e.Data1));
    }

    [Fact]
    public void Arpeggiator_and_chord_trigger_give_a_chord_every_sixteenth() {
        using var arp = MidiRun.Create(
            BuiltinMidiEffects.ArpeggiatorId,
            (Arpeggiator.Rate, NoteDivisions.Sixteenth),
            (Arpeggiator.Octaves, 2)
        );
        using var chords = MidiRun.Create(ChordTrigger.Id);
        var contexts = MidiRun.Contexts(48_000, static _ => 110);
        var output = MidiRun.Run([arp, chords], contexts, [TimedEvent.On(0, 60)]);
        var steps = Ons(output).GroupBy(static e => e.Frame).ToList();
        Assert.Equal(Enumerable.Range(0, steps.Count).Select(k => SampleOf(k * 0.25, 110)), steps.Select(static g => g.Key));
        Assert.All(steps, static g => Assert.Equal(3, g.Count()));
        Assert.Equal([60, 64, 67], steps[0].Select(static e => (int)e.Data1));
        Assert.Equal([72, 76, 79], steps[1].Select(static e => (int)e.Data1));
    }

    // Chord Trigger.

    [Theory]
    [InlineData(0, new[] { 60, 64, 67 })]
    [InlineData(1, new[] { 60, 63, 67 })]
    [InlineData(2, new[] { 60, 64, 67, 70 })]
    [InlineData(3, new[] { 60, 64, 67, 71 })]
    [InlineData(4, new[] { 60, 63, 67, 70 })]
    [InlineData(5, new[] { 60, 62, 67 })]
    [InlineData(6, new[] { 60, 65, 67 })]
    [InlineData(7, new[] { 60, 63, 66 })]
    [InlineData(8, new[] { 60, 64, 68 })]
    [InlineData(9, new[] { 60, 67, 72 })]
    [InlineData(10, new[] { 60, 72, 84 })]
    public void Chord_trigger_plays_its_chords(int chord, int[] notes) {
        using var trigger = MidiRun.Create(ChordTrigger.Id, (ChordTrigger.Chord, chord));
        var output = MidiRun.Run(trigger, 4_096, [TimedEvent.On(100, 60, 77), TimedEvent.Off(2_000, 60)]);
        Assert.Equal(notes.Select(static n => new OutputEvent(100, 0x90, (byte)n, 77)), Ons(output));
        Assert.Equal(notes.Select(static n => new OutputEvent(2_000, 0x80, (byte)n, 0)), Offs(output));
    }

    [Fact]
    public void Chord_trigger_holds_a_shared_note_until_both_chords_let_go() {
        using var trigger = MidiRun.Create(ChordTrigger.Id);
        var output = MidiRun.Run(
            trigger,
            4_096,
            [TimedEvent.On(0, 60), TimedEvent.On(10, 64), TimedEvent.Off(100, 60), TimedEvent.Off(200, 64)]
        );
        Assert.Equal([60, 67], Offs(output).Where(static e => e.Frame == 100).Select(static e => (int)e.Data1));
        Assert.Equal([64, 68, 71], Offs(output).Where(static e => e.Frame == 200).Select(static e => (int)e.Data1));
    }

    [Fact]
    public void Chord_trigger_learns_a_chord_per_key_and_a_single_chord() {
        using var trigger = MidiRun.Create(ChordTrigger.Id, (ChordTrigger.Mode, 1), (ChordTrigger.Learn, 1));
        var learn = new List<TimedEvent> {
            TimedEvent.On(0, 48), TimedEvent.On(5, 55), TimedEvent.On(6, 63), TimedEvent.Off(50, 55),
            TimedEvent.Off(60, 48), TimedEvent.Off(70, 63)
        };
        var heard = MidiRun.Run(trigger, 128, learn);
        Assert.Equal([48, 55, 63], Ons(heard).Select(static e => (int)e.Data1)); // learning passes the notes through
        var chord = (ChordTrigger)trigger;
        Assert.Equal([48, 55, 63], chord.GetChord(48));

        trigger.SetParameter(ChordTrigger.Learn, 0);
        var played = MidiRun.Run(trigger, 4_096, [TimedEvent.On(0, 48), TimedEvent.On(10, 50)]);
        Assert.Equal([48, 55, 63, 50], Ons(played).Select(static e => (int)e.Data1)); // 50 has no chord: itself

        // Single mode learns intervals from the trigger, which the Learned chord plays on any key.
        using var single = MidiRun.Create(ChordTrigger.Id, (ChordTrigger.Learn, 1));
        MidiRun.Run(single, 128, [TimedEvent.On(0, 50), TimedEvent.On(1, 53), TimedEvent.On(2, 57), TimedEvent.On(3, 62), TimedEvent.Off(9, 50), TimedEvent.Off(9, 53), TimedEvent.Off(9, 57), TimedEvent.Off(9, 62)]);
        single.SetParameter(ChordTrigger.Learn, 0);
        single.SetParameter(ChordTrigger.Chord, ChordTrigger.Learned);
        var learned = MidiRun.Run(single, 128, [TimedEvent.On(0, 60)]);
        Assert.Equal([60, 63, 67, 72], Ons(learned).Select(static e => (int)e.Data1));

        // The learned chords are state.
        using var copy = MidiRun.Create(ChordTrigger.Id);
        Assert.True(copy.LoadState(trigger.SaveState()));
        Assert.Equal([48, 55, 63], ((ChordTrigger)copy).GetChord(48));
        Assert.True(copy.LoadState(single.SaveState()));
        Assert.Equal([0, 3, 7, 12], ((ChordTrigger)copy).GetChord(-1));
    }

    // Transposer.

    [Fact]
    public void Transposer_moves_notes_onto_a_scale() {
        using var transposer = MidiRun.Create(Transposer.Id, (Transposer.Scale, 1)); // C major
        var chromatic = Enumerable.Range(60, 13).ToArray();
        var events = chromatic.SelectMany(n => new[] { TimedEvent.On(n * 10, n), TimedEvent.Off(n * 10 + 5, n) }).ToList();
        var output = MidiRun.Run(transposer, 2_048, events);
        int[] major = [60, 60, 62, 62, 64, 65, 65, 67, 67, 69, 69, 71, 72];
        Assert.Equal(major, Ons(output).Select(static e => (int)e.Data1));
        Assert.Equal(major, Offs(output).Select(static e => (int)e.Data1));

        // D natural minor, up a whole tone and two degrees: C (60) → D (62) → F (65).
        using var minor = MidiRun.Create(
            Transposer.Id,
            (Transposer.Key, 2),
            (Transposer.Scale, 2),
            (Transposer.Semitones, 2),
            (Transposer.Degrees, 2)
        );
        var t = (Transposer)minor;
        Assert.Equal(65, t.Map(60));
        Assert.Equal(67, t.Map(62)); // E, two degrees up: G
        Assert.Equal(65, t.Map(61)); // E♭ is not in D minor: down to D (a tie), two degrees up: F
    }

    [Fact]
    public void Transposer_shifts_by_semitones_and_drops_notes_off_the_keyboard() {
        using var transposer = MidiRun.Create(Transposer.Id, (Transposer.Semitones, 12));
        var output = MidiRun.Run(transposer, 1_024, [TimedEvent.On(0, 60), TimedEvent.On(1, 120), TimedEvent.Off(9, 60), TimedEvent.Off(9, 120)]);
        Assert.Equal([72], Ons(output).Select(static e => (int)e.Data1));
        Assert.Equal([72], Offs(output).Select(static e => (int)e.Data1));
    }

    // Note Repeater.

    [Theory]
    [InlineData(NoteDivisions.Sixteenth, 120, 6_000)]
    [InlineData(9, 120, 8_000)] // 1/8T
    [InlineData(4, 90, 32_000)] // 1/4 at 90 bpm
    public void Note_repeater_repeats_at_its_rate(int rate, double bpm, int spacing) {
        using var repeater = MidiRun.Create(NoteRepeater.Id, (NoteRepeater.Rate, rate), (NoteRepeater.Repeats, 3));
        var output = MidiRun.Run(repeater, 4 * 48_000, [TimedEvent.On(100, 60, 100), TimedEvent.Off(200, 60)], bpm);
        var ons = Ons(output);
        Assert.Equal([100, 100 + spacing, 100 + 2 * spacing, 100 + 3 * spacing], ons.Select(static e => e.Frame));
        Assert.Equal([100, 80, 60, 40], ons.Select(static e => (int)e.Data2)); // −20 % a repeat
        var offs = Offs(output).Skip(1).Select(static e => e.Frame); // the original's own note-off first
        Assert.Equal([100 + 3 * spacing / 2, 100 + 5 * spacing / 2, 100 + 7 * spacing / 2], offs);
    }

    [Fact]
    public void Note_repeater_transposes_each_repeat() {
        using var repeater = MidiRun.Create(NoteRepeater.Id, (NoteRepeater.Transpose, 5), (NoteRepeater.VelocityRamp, 0));
        var ons = Ons(MidiRun.Run(repeater, 48_000, [TimedEvent.On(0, 60)]));
        Assert.Equal([60, 65, 70, 75], ons.Select(static e => (int)e.Data1));
    }

    // Velocity Processor, Randomizer, Modifier, Modulator.

    [Fact]
    public void Velocity_processor_scales_offsets_compresses_and_limits() {
        using var effect = MidiRun.Create(VelocityProcessor.Id);
        var processor = (VelocityProcessor)effect;
        Assert.Equal(100, processor.Map(100));
        effect.SetParameter(VelocityProcessor.Scale, 50);
        effect.SetParameter(VelocityProcessor.Offset, 10);
        Assert.Equal(60, processor.Map(100));
        effect.SetParameter(VelocityProcessor.Scale, 100);
        effect.SetParameter(VelocityProcessor.Offset, 0);
        effect.SetParameter(VelocityProcessor.Threshold, 64);
        effect.SetParameter(VelocityProcessor.Ratio, 2);
        Assert.Equal(96, processor.Map(127));
        Assert.Equal(40, processor.Map(40));
        effect.SetParameter(VelocityProcessor.Min, 50);
        effect.SetParameter(VelocityProcessor.Max, 90);
        Assert.Equal(50, processor.Map(1));
        Assert.Equal(90, processor.Map(127));
        var output = MidiRun.Run(effect, 1_024, [TimedEvent.On(3, 60, 10), TimedEvent.Off(9, 60)]);
        Assert.Equal([new OutputEvent(3, 0x90, 60, 50), new OutputEvent(9, 0x80, 60, 0)], output);
    }

    [Fact]
    public void Randomizer_is_repeatable_and_stays_in_range() {
        List<OutputEvent> Run(params (int, float)[] parameters) {
            using var randomizer = MidiRun.Create(Randomizer.Id, parameters);
            var events = Enumerable.Range(0, 200)
                .SelectMany(n => new[] { TimedEvent.On(n * 100, 60, 64), TimedEvent.Off(n * 100 + 50, 60) })
                .ToList();
            return MidiRun.Run(randomizer, 20_480, events);
        }

        var a = Run((Randomizer.Velocity, 10), (Randomizer.Pitch, 2));
        Assert.Equal(a, Run((Randomizer.Velocity, 10), (Randomizer.Pitch, 2)));
        Assert.NotEqual(a, Run((Randomizer.Velocity, 10), (Randomizer.Pitch, 2), (Randomizer.Seed, 7)));
        var ons = Ons(a);
        Assert.Equal(200, ons.Count);
        Assert.All(ons, static e => Assert.InRange(e.Data2, 54, 74));
        Assert.All(ons, static e => Assert.InRange(e.Data1, 58, 62));
        Assert.Equal(5, ons.Select(static e => e.Data1).Distinct().Count());
        Assert.Equal(ons.Select(static e => e.Data1), Offs(a).Select(static e => e.Data1)); // note-offs follow

        var half = Ons(Run((Randomizer.Chance, 50)));
        Assert.InRange(half.Count, 70, 130);
        Assert.Empty(Ons(Run((Randomizer.Chance, 0))));
    }

    [Fact]
    public void Modifier_remaps_a_controller() {
        using var modifier = MidiRun.Create(Modifier.Id, (Modifier.Scale, 50), (Modifier.Add, 10));
        var output = MidiRun.Run(modifier, 1_024, [TimedEvent.Cc(5, 1, 100), TimedEvent.Cc(6, 7, 100), TimedEvent.On(7, 60)]);
        Assert.Equal(
            [new OutputEvent(5, 0xB0, 11, 60), new OutputEvent(6, 0xB0, 7, 100), new OutputEvent(7, 0x90, 60, 100)],
            output
        );

        modifier.SetParameter(Modifier.Output, Modifier.PitchBend);
        modifier.SetParameter(Modifier.Scale, 100);
        modifier.SetParameter(Modifier.Add, 0);
        modifier.SetParameter(Modifier.Thru, 1);
        output = MidiRun.Run(modifier, 64, [TimedEvent.Cc(0, 1, 127)]);
        Assert.Equal([new OutputEvent(0, 0xB0, 1, 127), new OutputEvent(0, 0xE0, 0x7F, 0x7F)], output);
    }

    [Fact]
    public void Modulator_sends_its_lfo_as_a_controller_on_a_grid() {
        using var modulator = MidiRun.Create(Modulator.Id, (Modulator.Rate, 2), (Modulator.Depth, 100));
        var output = MidiRun.Run(modulator, 48_000, [], block: 100);
        Assert.All(output, static e => Assert.Equal((0L, 0xB0, 1), (e.Frame % Modulator.Interval, (int)e.Status, (int)e.Data1)));
        Assert.InRange(output.Count, 200, 1_500);
        Assert.Equal(64, output[0].Data2);
        // A 2 Hz sine: the top a quarter cycle in, at 6000 samples.
        var top = output.MaxBy(static e => e.Data2);
        Assert.Equal(127, top.Data2);
        Assert.InRange(top.Frame, 5_000, 7_000);

        // Synced while playing, the phase is the project position's: a 1/4 LFO peaks a 16th after the beat.
        using var synced = MidiRun.Create(Modulator.Id, (Modulator.Sync, 5), (Modulator.Depth, 100));
        var contexts = MidiRun.Contexts(48_000, static _ => 120, startQuarter: 3);
        output = MidiRun.Run([synced], contexts, []);
        Assert.Equal(64, output[0].Data2);
        Assert.InRange(output.First(static e => e.Data2 == 127).Frame, 5_200, 6_000);
    }

    [Fact]
    public void Transport_context_offsets_match_the_run() {
        var contexts = MidiRun.Contexts(640, static _ => 120);
        Assert.Equal(10, contexts.Count);
        Assert.Equal(64 * 120 / (60 * Rate), contexts[1].Quarter, 12);
        Assert.Equal(0, TransportContext.Stopped(Rate, 64).Quarter);
    }
}
