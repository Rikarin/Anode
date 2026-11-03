using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Surround;
using Anode.Dsp.IO;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Tape;
using Anode.Dsp.Mastering;
using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Spatial;
using Anode.Dsp.Tests.Support;
using A = Anode.Dsp.Instruments.Alchemy.SampleAlchemyParameters;
using P = Anode.Dsp.Instruments.Physical.PhysicalModelParameters;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     Golden renders of the T3 instruments and effects (as <see cref="GoldenRenderTests" /> and
///     <see cref="EffectGoldenRenderTests" />): each must null against
///     <c>tests/Anode.Dsp.Tests/Golden/&lt;name&gt;.wav</c>
///     to −90 dB. Regenerate an intended change with <c>ANODE_UPDATE_GOLDEN=1 dotnet test</c> and listen to it.
/// </summary>
public sealed class T3GoldenRenderTests {
    const double ResidualDb = -90;

    public static TheoryData<string> Names => [
        PhysicalModel.Id, PhysicalModel.Id + "-bowed", TapeReplay.Id, SampleAlchemy.Id,
        SampleAlchemy.Id + "-granular", SampleAlchemy.Id + "-spectral", Saturation.Id, Saturation.Id + "-tape",
        Saturation.Id + "-transformer", Saturation.Id + "-modern", MasteringAssistant.Id, DownMixerInsert.Id,
        BinauralInsert.Id
    ];

    static IInstrument Instrument(string name) =>
        name switch {
            PhysicalModel.Id + "-bowed" => T3Kit.Instrument(
                PhysicalModel.Id,
                (P.ExciterType(1), (int)PhysicalExciter.Bow),
                (P.ExciterType(2), (int)PhysicalExciter.Pick),
                (P.ExciterType(2) + 2, 40),
                (P.VibratoDepth, 15),
                (P.Lfo2Pickup, 40),
                (P.Body, (int)PhysicalBody.Violin)
            ),
            SampleAlchemy.Id + "-granular" => T3Kit.Instrument(
                SampleAlchemy.Id,
                (A.Mode, (int)AlchemyMode.Granular),
                (A.GrainPitch, 30)
            ),
            SampleAlchemy.Id + "-spectral" => T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, (int)AlchemyMode.Spectral)),
            _ => T3Kit.Instrument(name)
        };

    /// <summary>The render of <paramref name="name" /> in buffers of <paramref name="block" /> frames.</summary>
    static (float[] Left, float[] Right) Render(string name, int block = Play.Block) {
        if (name.StartsWith(Saturation.Id, StringComparison.Ordinal) || name == MasteringAssistant.Id) {
            using var effect = name switch {
                MasteringAssistant.Id => Mastered(),
                Saturation.Id + "-tape" => T3Kit.Effect(
                    Saturation.Id,
                    (Saturation.Model, Saturation.Tape),
                    (Saturation.Drive, 70),
                    (Saturation.Tone, 40)
                ),
                Saturation.Id + "-transformer" => T3Kit.Effect(
                    Saturation.Id,
                    (Saturation.Model, Saturation.Transformer),
                    (Saturation.Drive, 70)
                ),
                Saturation.Id + "-modern" => T3Kit.Effect(
                    Saturation.Id,
                    (Saturation.Model, Saturation.Modern),
                    (Saturation.Drive, 70),
                    (Saturation.Mix, 60)
                ),
                _ => T3Kit.Effect(Saturation.Id, (Saturation.Drive, 60), (Saturation.Tone, -30))
            };
            var (left, right) = Fx.Music(24_000);
            return Fx.Run(effect, left, right, block);
        }

        if (name is DownMixerInsert.Id or BinauralInsert.Id) {
            return Surround(name, block);
        }

        using var instrument = Instrument(name);
        return Play.Render(instrument, Phrases.GoldenFrames, Phrases.Golden(), block);
    }

    static MasteringAssistant Mastered() {
        var (left, right) = Fx.Music();
        var assistant = (MasteringAssistant)T3Kit.Effect(MasteringAssistant.Id, (MasteringAssistant.Character, 1));
        assistant.SetChain(
            MasteringChainDesigner.Design(MasteringAnalyzer.Analyse(left, right, Fx.Rate), MasteringCharacterKind.Valve)
        );
        return assistant;
    }

    /// <summary>A 7.1.4 mix (a different note in each speaker) through the insert to stereo.</summary>
    static (float[] Left, float[] Right) Surround(string name, int block) {
        const int frames = 24_000;
        var input = SpeakerLayout.Of(SpeakerFormat.Surround714);
        IFormatEffect effect = name == DownMixerInsert.Id ? new DownMixerInsert() : new BinauralInsert();
        if (effect is BinauralInsert binaural) {
            binaural.UseHrtfs(HrtfSet.Load(HrtfSet.Find(Ear.RepositoryRoot())!));
        } else {
            effect.SetParameter(DownMixerInsert.Lfe, -10);
        }

        using var _ = effect;
        effect.Prepare(input, SpeakerLayout.Of(SpeakerFormat.Stereo), Fx.Rate, 1_024);
        var channels = input.ChannelCount;
        var signal = new float[channels * frames];
        for (var c = 0; c < channels; c++) {
            var pluck = TestWav.Pluck(NoteFrequency(48 + 3 * c), (int)Fx.Rate, frames / Fx.Rate, c + 1);
            for (var i = 0; i < frames; i++) {
                signal[c * frames + i] = 0.2f * pluck[i];
            }
        }

        var left = new float[frames];
        var right = new float[frames];
        var inBuffer = new float[channels * 1_024];
        var outBuffer = new float[2 * 1_024];
        for (var at = 0; at < frames; at += block) {
            var n = Math.Min(block, frames - at);
            var inBlock = new PlanarBlock(inBuffer, channels, n);
            for (var c = 0; c < channels; c++) {
                signal.AsSpan(c * frames + at, n).CopyTo(inBlock.Channel(c));
            }

            var outBlock = new PlanarBlock(outBuffer, 2, n);
            effect.Process(inBlock, outBlock);
            outBlock.Channel(0).CopyTo(left.AsSpan(at, n));
            outBlock.Channel(1).CopyTo(right.AsSpan(at, n));
        }

        return (left, right);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Render_matches_the_committed_one(string name) {
        var (left, right) = Render(name);
        Assert.True(Rms(left) > 0.005 && Rms(right) > 0.005);
        Assert.True(Peak(left) < 1 && Peak(right) < 1, "a golden render must fit a 24-bit file");
        var path = Path.Combine(Play.EngineRoot(), "tests", "Anode.Dsp.Tests", "Golden", name + ".wav");
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_GOLDEN") == "1"
            || (!File.Exists(path) && Environment.GetEnvironmentVariable("CI") is null)) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TestWav.Write(path, (int)Play.Rate, [left, right]);
            return;
        }

        Assert.True(File.Exists(path), $"No golden render for {name}: run ANODE_UPDATE_GOLDEN=1 dotnet test.");
        var golden = WavReader.Read(path);
        var l = NullResidualDb(golden.Channels[0], left);
        var r = NullResidualDb(golden.Channels[1], right);
        Assert.True(
            l < ResidualDb && r < ResidualDb,
            $"{name} no longer matches its golden render (left {l:0.0} dB, right {r:0.0} dB). If the change is intended, "
            + "regenerate with ANODE_UPDATE_GOLDEN=1."
        );
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Rendering_is_deterministic(string name) {
        var first = Render(name);
        var second = Render(name, 333);
        Assert.Equal(-300, NullResidualDb(first.Left, second.Left));
        Assert.Equal(-300, NullResidualDb(first.Right, second.Right));
    }
}
