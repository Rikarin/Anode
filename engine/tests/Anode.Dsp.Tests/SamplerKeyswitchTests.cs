using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     SFZ keyswitches in the Sampler (docs/08-phase-7.md, "Articulations"): two groups, a sine and a square, told apart
///     by their third harmonic.
/// </summary>
public sealed class SamplerKeyswitchTests : IDisposable {
    const int Rate = (int)Play.Rate;
    const int Sine = 24;
    const int Square = 26;
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    static readonly double Fundamental = NoteFrequency(60);

    string Program(string global = "sw_lokey=c1 sw_hikey=d1 sw_default=c1") {
        var sine = new float[Rate * 2];
        var square = new float[Rate * 2];
        for (var i = 0; i < sine.Length; i++) {
            var phase = Fundamental * i / Rate;
            sine[i] = 0.5f * (float)Math.Sin(2 * Math.PI * phase);
            square[i] = phase - Math.Floor(phase) < 0.5 ? 0.4f : -0.4f;
        }

        TestWav.Write(Path.Combine(folder, "sine.wav"), Rate, [sine], rootNote: 60);
        TestWav.Write(Path.Combine(folder, "square.wav"), Rate, [square], rootNote: 60);
        var path = Path.Combine(folder, "switch.sfz");
        File.WriteAllText(
            path,
            $"""
             <global> {global}
             <group> sw_last=c1 sw_label=Sine
             <region> sample=sine.wav lokey=36 hikey=96 pitch_keycenter=60
             <group> sw_last=d1 sw_label=Square Wave
             <region> sample=square.wav lokey=36 hikey=96 pitch_keycenter=60
             """
        );
        return path;
    }

    AnodeSampler Load(string? global = null) {
        var sampler = (AnodeSampler)Play.Create(AnodeSampler.Id);
        Assert.True(sampler.LoadFile(global is null ? Program() : Program(global)));
        return sampler;
    }

    /// <summary>The third harmonic's level over the fundamental's, over 0.25 s from 0.1 s into the render.</summary>
    static double ThirdHarmonic(float[] audio) {
        var from = Rate / 10;
        var length = Rate / 4;
        return Goertzel(audio, from, length, 3 * Fundamental) / Goertzel(audio, from, length, Fundamental);
    }

    static double Goertzel(float[] audio, int from, int length, double frequency) {
        double re = 0, im = 0;
        for (var i = 0; i < length; i++) {
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / length);
            var a = 2 * Math.PI * frequency * (from + i) / Rate;
            re += w * audio[from + i] * Math.Cos(a);
            im += w * audio[from + i] * Math.Sin(a);
        }

        return Math.Sqrt(re * re + im * im) * 4 / length;
    }

    static float[] Note(IInstrument sampler, params TimedEvent[] before) {
        var events = new List<TimedEvent>(before) { TimedEvent.On(100, 60), TimedEvent.Off(Rate / 2, 60) };
        var (left, _) = Play.Render(sampler, Rate * 3 / 4, events);
        sampler.Reset();
        return left;
    }

    [Fact]
    public void The_parser_reads_the_keyswitch_opcodes() {
        var regions = SfzParser.Parse(
            """
            <control> sw_default=d1
            <global> sw_lokey=24 sw_hikey=c#1
            <group> sw_last=24 sw_label=Long Bow
            <region> sample=a.wav
            <group> sw_last=25 sw_default=c1
            <region> sample=b.wav
            """,
            folder
        );
        Assert.Equal(2, regions.Count);
        Assert.Equal(
            (24, 25, 24, 26, "Long Bow"),
            (regions[0].SwLoKey, regions[0].SwHiKey, regions[0].SwLast, regions[0].SwDefault, regions[0].SwLabel)
        );
        Assert.Equal((25, 24, (string?)null), (regions[1].SwLast, regions[1].SwDefault, regions[1].SwLabel));
        Assert.Equal(-1, new SfzRegion().SwLast);
    }

    [Fact]
    public void The_default_keyswitch_plays_its_group() {
        using var sampler = Load();
        Assert.Equal(Sine, sampler.ActiveKeyswitch);
        Assert.Equal([new(Sine, "Sine"), new SamplerKeyswitch(Square, "Square Wave")], sampler.Keyswitches);
        var ratio = ThirdHarmonic(Note(sampler));
        Assert.True(ratio < 0.01, $"third harmonic {ratio:0.0000} of the fundamental");
    }

    [Fact]
    public void A_keyswitch_selects_its_group_and_does_not_sound() {
        using var sampler = Load();

        var (silent, _) = Play.Render(sampler, Rate / 4, [TimedEvent.On(0, Square), TimedEvent.Off(Rate / 8, Square)]);
        Assert.Equal(0, Peak(silent));
        Assert.Equal(Square, sampler.ActiveKeyswitch);
        var square = ThirdHarmonic(Note(sampler));
        Assert.True(square is > 0.25 and < 0.4, $"third harmonic {square:0.0000}"); // a square's is 1/3

        // Within one buffer: the switch before the note applies to it.
        var back = ThirdHarmonic(Note(sampler, TimedEvent.On(10, Sine), TimedEvent.Off(20, Sine)));
        Assert.True(back < 0.01, $"third harmonic {back:0.0000}");
        Assert.Equal(Sine, sampler.ActiveKeyswitch);
    }

    [Fact]
    public void A_key_in_the_range_without_a_group_still_switches_and_silences() {
        using var sampler = Load();
        Note(sampler, TimedEvent.On(0, 25)); // c#1: in sw_lokey…sw_hikey, no group plays under it
        Assert.Equal(25, sampler.ActiveKeyswitch);
        Assert.Equal(0, Peak(Note(sampler)));
    }

    [Fact]
    public void Without_a_default_no_switched_group_plays_until_a_keyswitch() {
        using var sampler = Load("sw_lokey=c1 sw_hikey=d1");
        Assert.Equal(-1, sampler.ActiveKeyswitch);
        Assert.Equal(0, Peak(Note(sampler)));
        Assert.True(ThirdHarmonic(Note(sampler, TimedEvent.On(0, Square))) > 0.25);
    }

    [Fact]
    public void The_state_keeps_the_active_keyswitch() {
        using var sampler = Load();
        Note(sampler, TimedEvent.On(0, Square));
        var state = sampler.SaveState()!;

        using var restored = (AnodeSampler)Play.Create(AnodeSampler.Id);
        Assert.True(restored.LoadState(state));
        Assert.Equal(Square, restored.ActiveKeyswitch);
        Assert.True(ThirdHarmonic(Note(restored)) > 0.25);

        // A loaded program's state applied again (same file) takes its keyswitch too.
        Note(restored, TimedEvent.On(0, Sine));
        Assert.True(restored.LoadState(state));
        Assert.Equal(Square, restored.ActiveKeyswitch);
    }

    [Fact]
    public void A_program_without_keyswitches_writes_the_plain_state() {
        var wav = Path.Combine(folder, "plain.wav");
        TestWav.Write(wav, Rate, [TestWav.Sine(440, Rate, 0.1)]);
        using var sampler = (AnodeSampler)Play.Create(AnodeSampler.Id);
        Assert.True(sampler.LoadFile(wav));
        Assert.Empty(sampler.Keyswitches);
        Assert.Equal(-1, sampler.ActiveKeyswitch);
        var state = sampler.SaveState()!;
        Assert.True(state.AsSpan().IndexOf("SWKS"u8) < 0);
    }

    [Fact]
    public void Keyswitching_does_not_allocate() {
        using var sampler = Load();
        var left = new float[64];
        var right = new float[64];
        MidiEvent[] toSquare = [new(0, 0x90, Square, 100), new(1, 0x80, Square, 0), new(2, 0x90, 60, 100)];
        MidiEvent[] toSine = [new(0, 0x80, 60, 0), new(5, 0x90, Sine, 100), new(9, 0x90, 64, 90)];
        MidiEvent[] off = [new(0, 0x80, 64, 0), new(1, 0x80, Sine, 0)];
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 300; block++) {
                    var e = (block % 60) switch {
                        0 => toSquare,
                        20 => toSine,
                        40 => off,
                        _ => []
                    };
                    sampler.Process(e, left, right);
                }

                sampler.Reset();
            }
        );
        Assert.Equal(0, allocated);
    }
}
