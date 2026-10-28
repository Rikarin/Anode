using Anode.Engine.Recording;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Recording: takes as 24-bit files, the round-trip latency shift, cycle recording (one file across the wraps, its
///     passes), and autopunch (recording and monitoring only inside the range, sample-exactly at both edges, with and
///     without a cycle). The engine runs offline; the input is a known pattern numbered by the input frame.
/// </summary>
public sealed unsafe class RecorderTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    const int Seed = 20;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void A_take_writes_exactly_what_it_was_fed_as_24_bit_wav() {
        var path = Path.Combine(dir, "1-take.wav");
        using var take = new RecordTake(1, path, 2, Rate);
        var left = new float[64];
        var right = new float[64];
        var scratch = new float[1024]; // small, so a drain takes several reads
        for (var block = 0; block < 100; block++) {
            for (var i = 0; i < 64; i++) {
                left[i] = TestAudio.Sample(block * 64 + i, 3);
                right[i] = TestAudio.Sample(block * 64 + i, 4);
            }

            fixed (float* l = left, r = right) {
                take.Push(l, r, 64, 4_800 + block * 64);
            }

            if (block % 7 == 0) {
                take.Drain(scratch);
            }
        }

        var result = take.Complete(scratch);
        Assert.Equal(new(1, path, 4_800, 6_400, 2, Rate), result);
        Assert.Null(result.Passes); // one linear pass
        var samples = TestAudio.Read24(path, out var channels);
        Assert.Equal(2, channels);
        Assert.Equal(12_800, samples.Length);
        for (var f = 0; f < 6_400; f++) {
            Assert.Equal(TestAudio.Pattern(f, 3), samples[2 * f]);
            Assert.Equal(TestAudio.Pattern(f, 4), samples[2 * f + 1]);
        }

        // The engine reads its own takes back.
        using var file = new Streaming.StreamFile(0, path);
        Assert.Equal(Streaming.SampleEncoding.Pcm24, file.Format.Encoding);
        Assert.Equal(6_400, file.Frames);
    }

    [Fact]
    public void A_push_that_does_not_continue_the_last_starts_a_pass() {
        var path = Path.Combine(dir, "passes.wav");
        using var take = new RecordTake(1, path, 1, Rate);
        var input = new float[100];
        fixed (float* p = input) {
            take.Push(p, null, 100, 1_000);
            take.Push(p, null, 50, 1_100); // continues
            take.Push(p, null, 30, 500); // a wrap
            take.Push(p, null, 20, 530);
            take.Push(p, null, 10, 2_000); // a jump forward
        }

        var result = take.Complete(new float[4096]);
        Assert.Equal(1_000, result.StartSample);
        Assert.Equal(210, result.Frames);
        Assert.Equal([new(1_000, 0, 150), new(500, 150, 50), new(2_000, 200, 10)], result.Passes!);
    }

    [Fact]
    public void Samples_are_rounded_and_clipped_to_24_bits() {
        Assert.Equal(8_388_607, WavWriter.ToInt24(1f));
        Assert.Equal(8_388_607, WavWriter.ToInt24(2f));
        Assert.Equal(-8_388_608, WavWriter.ToInt24(-1f));
        Assert.Equal(-8_388_608, WavWriter.ToInt24(-3f));
        Assert.Equal(1, WavWriter.ToInt24(0.6f / 8_388_608f));
        Assert.Equal(0, WavWriter.ToInt24(0f));
    }

    /// <summary>An offline engine with one armed stereo track (7) on inputs 0–1, fed a pattern numbered by input frame.</summary>
    sealed class Rig : IDisposable {
        readonly string dir;
        readonly SharedSegmentHolder segment;
        long frame;

        public Rig(
            string dir,
            int latency = 0,
            Ipc.Segments.AudioPlaylistEntry[]? playback = null,
            string? file = null
        ) {
            this.dir = dir;
            Controller = new(new(Rate, Block, latency));
            segment = new(TestAudio.Segment([new(7, [], playback ?? [])], 1));
            Controller.LoadModel(
                TestAudio.Load(
                    segment.Segment,
                    1,
                    true,
                    [TestAudio.Track(7, true, input: 0, armed: true)],
                    file is null ? [] : [new(0, file, 1, Rate, 20 * Rate)],
                    dir
                )
            );
            Driver = new(Controller, Block);
            Take = new(7, Path.Combine(dir, $"7-{Guid.NewGuid():n}.wav"), 2, Rate);
            Controller.Engine.Root.Tracks[0].Take = Take;
        }

        public EngineController Controller { get; }

        public OfflineDriver Driver { get; }

        public RecordTake Take { get; }

        /// <summary>The input frame numbers the blocks were fed, from the first.</summary>
        public long Frame => frame;

        /// <summary>Feeds and runs <paramref name="count" /> blocks; returns the left output.</summary>
        public float[] Blocks(int count) {
            var output = new float[count * Block];
            for (var b = 0; b < count; b++) {
                for (var i = 0; i < Block; i++) {
                    Driver.Input(0)[i] = TestAudio.Sample(frame + i, Seed);
                    Driver.Input(1)[i] = TestAudio.Sample(frame + i, Seed + 1);
                }

                Driver.Process();
                Driver.Output(0).CopyTo(output.AsSpan(b * Block));
                frame += Block;
            }

            return output;
        }

        public RecordedTake Finish() {
            Controller.Engine.Root.Tracks[0].Take = null;
            return Take.Complete(new float[4096]);
        }

        public void Dispose() {
            Take.Dispose();
            Driver.Dispose();
            Controller.Dispose();
            segment.Dispose();
        }
    }

    sealed class SharedSegmentHolder(Ipc.Segments.SharedSegment segment) : IDisposable {
        public Ipc.Segments.SharedSegment Segment => segment;

        public void Dispose() => segment.Dispose();
    }

    /// <summary>Asserts file frames [from, + count) hold input frames [input, + count).</summary>
    static void AssertInput(int[] samples, long from, long input, long count) {
        for (long f = 0; f < count; f++) {
            var expected = TestAudio.Pattern(input + f, Seed);
            if (samples[2 * (from + f)] != expected
                || samples[2 * (from + f) + 1] != TestAudio.Pattern(input + f, Seed + 1)) {
                Assert.Fail($"file frame {from + f}: expected input frame {input + f}");
            }
        }
    }

    [Fact]
    public void The_engine_records_an_armed_tracks_input_while_recording_only() {
        using var rig = new Rig(dir);
        rig.Driver.PlayFrom(9_600);
        rig.Blocks(10); // playing, not recording
        Assert.Equal(0, rig.Take.Pushed);
        rig.Driver.Send(new(EngineCommandKind.Record));
        var recordedFrom = rig.Frame;
        rig.Blocks(50);
        rig.Driver.Send(new(EngineCommandKind.Stop));
        rig.Blocks(5);
        var result = rig.Finish();
        Assert.Equal(9_600 + 10 * 64, result.StartSample);
        Assert.Equal(50 * 64, result.Frames);
        Assert.Null(result.Passes);
        AssertInput(TestAudio.Read24(result.Path, out _), 0, recordedFrom, 50 * 64);
    }

    [Theory]
    [InlineData(5_000, 1_000, 4_000, 0)]
    [InlineData(600, 1_000, 0, 400)]
    [InlineData(0, 0, 0, 0)]
    public void A_take_is_shifted_back_by_the_round_trip_latency(long position, int latency, long start, int skipped) {
        using var rig = new Rig(dir, latency);
        rig.Driver.Send(new(EngineCommandKind.Locate, position));
        rig.Driver.Send(new(EngineCommandKind.Record)); // from a stop
        var recordedFrom = rig.Frame;
        rig.Blocks(2_048 / Block);
        var result = rig.Finish();
        Assert.Equal(start, result.StartSample);
        Assert.Equal(2_048 - skipped, result.Frames);
        AssertInput(TestAudio.Read24(result.Path, out _), 0, recordedFrom + skipped, 2_048 - skipped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(700)]
    public void Cycle_recording_writes_one_file_across_the_wraps_with_its_passes(int latency) {
        using var rig = new Rig(dir, latency);
        // A cycle of 3,000 frames (not a multiple of the block), recording from before it.
        rig.Driver.Send(new(EngineCommandKind.SetCycle, 10_000, 13_000, true));
        rig.Driver.PlayFrom(8_500);
        rig.Blocks(4);
        rig.Driver.Send(new(EngineCommandKind.Record)); // punch in while playing, at 8,756
        var recordedFrom = rig.Frame;
        rig.Blocks(200); // 12,800 frames: 4,244 to the end, then three whole passes and part of a fourth
        var result = rig.Finish();
        var start = 8_756L - latency;
        Assert.Equal(start, result.StartSample);
        Assert.Equal(12_800, result.Frames);
        var first = 13_000 - start;
        List<RecordedPass> expected = [new(start, 0, first)];
        for (var offset = first; offset < 12_800; offset += 3_000) {
            expected.Add(new(10_000, offset, Math.Min(3_000, 12_800 - offset)));
        }

        Assert.Equal(expected, result.Passes!);
        AssertInput(TestAudio.Read24(result.Path, out _), 0, recordedFrom, 12_800);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 300)]
    [InlineData(true, 300)]
    public void Autopunch_records_only_inside_the_range_sample_exactly(bool cycle, int latency) {
        using var rig = new Rig(dir, latency);
        const long punchIn = 10_037, punchOut = 11_501;
        rig.Driver.Send(new(EngineCommandKind.SetPunch, punchIn, punchOut, true));
        if (cycle) {
            rig.Driver.Send(new(EngineCommandKind.SetCycle, 9_000, 12_000, true));
        }

        rig.Driver.Send(new(EngineCommandKind.Locate, 9_000));
        rig.Driver.Send(new(EngineCommandKind.Record));
        var recordedFrom = rig.Frame;
        rig.Blocks(150); // 9,600 frames: past the out point linearly, or three full cycle passes and a part
        var result = rig.Finish();
        Assert.Equal(punchIn, result.StartSample);
        var length = punchOut - punchIn;

        // Input frame k (from the record start) was played at position 9,000 + k − latency (through the cycle).
        long InputAt(long position, int pass) => recordedFrom + (position - 9_000) + pass * 3_000L + latency;
        var samples = TestAudio.Read24(result.Path, out _);
        if (!cycle) {
            Assert.Null(result.Passes);
            Assert.Equal(length, result.Frames);
            AssertInput(samples, 0, InputAt(punchIn, 0), length);
            return;
        }

        var passes = result.Passes!;
        Assert.Equal(Enumerable.Range(0, 3).Select(p => new RecordedPass(punchIn, p * length, length)), passes);
        Assert.Equal(3 * length, result.Frames);
        for (var p = 0; p < 3; p++) {
            AssertInput(samples, p * length, InputAt(punchIn, p), length);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Autopunch_hears_the_input_inside_the_range_and_plays_back_outside(bool cycle) {
        var file = TestAudio.Write24(dir, "playback.wav", 1, 20 * Rate, 5);
        using var rig = new Rig(dir, 0, [TestAudio.Region(0, 20 * Rate, 0)], file);
        const long punchIn = 10_037, punchOut = 11_501;
        rig.Driver.Send(new(EngineCommandKind.SetPunch, punchIn, punchOut, true));
        var c = new Cycle(cycle, 9_000, 12_000);
        rig.Driver.Send(new(EngineCommandKind.SetCycle, c.Start, c.End, c.Enabled));
        rig.Driver.PlayFrom(9_000);
        var before = rig.Blocks(2); // playing: playback only (the track does not monitor)
        rig.Driver.Send(new(EngineCommandKind.Record));
        var recordedFrom = rig.Frame;
        var output = rig.Blocks(150);
        var (pan, _) = Strip.PanGains(0f, true);
        var (master, _) = Strip.PanGains(0f, true);
        for (var f = 0; f < before.Length; f++) {
            Assert.Equal(TestAudio.Sample(9_000 + f, 5) * pan * master, before[f]);
        }

        var position = 9_000L + before.Length;
        for (var f = 0; f < output.Length; f++) {
            var inside = position is >= punchIn and < punchOut;
            var source = inside ? TestAudio.Sample(recordedFrom + f, Seed) : TestAudio.Sample(position, 5);
            var expected = source * pan * master;
            if (output[f] != expected) {
                Assert.Fail($"frame {f} at {position}: expected {(inside ? "input" : "playback")}");
            }

            position = c.Advance(position, 1);
        }
    }

    [Fact]
    public void A_punch_range_the_recording_never_reaches_keeps_nothing() {
        using var rig = new Rig(dir);
        rig.Driver.Send(new(EngineCommandKind.SetPunch, 50_000, 60_000, true));
        rig.Driver.Send(new(EngineCommandKind.Locate, 0));
        rig.Driver.Send(new(EngineCommandKind.Record));
        rig.Blocks(20);
        Assert.Equal(0, rig.Finish().Frames);
    }
}
