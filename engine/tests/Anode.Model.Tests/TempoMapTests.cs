using Anode.Model.Time;
using CsCheck;

namespace Anode.Model.Tests;

public sealed class TempoMapTests {
    static readonly Gen<TempoMap> Maps = Gen.Double[TempoMap.MinBpm, TempoMap.MaxBpm]
        .Select(Gen.Long[1, 100_000])
        .Array[0, 20]
        .Select(Gen.Double[TempoMap.MinBpm, TempoMap.MaxBpm])
        .Select(static (changes, first) => {
                var points = new List<TempoPoint> { new(0, first) };
                var tick = 0L;
                foreach (var (bpm, gap) in changes) {
                    tick += gap;
                    points.Add(new(tick, bpm));
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
        var map = new TempoMap([new(0, 120), new(4 * TempoMap.Ppq, 60)]);
        Assert.Equal(3.0, map.SecondsAt(5 * TempoMap.Ppq), 12);
        Assert.Equal(60, map.BpmAt(5 * TempoMap.Ppq));
        Assert.Equal(5 * TempoMap.Ppq, map.TickAt(3.0), 9);
    }

    [Fact]
    public void Ticks_round_trip_through_seconds() =>
        Gen.Select(Maps, Gen.Long[0, 10_000_000])
            .Sample(static (map, tick) => Math.Abs(map.TickAt(map.SecondsAt(tick)) - tick) < 1e-6);

    [Fact]
    public void Seconds_never_decrease_as_ticks_grow() =>
        Gen.Select(Maps, Gen.Long[0, 10_000_000], Gen.Long[0, 100_000])
            .Sample(static (map, tick, step) => map.SecondsAt(tick + step) >= map.SecondsAt(tick));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(1000)]
    public void A_map_starts_at_tick_zero(long first) =>
        Assert.Throws<ArgumentException>(() => new TempoMap([new(first, 120)]));

    [Fact]
    public void Changes_are_in_strictly_increasing_order() =>
        Assert.Throws<ArgumentException>(static () => new TempoMap([new(0, 120), new(960, 100), new(960, 90)]));

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(double.NaN)]
    public void Tempo_is_in_range(double bpm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TempoMap.Constant(bpm));
}
