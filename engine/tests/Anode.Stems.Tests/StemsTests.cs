using Anode.Contracts;

namespace Anode.Stems.Tests;

/// <summary>The model choice and file names (docs/09-phase-8.md, "Contract notes").</summary>
public sealed class StemsTests {
    [Fact]
    public void Guitar_or_piano_takes_the_six_stem_model() {
        Assert.Equal(StemModel.FourStems, StemSeparators.ModelFor([StemKind.Vocals, StemKind.Drums]));
        Assert.Equal(StemModel.FourStems, StemSeparators.ModelFor([]));
        Assert.Equal(StemModel.SixStems, StemSeparators.ModelFor([StemKind.Bass, StemKind.Piano]));
        Assert.Equal(StemModel.SixStems, StemSeparators.ModelFor([StemKind.Guitar]));
    }

    [Fact]
    public void Each_model_has_its_file_and_stems() {
        Assert.Equal("htdemucs.onnx", StemSeparators.FileName(StemModel.FourStems));
        Assert.Equal("htdemucs_6s.onnx", StemSeparators.FileName(StemModel.SixStems));
        Assert.Equal(
            [StemKind.Drums, StemKind.Bass, StemKind.Other, StemKind.Vocals],
            StemSeparators.StemsOf(StemModel.FourStems)
        );
        Assert.Equal(6, StemSeparators.StemsOf(StemModel.SixStems).Count);
    }
}
