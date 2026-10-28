using Anode.Contracts;
using Anode.Model.Time;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     <see cref="RegionTiming" /> against hand-worked maps (docs/06-phase-5.md, "Region timing"), and the shared vectors
///     the editor checks its mirror against (<c>RegionTimingVectors.json</c>, rewritten with
///     <c>ANODE_UPDATE_VECTORS=1</c>).
/// </summary>
public sealed class RegionTimingTests {
    const int Rate = 48_000;

    /// <summary>A file beat at 100 bpm, in frames.</summary>
    const long Beat100 = 28_800;

    static readonly TempoEvent[] At120 = [new(0, 120, TempoCurve.Step)];
    static readonly SignatureEvent[] FourFour = [new(0, 4, 4)];

    static AudioRegion Region(
        long startTick,
        long offset,
        long length,
        FlexFollow follow = FlexFollow.Off,
        IReadOnlyList<FlexMarker>? markers = null
    ) =>
        new(
            "rgn_1",
            "trk_1",
            "aud_1",
            "R",
            startTick,
            offset,
            length,
            0,
            0,
            0,
            0,
            0,
            false,
            follow,
            markers ?? [],
            0,
            0,
            []
        );

    static RegionTiming Timing(
        AudioRegion region,
        FileTempo? tempo,
        bool flexOn,
        IReadOnlyList<TempoEvent>? tempoEvents = null,
        IReadOnlyList<SignatureEvent>? signatures = null
    ) =>
        RegionTiming.Of(
            region,
            tempo,
            flexOn,
            TempoMap.Of(tempoEvents ?? At120),
            new(signatures ?? FourFour),
            Rate
        );

    static readonly FileTempo Steady100 = new(
        100,
        false,
        [..Enumerable.Range(0, 20).Select(static i => i * Beat100)],
        [
            ..Enumerable.Range(0, 5).Select(static i => i * 4 * Beat100)
        ]
    );

    [Fact]
    public void An_unwarped_region_plays_its_frames_one_to_one_from_its_start() {
        // 120 bpm: tick 960 is half a second, sample 24 000; 48 000 frames later is tick 960 + 1920.
        var t = Timing(Region(960, 1_000, 96_000), null, false);
        Assert.False(t.Warped);
        Assert.Equal(24_000, t.StartSample);
        Assert.Equal(24_000 + 96_000, t.EndSample);
        Assert.Equal(960 + 3_840, t.EndTick, 9);
        Assert.Equal(960 + 1_920, t.TickAt(1_000 + 48_000), 9);
        Assert.Equal(1_000 + 48_000, t.FrameAt(960 + 1_920), 6);
    }

    [Fact]
    public void Flex_without_follow_plays_in_time() {
        var t = Timing(Region(960, 1_000, 96_000), Steady100, true);
        Assert.True(t.Warped);
        Assert.Equal(24_000 + 96_000, t.EndSample);
        Assert.Equal(960 + 1_920, t.TickAt(1_000 + 48_000), 9);
    }

    [Fact]
    public void Follow_on_maps_a_file_beat_to_a_quarter_note() {
        // A 100 bpm file at 120 bpm: 28 800 frames are 960 ticks. Ten beats end at tick 3840 + 9600.
        var t = Timing(Region(3_840, 14_400, 10 * Beat100, FlexFollow.On), Steady100, false);
        Assert.Equal(3_840 + 9_600, t.EndTick, 9);
        Assert.Equal(3_840 + 960, t.TickAt(14_400 + Beat100), 9);
        Assert.Equal(14_400 + Beat100 / 2, t.FrameAt(3_840 + 480), 6);
        // 13 440 ticks at 120 bpm are 7 s.
        Assert.Equal(7 * Rate, t.EndSample);
    }

    [Fact]
    public void Follow_without_a_file_tempo_is_time() {
        var t = Timing(Region(0, 0, 48_000, FlexFollow.On), null, false);
        Assert.Equal(FlexFollow.Off, t.Follow);
        Assert.Equal(48_000, t.EndSample);
    }

    [Fact]
    public void Follow_bars_lands_downbeats_on_bar_lines() {
        // Downbeats at 120 000 and 230 400 (a humanised 100 bpm bar is 115 200). The on map puts the first at tick
        // 4000: the nearest bar line after the start is 3840, the next downbeat on 7680; after it the on slope
        // (1/30 tick per frame) carries on: frame 300 000 is 7680 + 69 600 / 30 = 10 000.
        var tempo = new FileTempo(100, true, [], [0, 120_000, 230_400]);
        var t = Timing(Region(0, 0, 300_000, FlexFollow.Bars), tempo, false);
        Assert.Equal(3_840, t.TickAt(120_000), 9);
        Assert.Equal(7_680, t.TickAt(230_400), 9);
        Assert.Equal(1_920, t.TickAt(60_000), 9);
        Assert.Equal(10_000, t.EndTick, 9);
        Assert.Equal(175_200, t.FrameAt(5_760), 6);
    }

    [Fact]
    public void Follow_bars_and_beats_lands_each_beat_on_the_next_beat() {
        // Beats 30 000 frames apart (96 bpm), the region from frame 10 000 at tick 480: the first beat after it (frame
        // 30 000) is put at 480 + 20 000/30 = 1146.7 by the on map, nearest beat after 480 is 960; then 1920, 2880.
        var tempo = new FileTempo(100, false, [0, 30_000, 60_000, 90_000], [0]);
        var t = Timing(Region(480, 10_000, 100_000, FlexFollow.BarsAndBeats), tempo, false);
        Assert.Equal(960, t.TickAt(30_000), 9);
        Assert.Equal(1_920, t.TickAt(60_000), 9);
        Assert.Equal(2_880, t.TickAt(90_000), 9);
        // Before the first beat: linear from the start (frame 10 000 at tick 480).
        Assert.Equal(720, t.TickAt(20_000), 9);
        Assert.Equal(2_880 + 20_000 / 30.0, t.EndTick, 9);
    }

    [Fact]
    public void Markers_scale_the_base_map_between_them_and_keep_the_ends() {
        // Follow on, ten 100-bpm beats from tick 0 (base end 9600); a marker pulls frame 144 000 (base 4800) to 4000.
        var t = Timing(Region(0, 0, 10 * Beat100, FlexFollow.On, [new(144_000, 4_000)]), Steady100, true);
        Assert.Equal(9_600, t.EndTick, 9);
        Assert.Equal(4_000, t.TickAt(144_000), 9);
        Assert.Equal(2_000, t.TickAt(72_000), 9); // 2400 · 4000/4800
        Assert.Equal(6_800, t.TickAt(216_000), 9); // 4000 + (7200 − 4800) · 5600/4800
        Assert.Equal(216_000, t.FrameAt(6_800), 6);
    }

    [Fact]
    public void Markers_out_of_order_or_outside_are_ignored() {
        var t = Timing(
            Region(0, 0, 10 * Beat100, FlexFollow.On, [new(144_000, 4_000), new(150_000, 3_000), new(400_000, 100)]),
            Steady100,
            true
        );
        Assert.Equal([new FlexMarker(144_000, 4_000)], t.Markers);
    }

    [Fact]
    public void A_curved_ramp_lasts_its_integral() {
        // 60 → 120 bpm over a bar: shape 0.5 makes the tempo 60 + 60·√x, so the bar lasts 4·∫dx/(1+√x) =
        // 4·(2 − 2 ln 2) s; shape −0.5 makes it 60 + 60·x², 4·∫dx/(1+x²) = π s.
        var concave = TempoMap.Of([new(0, 60, TempoCurve.Ramp, 0.5), new(3_840, 120, TempoCurve.Step)]);
        Assert.Equal(8 - 8 * Math.Log(2), concave.SecondsAt(3_840), 8);
        Assert.Equal(60 + 60 * Math.Sqrt(0.5), concave.BpmAt(1_920), 9);
        Assert.Equal(1_920, concave.TickAt(concave.SecondsAt(1_920)), 6);
        var convex = TempoMap.Of([new(0, 60, TempoCurve.Ramp, -0.5), new(3_840, 120, TempoCurve.Step)]);
        Assert.Equal(Math.PI, convex.SecondsAt(3_840), 8);
        Assert.Equal(Math.PI + 0.5, convex.SecondsAt(3_840 + 960), 8);
        // Half way: 4·atan(0.5) s.
        Assert.Equal(4 * Math.Atan(0.5), convex.SecondsAt(1_920), 8);
    }

    [Fact]
    public void Following_regions_ride_a_curved_ramp() {
        // A 60 bpm file following a 60 → 120 ramp of shape −0.5: file frame 96 000 (two beats) is tick 1920, which
        // plays 4·atan(0.5) s in.
        var ramp = new TempoEvent[] { new(0, 60, TempoCurve.Ramp, -0.5), new(3_840, 120, TempoCurve.Step) };
        var tempo = new FileTempo(60, false, [0, 48_000, 96_000, 144_000], [0]);
        var t = Timing(Region(0, 0, 192_000, FlexFollow.On), tempo, false, ramp);
        Assert.Equal(1_920, t.TickAt(96_000), 9);
        Assert.Equal(4 * Math.Atan(0.5) * Rate, t.SampleAt(96_000), 3);
        Assert.Equal((long)Math.Round(Math.PI * Rate), t.EndSample);
    }

    [Fact]
    public void Varispeed_divides_time_by_the_speed() {
        var map = TempoMap.Constant(120).WithSpeed(0.9);
        Assert.Equal(0.5 / 0.9, map.SecondsAt(960), 12);
        Assert.Equal(960, map.TickAt(0.5 / 0.9), 9);
        Assert.Equal(120, map.BpmAt(960));
    }

    /// <summary>The cases of the shared vectors: inputs, and the frames whose ticks and samples they record.</summary>
    static IEnumerable<(string Name, IReadOnlyList<TempoEvent> Tempo, IReadOnlyList<SignatureEvent> Signatures, bool
        FlexOn, AudioRegion Region, FileTempo? File, long[] Frames)> Cases() {
        static long[] Frames(long offset, long length, int n) =>
            [..Enumerable.Range(0, n + 1).Select(i => offset + length * i / n)];

        yield return ("unwarped", At120, FourFour, false, Region(960, 1_000, 96_000), null, Frames(1_000, 96_000, 4));
        yield return ("flex, time", At120, FourFour, true, Region(960, 1_000, 96_000), Steady100,
            Frames(1_000, 96_000, 4));
        yield return ("follow on", At120, FourFour, false, Region(3_840, 14_400, 288_000, FlexFollow.On), Steady100,
            Frames(14_400, 288_000, 8));
        yield return ("follow on, no file tempo", At120, FourFour, false, Region(0, 0, 48_000, FlexFollow.On), null,
            Frames(0, 48_000, 2));
        yield return ("follow bars", At120, FourFour, false, Region(0, 0, 300_000, FlexFollow.Bars),
            new(100, true, [], [0, 120_000, 230_400]), [0, 60_000, 120_000, 175_200, 230_400, 300_000]);
        yield return ("follow bars and beats", At120, FourFour, false,
            Region(480, 10_000, 100_000, FlexFollow.BarsAndBeats),
            new(100, false, [0, 30_000, 60_000, 90_000], [0]), [10_000, 20_000, 30_000, 60_000, 90_000, 110_000]);
        yield return ("follow bars in 7/8 after a signature change", At120, [new(0, 4, 4), new(3_840, 7, 8)], false,
            Region(0, 0, 400_000, FlexFollow.Bars), new(100, false, [], [0, 115_200, 230_400, 345_600]),
            [0, 115_200, 230_400, 345_600, 400_000]);
        yield return ("markers", At120, FourFour, true, Region(0, 0, 288_000, FlexFollow.On, [new(144_000, 4_000)]),
            Steady100, [0, 72_000, 144_000, 216_000, 288_000]);
        yield return ("markers ignored", At120, FourFour, true,
            Region(0, 0, 288_000, FlexFollow.On, [new(144_000, 4_000), new(150_000, 3_000), new(400_000, 100)]),
            Steady100, [0, 144_000, 150_000, 288_000]);
        yield return ("markers on time", At120, FourFour, true,
            Region(1_920, 0, 96_000, FlexFollow.Off, [new(48_000, 1_000)]),
            null, [0, 24_000, 48_000, 72_000, 96_000]);
        var linear = new TempoEvent[] { new(0, 60, TempoCurve.Ramp), new(3_840, 120, TempoCurve.Step) };
        var convex = new TempoEvent[] { new(0, 60, TempoCurve.Ramp, -0.5), new(3_840, 120, TempoCurve.Step) };
        var concave = new TempoEvent[] { new(0, 60, TempoCurve.Ramp, 0.5), new(3_840, 120, TempoCurve.Step) };
        var file60 = new FileTempo(60, false, [0, 48_000, 96_000, 144_000, 192_000], [0, 192_000]);
        yield return ("unwarped over a linear ramp", linear, FourFour, false, Region(0, 0, 240_000), null,
            Frames(0, 240_000, 5));
        yield return ("follow on over a linear ramp", linear, FourFour, false, Region(0, 0, 240_000, FlexFollow.On),
            file60,
            Frames(0, 240_000, 5));
        yield return ("follow on over a convex ramp", convex, FourFour, false, Region(0, 0, 240_000, FlexFollow.On),
            file60,
            Frames(0, 240_000, 5));
        yield return ("follow on over a concave ramp", concave, FourFour, false, Region(0, 0, 240_000, FlexFollow.On),
            file60,
            Frames(0, 240_000, 5));
        yield return ("markers over a concave ramp", concave, FourFour, true,
            Region(480, 0, 192_000, FlexFollow.On, [new(96_000, 2_000)]), file60, Frames(0, 192_000, 4));
    }

    [Fact]
    public void The_shared_vectors_are_what_RegionTiming_computes() {
        var cases = new JsonArray();
        foreach (var (name, tempo, signatures, flexOn, region, file, frames) in Cases()) {
            var t = Timing(region, file, flexOn, tempo, signatures);
            cases.Add(
                new JsonObject {
                    ["name"] = name,
                    ["sampleRate"] = Rate,
                    ["tempoEvents"] = Node(tempo),
                    ["signatures"] = Node(signatures),
                    ["flexOn"] = flexOn,
                    ["region"] = Node(region),
                    ["fileTempo"] = file is null ? null : Node(file),
                    ["expected"] = new JsonObject {
                        ["warped"] = t.Warped,
                        ["follow"] = JsonSerializer.SerializeToNode(t.Follow, ContractsJsonContext.Default.FlexFollow),
                        ["startSample"] = t.StartSample,
                        ["endSample"] = t.EndSample,
                        ["endTick"] = Round(t.EndTick),
                        ["points"] = new JsonArray(
                            [
                                ..frames.Select(f => (JsonNode)new JsonObject {
                                        ["frame"] = f, ["tick"] = Round(t.TickAt(f)), ["sample"] = Round(t.SampleAt(f))
                                    }
                                )
                            ]
                        )
                    }
                }
            );
        }

        var document = new JsonObject {
            ["about"] = "RegionTiming vectors (docs/06-phase-5.md): Anode.Model's RegionTiming computes these; the "
                + "editor's mirror must match ticks and samples within 1e-6 (relative 1e-9 on ramps). Ticks are absolute "
                + "project ticks (960 per quarter note), samples project samples (fractional), frames file frames.",
            ["cases"] = cases
        };
        var json = document.ToJsonString(new() { WriteIndented = true }) + "\n";
        var path = VectorsPath();
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_VECTORS") == "1") {
            File.WriteAllText(path, json);
        }

        Assert.Equal(json, File.ReadAllText(path));
    }

    static double Round(double value) => Math.Round(value, 6);

    static JsonNode Node<T>(T value) =>
        JsonSerializer.SerializeToNode(
            value,
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ContractsJsonContext.Default.GetTypeInfo(
                typeof(T)
            )!
        )!;

    static string VectorsPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "RegionTimingVectors.json");
}
