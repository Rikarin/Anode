using Anode.Dsp.IO;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     Golden renders: each instrument plays a short phrase that must match its committed reference in
///     <c>tests/Anode.Dsp.Tests/Golden</c> (24-bit WAVs). A change that alters the sound fails here; an intended one is
///     accepted by regenerating with <c>ANODE_UPDATE_GOLDEN=1 dotnet test</c> and listening to the diff. The tolerance
///     absorbs last-bit differences between platforms' math libraries and the 24-bit storage, not audible changes.
/// </summary>
public sealed class GoldenRenderTests : IDisposable {
    const double ResidualDb = -90;
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    public static TheoryData<string> Ids => [AnodeSynth.Id, AnodeSampler.Id];

    [Theory]
    [MemberData(nameof(Ids))]
    public void Phrase_matches_the_committed_render(string id) {
        using var instrument = id == AnodeSynth.Id ? Phrases.Synth() : Phrases.Sampler(folder);
        var (left, right) = Play.Render(instrument, Phrases.GoldenFrames, Phrases.Golden());
        Assert.True(Rms(left) > 0.01 && Rms(right) > 0.01);
        var path = Path.Combine(Play.EngineRoot(), "tests", "Anode.Dsp.Tests", "Golden", id + ".wav");

        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_GOLDEN") == "1"
            || (!File.Exists(path) && Environment.GetEnvironmentVariable("CI") is null)) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TestWav.Write(path, (int)Play.Rate, [left, right]);
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
        IInstrument Make() =>
            id == AnodeSynth.Id ? Phrases.Synth() : Phrases.Sampler(Path.Combine(folder, Guid.NewGuid().ToString("N")));

        using var a = Make();
        using var b = Make();
        var first = Play.Render(a, 30_000, Phrases.Golden()).Left;
        Assert.Equal(-300, NullResidualDb(first, Play.Render(b, 30_000, Phrases.Golden(), 333).Left));
        a.Reset();
        Assert.Equal(-300, NullResidualDb(first, Play.Render(a, 30_000, Phrases.Golden()).Left));
    }
}
