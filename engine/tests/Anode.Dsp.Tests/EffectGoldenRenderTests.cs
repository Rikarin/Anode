using Anode.Dsp.Effects;
using Anode.Dsp.IO;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     Golden renders of the effects, as <see cref="GoldenRenderTests" /> for the instruments: each effect, at its
///     <see cref="EffectPresets" /> setting, processes half a second of <see cref="Fx.Music" />, which must null against
///     <c>tests/Anode.Dsp.Tests/Golden/&lt;id&gt;.wav</c> to −90 dB. Regenerate an intended change with
///     <c>ANODE_UPDATE_GOLDEN=1 dotnet test</c> and listen to it.
/// </summary>
public sealed class EffectGoldenRenderTests {
    const double ResidualDb = -90;
    const int Frames = 24_000;

    public static TheoryData<string> Ids => [..BuiltinEffects.All.Select(static d => d.Id)];

    static (float[] Left, float[] Right) Render(string id, int block = Play.Block) {
        using var effect = Fx.Create(id, EffectPresets.For(id));
        var (left, right) = Fx.Music(Frames);
        return Fx.Run(effect, left, right, block, sidechainLeft: right);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Render_matches_the_committed_one(string id) {
        var (left, right) = Render(id);
        Assert.True(Rms(left) > 0.01 && Rms(right) > 0.01);
        Assert.True(Peak(left) < 1 && Peak(right) < 1, "a golden render must fit a 24-bit file");
        var path = Path.Combine(Play.EngineRoot(), "tests", "Anode.Dsp.Tests", "Golden", id + ".wav");
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_GOLDEN") == "1"
            || (!File.Exists(path) && Environment.GetEnvironmentVariable("CI") is null)) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TestWav.Write(path, (int)Fx.Rate, [left, right]);
            return;
        }

        Assert.True(File.Exists(path), $"No golden render for {id}: run ANODE_UPDATE_GOLDEN=1 dotnet test.");
        var golden = WavReader.Read(path);
        var l = NullResidualDb(golden.Channels[0], left);
        var r = NullResidualDb(golden.Channels[1], right);
        Assert.True(
            l < ResidualDb && r < ResidualDb,
            $"{id} no longer matches its golden render (left {l:0.0} dB, right {r:0.0} dB). If the change is intended, "
            + "regenerate with ANODE_UPDATE_GOLDEN=1."
        );
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Rendering_is_deterministic(string id) {
        var first = Render(id);
        var second = Render(id, 333);
        Assert.Equal(-300, NullResidualDb(first.Left, second.Left));
        Assert.Equal(-300, NullResidualDb(first.Right, second.Right));
    }
}
