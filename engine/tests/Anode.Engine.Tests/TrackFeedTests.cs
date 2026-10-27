using Anode.Engine.Streaming;

namespace Anode.Engine.Tests;

public sealed class TrackFeedTests {
    static void Append(TrackFeed feed, int frames, float value) {
        var data = new float[frames * feed.Channels];
        Array.Fill(data, value);
        feed.Append(data);
    }

    [Fact]
    public void A_new_fill_takes_over_sample_exactly_at_its_first_clock() {
        using var feed = new TrackFeed(1, 4_096);
        var old = TrackFeed.Identity(1, 1);
        var edited = TrackFeed.Identity(1, 2);
        feed.BeginFill(old, 0);
        Append(feed, 1_000, 1f);
        feed.BeginFill(edited, 500);
        Append(feed, 1_000, 2f);

        var left = new float[400];
        var right = new float[400];
        Assert.Equal(400, feed.Read(0, 400, edited, 0, left, right));
        Assert.All(left, v => Assert.Equal(1f, v)); // the old fill plays on until the new one starts

        left = new float[200];
        Assert.Equal(200, feed.Read(400, 200, edited, 0, left, right));
        Assert.All(left[..100], v => Assert.Equal(1f, v));
        Assert.All(left[100..], v => Assert.Equal(2f, v)); // spliced at clock 500
    }

    [Fact]
    public void A_hard_anchor_drops_older_audio_at_once() {
        using var feed = new TrackFeed(2, 4_096);
        feed.BeginFill(TrackFeed.Identity(1, 1), 0);
        Append(feed, 1_000, 1f);
        var located = TrackFeed.Identity(2, 1);

        var left = new float[100];
        var right = new float[100];
        Assert.Equal(0, feed.Read(0, 100, located, 2, left, right)); // nothing for the new anchor yet: silence
        Assert.All(left, v => Assert.Equal(0f, v));

        feed.BeginFill(located, 100);
        Append(feed, 500, 3f);
        Assert.Equal(100, feed.Read(100, 100, located, 2, left, right));
        Assert.All(left, v => Assert.Equal(3f, v));
        Assert.All(right, v => Assert.Equal(3f, v));
    }

    [Fact]
    public void Late_audio_is_dropped_to_stay_in_time_and_a_future_chunk_waits() {
        using var feed = new TrackFeed(1, 4_096);
        var id = TrackFeed.Identity(1, 1);
        feed.BeginFill(id, 1_000);
        var data = Enumerable.Range(0, 500).Select(i => (float)i).ToArray();
        feed.Append(data);
        var left = new float[100];
        var right = new float[100];
        Assert.Equal(50, feed.Read(950, 100, id, 0, left, right)); // starts 50 frames into the block
        Assert.Equal(0f, left[49]);
        Assert.Equal(0f, left[50]);
        Assert.Equal(49f, left[99]);

        Assert.Equal(100, feed.Read(1_200, 100, id, 0, left, right)); // 150 late frames dropped
        Assert.Equal(200f, left[0]);
    }

    [Fact]
    public void Align_drops_stale_audio_while_held() {
        using var feed = new TrackFeed(1, 4_096);
        feed.BeginFill(TrackFeed.Identity(1, 1), 0);
        Append(feed, 4_000, 1f);
        Assert.True(feed.FreeFrames < 3_000);
        var current = TrackFeed.Identity(1, 2);
        feed.BeginFill(current, 0);
        Append(feed, 1_000, 2f);
        feed.Align(0, current, 0);
        Assert.Equal(6_144 - 1_000, feed.FreeFrames);
    }
}
