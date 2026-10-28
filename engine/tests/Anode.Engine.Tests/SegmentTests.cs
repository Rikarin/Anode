using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

public sealed unsafe class SegmentTests {
    [Fact]
    public void Struct_sizes_match_the_documented_layout() {
        Assert.Equal(24, sizeof(PlaybackEvent));
        Assert.Equal(48, sizeof(AudioPlaylistEntry));
        Assert.Equal(40, sizeof(TrackEntry));
    }

    [Fact]
    public void A_written_segment_maps_validates_and_reads_back() {
        TrackModel[] tracks = [
            new(
                3,
                [new() { SamplePos = 10, Track = 3, Kind = PlaybackEventKind.Click, Data1 = 1 }],
                [
                    new() {
                        Start = 0,
                        FileOffset = 0,
                        Length = 100,
                        FileId = 7,
                        Gain = 1,
                        FadeIn = 4,
                        FadeOut = 4
                    },
                    new() {
                        Start = 200,
                        FileOffset = 50,
                        Length = 10,
                        FileId = 8,
                        Gain = 0.5f
                    }
                ]
            ),
            new(TrackEntry.MetronomeTrack, [], [])
        ];
        using var written = SegmentWriter.Write(tracks, 42, 48_000, "an-test", Environment.ProcessId, true);
        using var mapped = MappedSegment.Map(1, written.Name, written.Length);
        mapped.ValidateLayout();
        mapped.Verify();

        Assert.Equal(42, mapped.Header->ModelVersion);
        Assert.Equal(2, mapped.Header->TrackCount);
        Assert.Equal(3, mapped.TrackId(0));
        Assert.Equal(TrackEntry.MetronomeTrack, mapped.TrackId(1));
        var track = mapped.Track(0);
        Assert.Equal(1, track.EventCount);
        Assert.Equal(PlaybackEventKind.Click, track.Events[0].Kind);
        Assert.Equal(2, track.PlaylistCount);
        Assert.Equal(8, track.Playlist[1].FileId);
        Assert.Equal(0, mapped.Track(1).PlaylistCount);
    }
}
