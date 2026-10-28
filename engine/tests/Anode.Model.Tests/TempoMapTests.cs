using Anode.Contracts;
using Anode.Model.Time;
using CsCheck;

namespace Anode.Model.Tests;

public sealed class TempoMapTests {
    /// <summary>Maps of steps and ramps.</summary>
    static readonly Gen<TempoMap> Maps = Gen.Select(
            Gen.Double[TempoMap.MinBpm, TempoMap.MaxBpm],
            Gen.Long[1, 100_000],
            Gen.Bool
        )
        .Array[0, 20]
        .Select(Gen.Double[TempoMap.MinBpm, TempoMap.MaxBpm], Gen.Bool)
        .Select(static (changes, first, firstRamp) => {
                var points = new List<TempoPoint> { new(0, first, firstRamp) };
                var tick = 0L;
                foreach (var (bpm, gap, ramp) in changes) {
                    tick += gap;
                    points.Add(new(tick, bpm, ramp));
                }

                return new TempoMap(points);
            }
        );

    [Fact]
    public void A_quarter_note_at_120_bpm_lasts_half_a_second() {
        var map = TempoMap.Constant(120);
        Assert.Equal(0.5, map.SecondsAt(TempoMap.Ppq), 12);
        Assert.Equal(24_000, map.SampleAt(TempoMap.Ppq, 48_000));
    }

    [Fact]
    public void Time_accumulates_across_tempo_changes() {
        // One bar of 4/4 at 120 (2 s), then 60 BPM: one more quarter note is one second.
        var map = new TempoMap([new TempoPoint(0, 120), new TempoPoint(4 * TempoMap.Ppq, 60)]);
        Assert.Equal(3.0, map.SecondsAt(5 * TempoMap.Ppq), 12);
        Assert.Equal(60, map.BpmAt(5 * TempoMap.Ppq));
        Assert.Equal(5 * TempoMap.Ppq, map.TickAt(3.0), 9);
    }

    [Fact]
    public void A_ramp_lasts_its_exact_integral() {
        // 60 → 120 BPM over a 4/4 bar: k = 60/3840 BPM per tick, so the bar lasts 60/(960·k)·ln 2 = 4·ln 2 s.
        var map = TempoMap.Of([new TempoEvent(0, 60, TempoCurve.Ramp), new TempoEvent(3840, 120, TempoCurve.Step)]);
        Assert.Equal(4 * Math.Log(2), map.SecondsAt(3840), 12);
        Assert.Equal(90, map.BpmAt(1920), 9);
        Assert.Equal(120, map.BpmAt(10_000));
        Assert.Equal(1920, map.TickAt(map.SecondsAt(1920)), 9);
        // After the ramp the tempo holds at 120: one more beat is half a second.
        Assert.Equal(4 * Math.Log(2) + 0.5, map.SecondsAt(3840 + 960), 12);
        Assert.Equal(3840 + 960, map.TickAt(4 * Math.Log(2) + 0.5), 9);
    }

    [Fact]
    public void A_falling_ramp_slows_down() {
        var map = new TempoMap([new TempoPoint(0, 120, true), new TempoPoint(3840, 60)]);
        Assert.Equal(4 * Math.Log(2), map.SecondsAt(3840), 12);
        Assert.True(map.SecondsAt(1920) < map.SecondsAt(3840) - map.SecondsAt(1920));
    }

    [Fact]
    public void A_ramp_on_the_last_change_holds_its_tempo() {
        var map = new TempoMap([new TempoPoint(0, 120), new TempoPoint(960, 60, true)]);
        Assert.Equal(1.5, map.SecondsAt(1920), 12);
        Assert.Equal(60, map.BpmAt(5000));
    }

    [Fact]
    public void Ticks_round_trip_through_seconds() =>
        Gen.Select(Maps, Gen.Long[0, 10_000_000])
            .Sample(static (map, tick) => Math.Abs(map.TickAt(map.SecondsAt(tick)) - tick) < 1e-6 * Math.Max(1, tick));

    [Fact]
    public void Ticks_round_trip_through_samples_within_a_tick() =>
        Gen.Select(Maps, Gen.Long[0, 10_000_000])
            .Sample(static (map, tick) => Math.Abs(map.TickAtSample(map.SampleAt(tick, 48_000), 48_000) - tick) <= 1);

    [Fact]
    public void Seconds_never_decrease_as_ticks_grow() =>
        Gen.Select(Maps, Gen.Long[0, 10_000_000], Gen.Long[0, 100_000])
            .Sample(static (map, tick, step) => map.SecondsAt(tick + step) >= map.SecondsAt(tick));

    [Fact]
    public void Seconds_match_numeric_integration_of_the_tempo() =>
        Gen.Select(Maps, Gen.Long[0, 2_000_000])
            .Sample(static (map, tick) => {
                    var seconds = Integrate(map, tick);
                    return Math.Abs(map.SecondsAt(tick) - seconds) <= 1e-6 * Math.Max(1, seconds);
                }
            );

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(1000)]
    public void A_map_starts_at_tick_zero(long first) =>
        Assert.Throws<ArgumentException>(() => new TempoMap([new TempoPoint(first, 120)]));

    [Fact]
    public void Changes_are_in_strictly_increasing_order() =>
        Assert.Throws<ArgumentException>(static () =>
            new TempoMap([new TempoPoint(0, 120), new TempoPoint(960, 100), new TempoPoint(960, 90)])
        );

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(double.NaN)]
    public void Tempo_is_in_range(double bpm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TempoMap.Constant(bpm));

    /// <summary>Simpson's rule over seconds per tick, segment by segment (a ramp's 1/bpm is smooth inside one).</summary>
    static double Integrate(TempoMap map, long tick) {
        const int n = 2048;
        var seconds = 0.0;
        for (var i = 0; i < map.Count && map[i].Tick < tick; i++) {
            double from = map[i].Tick;
            var to = i + 1 < map.Count ? Math.Min(map[i + 1].Tick, tick) : tick;
            var h = (to - from) / n;
            var sum = 0.0;
            for (var k = 0; k <= n; k++) {
                var weight = k == 0 || k == n ? 1 : (k & 1) == 1 ? 4 : 2;
                // The tempo inside segment i: at its own end, just before the next change.
                var bpm = Bpm(map, i, from + k * h);
                sum += weight * 60 / (bpm * TempoMap.Ppq);
            }

            seconds += sum * h / 3;
        }

        return seconds;
    }

    static double Bpm(TempoMap map, int segment, double tick) {
        var point = map[segment];
        if (!point.Ramp || segment + 1 >= map.Count) {
            return point.Bpm;
        }

        var next = map[segment + 1];
        return point.Bpm + (next.Bpm - point.Bpm) * (tick - point.Tick) / (next.Tick - point.Tick);
    }
}
