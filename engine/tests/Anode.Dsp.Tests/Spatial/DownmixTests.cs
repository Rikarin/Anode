using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Spatial;

/// <summary>The down-mixer: ITU-R BS.775 and Anode's documented extensions (docs/spatial/README.md).</summary>
public sealed class DownmixTests {
    static readonly double Minus3 = Math.Sqrt(0.5);

    static void AssertMatrix(SpeakerFormat from, SpeakerFormat to, double[,] expected, float lfeGain = 0) {
        var mixer = new Downmixer(SpeakerLayout.Of(from), SpeakerLayout.Of(to), lfeGain);
        for (var o = 0; o < expected.GetLength(0); o++) {
            for (var i = 0; i < expected.GetLength(1); i++) {
                var actual = mixer.Gain(o, i);
                var want = expected[o, i];
                if (want == 0) {
                    Assert.True(actual == 0, $"{from}→{to} [{o},{i}]: {actual}, expected 0");
                } else {
                    // Within 0.1 dB, as the exit criterion asks.
                    Assert.True(
                        Math.Abs(Support.Analysis.ToDb(actual) - Support.Analysis.ToDb(want)) < 0.1,
                        $"{from}→{to} [{o},{i}]: {actual}, expected {want}"
                    );
                }
            }
        }
    }

    [Fact]
    public void Five_one_to_stereo_is_bs775() {
        // L R C LFE Ls Rs → L R: L' = L + 0.7071 C + 0.7071 Ls, the LFE dropped.
        AssertMatrix(
            SpeakerFormat.Surround51,
            SpeakerFormat.Stereo,
            new[,] { { 1, 0, Minus3, 0, Minus3, 0 }, { 0, 1, Minus3, 0, 0, Minus3 } }
        );
        AssertMatrix(
            SpeakerFormat.Surround50,
            SpeakerFormat.Stereo,
            new[,] { { 1, 0, Minus3, Minus3, 0 }, { 0, 1, Minus3, 0, Minus3 } }
        );
    }

    [Fact]
    public void Five_one_to_mono_and_lcr_are_bs775() {
        AssertMatrix(SpeakerFormat.Surround51, SpeakerFormat.Mono, new[,] { { Minus3, Minus3, 1, 0, 0.5, 0.5 } });
        AssertMatrix(SpeakerFormat.Stereo, SpeakerFormat.Mono, new[,] { { Minus3, Minus3 } });
        AssertMatrix(
            SpeakerFormat.Surround51,
            SpeakerFormat.Lcr,
            new[,] { { 1, 0, 0, 0, Minus3, 0 }, { 0, 1, 0, 0, 0, Minus3 }, { 0, 0, 1, 0, 0, 0 } }
        );
    }

    [Fact]
    public void Seven_one_folds_sides_and_rears_as_surrounds() {
        // L R C LFE Lss Rss Lrs Rrs → 5.1: each surround pair at −3 dB into Ls/Rs.
        AssertMatrix(
            SpeakerFormat.Surround71,
            SpeakerFormat.Surround51,
            new[,] {
                { 1, 0, 0, 0, 0, 0, 0, 0 },
                { 0, 1, 0, 0, 0, 0, 0, 0 },
                { 0, 0, 1, 0, 0, 0, 0, 0 },
                { 0, 0, 0, 1, 0, 0, 0, 0 },
                { 0, 0, 0, 0, Minus3, 0, Minus3, 0 },
                { 0, 0, 0, 0, 0, Minus3, 0, Minus3 }
            }
        );
        // → stereo: then BS.775's −3 dB, so −6 dB each.
        AssertMatrix(
            SpeakerFormat.Surround71,
            SpeakerFormat.Stereo,
            new[,] { { 1, 0, Minus3, 0, 0.5, 0, 0.5, 0 }, { 0, 1, Minus3, 0, 0, 0.5, 0, 0.5 } }
        );
    }

    [Fact]
    public void Seven_one_four_folds_the_heights_at_minus_3_db() {
        // L R C LFE Lss Rss Lrs Rrs Ltf Rtf Ltr Rtr → stereo (the golden path's monitoring down-mix).
        AssertMatrix(
            SpeakerFormat.Surround714,
            SpeakerFormat.Stereo,
            new[,] {
                { 1, 0, Minus3, 0, 0.5, 0, 0.5, 0, Minus3, 0, 0.5, 0 },
                { 0, 1, Minus3, 0, 0, 0.5, 0, 0.5, 0, Minus3, 0, 0.5 }
            }
        );
        // → 7.1: top fronts into L/R, top rears into the rears, at −3 dB.
        var to71 = new double[8, 12];
        for (var c = 0; c < 8; c++) {
            to71[c, c] = 1;
        }

        to71[0, 8] = to71[1, 9] = to71[6, 10] = to71[7, 11] = Minus3;
        AssertMatrix(SpeakerFormat.Surround714, SpeakerFormat.Surround71, to71);
        // → 7.1.2: top fronts and rears share the top middles.
        var mixer = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround714), SpeakerLayout.Of(SpeakerFormat.Surround712));
        Assert.Equal((float)Minus3, mixer.Gain(8, 8), 5);
        Assert.Equal((float)Minus3, mixer.Gain(8, 10), 5);
        Assert.Equal(0, mixer.Gain(8, 9));
    }

    [Fact]
    public void Nine_one_six_to_seven_one_four_splits_the_top_middles() {
        var mixer = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround916), SpeakerLayout.Of(SpeakerFormat.Surround714));
        // 9.1.6: … Lw Rw (8, 9) Ltf Rtf (10, 11) Ltm Rtm (12, 13) Ltr Rtr (14, 15).
        Assert.Equal((float)Minus3, mixer.Gain(8, 12), 5);
        Assert.Equal((float)Minus3, mixer.Gain(10, 12), 5);
        Assert.Equal(1, mixer.Gain(8, 10));
        Assert.Equal(1, mixer.Gain(0, 8));
    }

    [Fact]
    public void The_lfe_is_off_unless_asked_for() {
        var to2 = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround51), SpeakerLayout.Of(SpeakerFormat.Stereo));
        Assert.Equal(0, to2.Gain(0, 3));
        AssertMatrix(
            SpeakerFormat.Surround51,
            SpeakerFormat.Stereo,
            new[,] { { 1, 0, Minus3, Minus3, Minus3, 0 }, { 0, 1, Minus3, Minus3, 0, Minus3 } },
            1
        );
        var keep = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround714), SpeakerLayout.Of(SpeakerFormat.Surround51));
        Assert.Equal(1, keep.Gain(3, 3));
    }

    [Fact]
    public void Process_applies_the_matrix_and_allocates_nothing() {
        var mixer = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround714), SpeakerLayout.Of(SpeakerFormat.Stereo));
        var input = new float[12 * 64];
        for (var c = 0; c < 12; c++) {
            input.AsSpan(c * 64, 64).Fill(c + 1);
        }

        var output = new float[2 * 64];
        output.AsSpan().Fill(99);
        mixer.Process(new(input, 12, 64), new(output, 2, 64));
        var expected = Enumerable.Range(0, 12).Sum(c => mixer.Gain(0, c) * (c + 1));
        Assert.Equal(expected, output[0], 4);
        Assert.Equal(expected, output[63], 4);
        Assert.Equal(0, AllocationProbe.Measure(() => mixer.Process(new(input, 12, 64), new(output, 2, 64))));
    }
}
