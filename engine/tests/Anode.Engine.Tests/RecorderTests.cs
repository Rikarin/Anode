using Anode.Engine.Recording;

namespace Anode.Engine.Tests;

public sealed unsafe class RecorderTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void A_take_writes_exactly_what_it_was_fed_as_24_bit_wav() {
        var path = Path.Combine(dir, "1-take.wav");
        using var take = new RecordTake(1, path, 2, Rate, 0);
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

    [Theory]
    [InlineData(5_000, 1_000, 4_000, 0)]
    [InlineData(600, 1_000, 0, 400)]
    [InlineData(0, 0, 0, 0)]
    public void A_take_is_shifted_back_by_the_round_trip_latency(long position, int latency, long start, int skipped) {
        var path = Path.Combine(dir, $"2-{position}.wav");
        using var take = new RecordTake(2, path, 1, Rate, latency);
        var input = Enumerable.Range(0, 2_000).Select(static i => TestAudio.Sample(i, 9)).ToArray();
        fixed (float* p = input) {
            take.Push(p, null, input.Length, position);
        }

        var result = take.Complete(new float[4096]);
        Assert.Equal(start, result.StartSample);
        Assert.Equal(2_000 - skipped, result.Frames);
        var samples = TestAudio.Read24(path, out _);
        Assert.Equal(TestAudio.Pattern(skipped, 9), samples[0]);
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

    [Fact]
    public void The_engine_records_an_armed_tracks_input_while_recording_only() {
        using var controller = new EngineController(new(Rate, 64));
        using var segment = TestAudio.Segment([new(7, [], [])], 1);
        controller.LoadModel(
            TestAudio.Load(segment, 1, true, [TestAudio.Track(7, true, input: 0, armed: true)], [], dir)
        );
        using var driver = new OfflineDriver(controller, 64);
        var track = controller.Engine.Root.Tracks[0];
        var path = Path.Combine(dir, "7-engine.wav");
        using var take = new RecordTake(7, path, 2, Rate, 0);
        track.Take = take;
        long frame = 0;

        void Blocks(int count) {
            for (var b = 0; b < count; b++) {
                for (var i = 0; i < 64; i++) {
                    driver.Input(0)[i] = TestAudio.Sample(frame + i, 20);
                    driver.Input(1)[i] = TestAudio.Sample(frame + i, 21);
                }

                driver.Process();
                frame += 64;
            }
        }

        driver.PlayFrom(9_600);
        Blocks(10); // playing, not recording
        Assert.Equal(0, take.Pushed);
        driver.Send(new(EngineCommandKind.Record));
        var recordedFrom = frame;
        Blocks(50);
        driver.Send(new(EngineCommandKind.Stop));
        Blocks(5);
        track.Take = null;
        var result = take.Complete(new float[4096]);
        Assert.Equal(9_600 + 10 * 64, result.StartSample);
        Assert.Equal(50 * 64, result.Frames);
        var samples = TestAudio.Read24(path, out _);
        for (var f = 0; f < 50 * 64; f++) {
            Assert.Equal(TestAudio.Pattern(recordedFrom + f, 20), samples[2 * f]);
            Assert.Equal(TestAudio.Pattern(recordedFrom + f, 21), samples[2 * f + 1]);
        }
    }
}
