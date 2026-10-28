using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

public sealed unsafe class SegmentTests {
    [Fact]
    public void Struct_sizes_match_the_documented_layout() {
        Assert.Equal(24, sizeof(PlaybackEvent));
        Assert.Equal(72, sizeof(AudioPlaylistEntry));
        Assert.Equal(40, sizeof(TrackEntry));
        Assert.Equal(16, sizeof(WarpPoint));
        Assert.Equal(24, sizeof(PitchPoint));
        Assert.Equal(sizeof(WarpPoint), sizeof(Dsp.Flex.FlexWarpPoint));
        Assert.Equal(sizeof(PitchPoint), sizeof(Dsp.Flex.FlexPitchPoint));
        Assert.Equal(
            Enum.GetNames<FlexAlgorithm>().Select(static n => (n, (byte)Enum.Parse<FlexAlgorithm>(n))),
            Enum.GetNames<Dsp.Flex.FlexKind>().Select(static n => (n, (byte)Enum.Parse<Dsp.Flex.FlexKind>(n)))
        );
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

    [Fact]
    public void Warp_and_pitch_ranges_are_rebased_into_the_segments_arrays() {
        static AudioPlaylistEntry Warped(long start, int warpStart, int pitchCount) =>
            new() {
                Start = start,
                Length = 1_000,
                FileId = 1,
                Gain = 1,
                WarpStart = warpStart,
                WarpCount = 2,
                PitchStart = 0,
                PitchCount = pitchCount,
                Algorithm = FlexAlgorithm.Polyphonic
            };

        static WarpPoint[] Map(double from) => [
            new() { Offset = 0, FileFrame = from }, new() { Offset = 1_000, FileFrame = from + 500 }
        ];

        TrackModel[] tracks = [
            new(1, [], [Warped(0, 0, 0), Warped(2_000, 2, 1)], [..Map(0), ..Map(10)], [new() { Cents = 100 }]),
            new(
                2,
                [],
                [new() { Start = 0, Length = 10, FileId = 1, Gain = 1 }, Warped(500, 0, 1)],
                Map(20),
                [new() { Cents = 200 }]
            )
        ];
        using var written = SegmentWriter.Write(tracks, 1, 48_000, "an-test-warp", Environment.ProcessId, true);
        using var mapped = MappedSegment.Map(2, written.Name, written.Length);
        mapped.ValidateLayout();
        mapped.Verify();

        Assert.Equal(6, mapped.Header->WarpCount);
        Assert.Equal(2, mapped.Header->PitchCount);
        var second = mapped.Track(1);
        Assert.Equal(0, second.Playlist[0].WarpCount);
        var entry = second.Playlist[1];
        Assert.Equal(4, entry.WarpStart);
        Assert.Equal(1, entry.PitchStart);
        Assert.Equal(20, second.Warp[entry.WarpStart].FileFrame);
        Assert.Equal(200, second.Pitch[entry.PitchStart].Cents);
        Assert.Equal(10, mapped.Track(0).Warp[mapped.Track(0).Playlist[1].WarpStart].FileFrame);
    }

    static long ids;

    /// <summary>Maps a segment of one track and runs the layout check (which the engine always runs).</summary>
    static void Validate(
        AudioPlaylistEntry entry,
        WarpPoint[] warp,
        PitchPoint[]? pitch = null,
        ushort? format = null
    ) {
        using var written = SegmentWriter.Write(
            [new(1, [], [entry], warp, pitch)],
            1,
            48_000,
            "an-test-flex",
            Environment.ProcessId * 100L + Interlocked.Increment(ref ids),
            false
        );
        if (format is { } f) {
            ((SegmentHeader*)written.Pointer)->Format = f;
        }

        using var mapped = MappedSegment.Map(3, written.Name, written.Length);
        mapped.ValidateLayout();
    }

    [Fact]
    public void Format_2_and_warp_maps_a_renderer_cannot_play_are_refused() {
        var entry = new AudioPlaylistEntry {
            Start = 0,
            Length = 1_000,
            FileId = 0,
            Gain = 1,
            WarpCount = 3,
            Algorithm = FlexAlgorithm.Slicing
        };
        WarpPoint[] good = [
            new() { Offset = 0, FileFrame = 0 }, new() { Offset = 400, FileFrame = 600 },
            new() { Offset = 1_000, FileFrame = 1_200 }
        ];
        Validate(entry, good, [new() { FileFrame = 0, Cents = 50 }, new() { FileFrame = 100, Cents = 70 }]);
        Validate(entry with { WarpCount = 0, Algorithm = FlexAlgorithm.None }, good); // unwarped, the points unused

        Assert.Throws<InvalidDataException>(() => Validate(entry, good, format: 2));
        Assert.Throws<InvalidDataException>(() => Validate(entry with { Algorithm = FlexAlgorithm.None }, good));
        Assert.Throws<InvalidDataException>(() => Validate(entry with { Algorithm = (FlexAlgorithm)9 }, good));
        Assert.Throws<InvalidDataException>(() => Validate(entry with { WarpCount = 1 }, good));
        Assert.Throws<InvalidDataException>(() => Validate(entry with { WarpCount = 4 }, good));
        Assert.Throws<InvalidDataException>(() => Validate(entry with { Length = 999 }, good));
        Assert.Throws<InvalidDataException>(() => Validate(entry with { PitchCount = 1, PitchStart = 2 }, good, []));
        WarpPoint[] late = [good[0] with { Offset = 1 }, good[1], good[2]];
        Assert.Throws<InvalidDataException>(() => Validate(entry, late));
        WarpPoint[] backwards = [good[0], good[1] with { Offset = 1_000 }, good[2]];
        Assert.Throws<InvalidDataException>(() => Validate(entry, backwards));
        WarpPoint[] nan = [good[0], good[1] with { FileFrame = double.NaN }, good[2]];
        Assert.Throws<InvalidDataException>(() => Validate(entry, nan));
        Assert.Throws<InvalidDataException>(() => Validate(
                entry with { PitchCount = 2 },
                good,
                [new() { FileFrame = 100 }, new() { FileFrame = 50 }]
            )
        );
    }
}
