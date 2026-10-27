using Anode.Dsp.Instruments.Sampler;

namespace Anode.Dsp.Tests;

public class SfzParserTests {
    static readonly string Folder = Path.Combine(Path.GetTempPath(), "sfz");

    [Theory]
    [InlineData("c4", 60)]
    [InlineData("C4", 60)]
    [InlineData("c#4", 61)]
    [InlineData("db4", 61)]
    [InlineData("a4", 69)]
    [InlineData("b3", 59)]
    [InlineData("bb3", 58)]
    [InlineData("c-1", 0)]
    [InlineData("g9", 127)]
    [InlineData("64", 64)]
    [InlineData("e#2", 41)]
    public void Note_names_and_numbers(string text, int note) => Assert.Equal(note, SfzParser.ParseNote(text));

    [Theory]
    [InlineData("h4")]
    [InlineData("c")]
    [InlineData("128")]
    [InlineData("-1")]
    [InlineData("g#9")]
    public void Bad_notes_are_rejected(string text) => Assert.Null(SfzParser.ParseNote(text));

    [Fact]
    public void Headers_inherit_and_reset_in_order() {
        var regions = SfzParser.Parse(
            """
            // A comment, then a block comment
            /* <region> sample=ignored.wav */
            #define $X 1
            <control> default_path=samples\piano/
            <global> volume=-3 ampeg_release=0.5
            <group> lovel=1 hivel=64 amp_veltrack=50
            <region> sample=soft c4.wav key=c4
            <region> sample=soft d4.wav lokey=d4 hikey=e4 pitch_keycenter=d4 volume=-6 // own volume
            <group> lovel=65
            <region> sample=loud c4.wav key=60 tune=-12 transpose=-1 pan=-30 offset=100
            <global>
            <region> sample=bare.wav
            """,
            Folder
        );

        Assert.Equal(4, regions.Count);
        var (a, b, c, d) = (regions[0], regions[1], regions[2], regions[3]);
        Assert.Equal(Path.GetFullPath(Path.Combine(Folder, "samples/piano/soft c4.wav")), a.Sample);
        Assert.Equal(
            (60, 60, 60, 1, 64, -3.0, 0.5, 50.0),
            (a.LoKey, a.HiKey, a.KeyCenter, a.LoVel, a.HiVel, a.Volume, a.Release, a.AmpVeltrack)
        );
        Assert.Equal((62, 64, 62, -6.0), (b.LoKey, b.HiKey, b.KeyCenter, b.Volume));
        Assert.Equal(
            (65, 127, 100.0),
            (c.LoVel, c.HiVel, c.AmpVeltrack)
        ); // the new group dropped the old one's opcodes
        Assert.Equal((-12.0, -1, -30.0, 100L, -3.0), (c.Tune, c.Transpose, c.Pan, c.Offset, c.Volume));
        Assert.Equal(
            (0, 127, 1, 0.0, 0.001),
            (d.LoKey, d.HiKey, d.LoVel, d.Volume, d.Release)
        ); // a new global resets all
        Assert.EndsWith("bare.wav", d.Sample, StringComparison.Ordinal);
    }

    [Fact]
    public void Loops_envelopes_and_round_robin_opcodes() {
        var region = Assert.Single(
            SfzParser.Parse(
                "<region> sample=x.wav loop_mode=loop_sustain loop_start=10 loop_end=99 ampeg_attack=0.1 ampeg_hold=0.2 "
                + "ampeg_decay=0.3 ampeg_sustain=40 ampeg_release=0.4 seq_length=4 seq_position=2",
                Folder
            )
        );
        Assert.Equal(LoopMode.Sustain, region.LoopMode);
        Assert.Equal((10L, 99L), (region.LoopStart, region.LoopEnd));
        Assert.Equal(
            (0.1, 0.2, 0.3, 40.0, 0.4),
            (region.Attack, region.Hold, region.Decay, region.Sustain, region.Release)
        );
        Assert.Equal((4, 2), (region.SeqLength, region.SeqPosition));

        string[] modes = ["no_loop", "one_shot", "loop_continuous", "loop_sustain"];
        LoopMode[] expected = [LoopMode.NoLoop, LoopMode.OneShot, LoopMode.Continuous, LoopMode.Sustain];
        for (var i = 0; i < modes.Length; i++) {
            Assert.Equal(
                expected[i],
                Assert.Single(SfzParser.Parse($"<region> sample=x.wav loop_mode={modes[i]}", Folder)).LoopMode
            );
        }

        Assert.Null(Assert.Single(SfzParser.Parse("<region> sample=x.wav", Folder)).LoopMode);
    }

    [Fact]
    public void Several_headers_on_a_line_unknown_opcodes_and_bad_values_are_tolerated() {
        var regions = SfzParser.Parse(
            "<group>hivel=100 <region>sample=a.wav lokey=c4 fil_type=lpf_2p<region>sample=b.wav lokey=nonsense volume=abc\r\n<curve> v000=0",
            Folder
        );
        Assert.Equal(2, regions.Count);
        Assert.Equal((60, 100), (regions[0].LoKey, regions[0].HiVel));
        Assert.Equal((0, 0.0, 100), (regions[1].LoKey, regions[1].Volume, regions[1].HiVel));
    }

    [Fact]
    public void Absolute_sample_paths_are_kept() {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "abs.wav"));
        Assert.Equal(absolute, Assert.Single(SfzParser.Parse($"<region> sample={absolute}", Folder)).Sample);
    }
}
