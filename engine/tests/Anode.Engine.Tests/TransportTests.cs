using CsCheck;

namespace Anode.Engine.Tests;

public sealed class TransportTests {
    [Fact]
    public void Advances_only_while_playing() {
        var transport = new Transport();
        transport.Advance(64);
        Assert.Equal(0, transport.Position);
        Assert.Equal(0, transport.Clock);

        transport.Play();
        transport.Advance(64);
        transport.Advance(64);
        Assert.Equal(128, transport.Position);
        Assert.Equal(128, transport.Clock);

        transport.Stop();
        transport.Advance(64);
        Assert.Equal(128, transport.Position);
    }

    [Fact]
    public void Locate_moves_the_playhead_with_a_hard_anchor() {
        var transport = new Transport();
        var first = transport.Anchor;
        transport.Locate(48_000);
        Assert.Equal(48_000, transport.Position);
        Assert.True(transport.Anchor.Hard);
        Assert.True(transport.Anchor.Id > first.Id);
        Assert.Equal(48_000, transport.Anchor.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.Locate(-1));
    }

    [Fact]
    public void The_cycle_wraps_sample_exactly() {
        var transport = new Transport();
        transport.SetCycle(new(true, 1_000, 1_100));
        transport.Locate(1_090);
        transport.Play();
        transport.Advance(64);
        Assert.Equal(1_000 + 54, transport.Position); // 10 frames to the end, 54 from the start
        transport.Advance(46);
        Assert.Equal(1_000, transport.Position); // exactly at the end: back to the start
        transport.Advance(250);
        Assert.Equal(1_050, transport.Position); // two and a half laps
    }

    [Fact]
    public void A_playhead_past_the_cycle_end_plays_on() {
        var cycle = new Cycle(true, 100, 200);
        Assert.Equal(long.MaxValue, cycle.UntilJump(250));
        Assert.Equal(314, cycle.Advance(250, 64));
        Assert.Equal(50, cycle.UntilJump(150));
        Assert.Equal(160, cycle.Advance(50, 110)); // enters the cycle from before it, then loops at 200
        Assert.Equal(110, cycle.Advance(50, 160));
    }

    [Fact]
    public void An_empty_or_disabled_cycle_is_inactive() {
        Assert.False(new Cycle(true, 100, 100).Active);
        Assert.False(new Cycle(true, 200, 100).Active);
        Assert.False(new Cycle(false, 0, 100).Active);
        Assert.Equal(1_064, new Cycle(true, 200, 100).Advance(1_000, 64));
    }

    [Fact]
    public void Advancing_in_steps_equals_advancing_at_once() =>
        Gen.Select(Gen.Long[0, 10_000], Gen.Long[1, 5_000], Gen.Long[0, 20_000], Gen.Int[1, 4096].Array[1, 20])
            .Sample(static (start, length, position, steps) => {
                    var cycle = new Cycle(true, start, start + length);
                    var stepped = position;
                    long total = 0;
                    foreach (var step in steps) {
                        stepped = cycle.Advance(stepped, step);
                        total += step;
                    }

                    return stepped == cycle.Advance(position, total);
                }
            );

    [Fact]
    public void The_anchor_maps_every_clock_to_the_transport_position() =>
        Gen.Select(Gen.Long[0, 10_000], Gen.Long[1, 5_000], Gen.Long[0, 20_000], Gen.Int[1, 512].Array[1, 40])
            .Sample(static (start, length, position, blocks) => {
                    var transport = new Transport();
                    transport.SetCycle(new(true, start, start + length));
                    transport.Locate(position);
                    transport.Play();
                    var anchor = transport.Anchor;
                    foreach (var block in blocks) {
                        transport.Advance(block);
                        if (anchor.PositionAt(transport.Clock) != transport.Position) {
                            return false;
                        }
                    }

                    return true;
                }
            );

    [Fact]
    public void Recording_ignores_the_cycle_and_changes_the_anchor() {
        var transport = new Transport();
        transport.SetCycle(new(true, 0, 1_000));
        transport.Locate(900);
        var located = transport.Anchor;
        transport.PrepareRecord();
        Assert.Equal(Cycle.Off, transport.PlayOrder);
        Assert.False(transport.Anchor.Hard);
        Assert.True(transport.Anchor.Id > located.Id);
        var pending = transport.Anchor;
        transport.Record();
        Assert.Equal(pending, transport.Anchor); // same play order: no new anchor
        transport.Advance(200);
        Assert.Equal(1_100, transport.Position); // linear through the cycle end
        transport.Stop();
        Assert.Equal(new(true, 0, 1_000), transport.PlayOrder);
        Assert.True(transport.Anchor.Id > pending.Id);
    }

    [Fact]
    public void Setting_the_same_cycle_keeps_the_anchor() {
        var transport = new Transport();
        transport.SetCycle(new(true, 0, 1_000));
        var anchor = transport.Anchor;
        transport.SetCycle(new(true, 0, 1_000));
        Assert.Equal(anchor, transport.Anchor);
    }
}
