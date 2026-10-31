using Anode.Contracts;

namespace Anode.Stems.Tests;

/// <summary>
///     Chunking with overlap-add, rates, channels, progress and cancellation (docs/09-phase-8.md, workstream E), on a
///     deterministic stand-in network and on the fixture model.
/// </summary>
public sealed class SeparationTests {
    const int Length = 343980;
    const int Stride = 257985; // ⌊0.75 · L⌋
    const int Shift = 11025;

    static readonly StemKind[] Four = [StemKind.Drums, StemKind.Bass, StemKind.Other, StemKind.Vocals];

    /// <summary>
    ///     A segment network that depends on its whole segment: each stem is the input times a gain that grows with the
    ///     segment's RMS, half through each branch. Any misplaced chunk shows.
    /// </summary>
    sealed class StandIn : ISegmentModel {
        public IReadOnlyList<StemKind> Stems => Four;

        public int SampleRate => 44100;

        public int SegmentLength => Length;

        public int Runs { get; private set; }

        public Action? OnRun { get; init; }

        public void Run(float[] mix, float[] spectrum, float[] spectrumOut, float[] waveOut, CancellationToken token) {
            Runs++;
            OnRun?.Invoke();
            var energy = 0.0;
            foreach (var v in mix) {
                energy += v * v;
            }

            var rms = (float)Math.Sqrt(energy / mix.Length);
            for (var s = 0; s < Stems.Count; s++) {
                var gain = (s + 1) * 0.1f * (1 + rms);
                for (var i = 0; i < spectrum.Length; i++) {
                    spectrumOut[s * spectrum.Length + i] = 0.5f * gain * spectrum[i];
                }

                for (var i = 0; i < mix.Length; i++) {
                    waveOut[s * mix.Length + i] = 0.5f * gain * mix[i];
                }
            }
        }

        public void Dispose() { }
    }

    static IStemSeparator Fixture() =>
        StemSeparators.Open(StemModel.FourStems, Signals.Fixture("htdemucs-fixture.onnx"));

    [Fact]
    public void Chunk_boundaries_leave_no_seams_a_long_split_equals_the_split_of_its_parts() {
        using var separator = new HtDemucsSeparator(StemModel.FourStems, new StandIn());
        var frames = 5 * Length;
        var input = Signals.Noise(frames, 2);
        var whole = separator.Separate(input, 44100, Four, null, CancellationToken.None);

        // A part from the start, and one from a chunk boundary of the whole (so their chunk grids line up). Away from
        // the parts' outer edges (where the whole had more context), every sample is the same.
        var head = 3 * Length;
        var first = separator.Separate(Signals.Slice(input, 0, head), 44100, Four, null, CancellationToken.None);
        var from = 2 * Stride;
        var second = separator.Separate(
            Signals.Slice(input, from, frames - from),
            44100,
            Four,
            null,
            CancellationToken.None
        );
        foreach (var stem in Four) {
            for (var c = 0; c < 2; c++) {
                var a = whole[stem][c];
                Assert.True(Signals.MaxDifference(a, first[stem][c], 0, head - Length - Stride) < 1e-4);
                var tail = a.AsSpan(from).ToArray();
                var start = Length + Stride - Shift;
                Assert.True(Signals.MaxDifference(tail, second[stem][c], start, tail.Length) < 1e-4);
            }
        }
    }

    [Fact]
    public void The_fixture_model_splits_by_frequency_and_the_stems_add_up_to_the_mix() {
        using var separator = Fixture();
        Assert.Equal(Four, separator.Stems);
        Assert.Equal(44100, separator.SampleRate);
        var frames = 20 * 44100;
        var bass = Signals.Sines(frames, 2, (80, 0.3));
        var vocals = Signals.Sines(frames, 2, (700, 0.2));
        var other = Signals.Sines(frames, 2, (3000, 0.1));
        var drums = Signals.Sines(frames, 2, (9000, 0.05));
        var mix = new float[2][];
        for (var c = 0; c < 2; c++) {
            mix[c] = new float[frames];
            for (var n = 0; n < frames; n++) {
                mix[c][n] = bass[c][n] + vocals[c][n] + other[c][n] + drums[c][n];
            }
        }

        var reports = new List<double>();
        var stems = separator.Separate(mix, 44100, Four, new Inline(reports.Add), CancellationToken.None);
        Assert.True(Signals.Sdr(bass, stems[StemKind.Bass]) > 30);
        Assert.True(Signals.Sdr(vocals, stems[StemKind.Vocals]) > 30);
        Assert.True(Signals.Sdr(other, stems[StemKind.Other]) > 30);
        Assert.True(Signals.Sdr(drums, stems[StemKind.Drums]) > 30);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public void Mono_and_other_rates_come_back_as_they_went_in() {
        using var separator = Fixture();
        var frames = 3 * 48000;
        var input = Signals.Sines(frames, 1, (80, 0.3), (3000, 0.1));
        var stems = separator.Separate(input, 48000, [StemKind.Bass, StemKind.Other], null, CancellationToken.None);
        Assert.Equal(2, stems.Count);
        var bass = stems[StemKind.Bass];
        Assert.Single(bass);
        Assert.Equal(frames, bass[0].Length);

        // 80 Hz at 48 kHz in the bass, away from the ends.
        var expected = Signals.Sines(frames, 1, (80, 0.3))[0];
        Assert.True(Signals.MaxDifference(expected, bass[0], 4800, frames - 4800) < 0.02);
    }

    [Fact]
    public void Silence_and_empty_input_separate_to_silence() {
        using var separator = Fixture();
        var empty = separator.Separate([[], []], 44100, Four, null, CancellationToken.None);
        Assert.All(Four, s => Assert.Empty(empty[s][0]));
        var silent = separator.Separate([new float[1000]], 44100, Four, null, CancellationToken.None);
        Assert.All(Four, s => Assert.All(silent[s][0], static v => Assert.Equal(0, v)));
    }

    [Fact]
    public void Cancellation_stops_between_chunks() {
        using var cancel = new CancellationTokenSource();
        var standIn = new StandIn { OnRun = cancel.Cancel };
        using var separator = new HtDemucsSeparator(StemModel.FourStems, standIn);
        var input = Signals.Noise(4 * Length, 2);
        Assert.Throws<OperationCanceledException>(() => separator.Separate(input, 44100, Four, null, cancel.Token));
        Assert.Equal(1, standIn.Runs);
    }

    [Fact]
    public void A_stem_the_model_does_not_make_is_refused() {
        using var separator = Fixture();
        Assert.Throws<ArgumentException>(() =>
            separator.Separate([new float[10]], 44100, [StemKind.Guitar], null, CancellationToken.None)
        );
    }

    [Fact]
    public void A_missing_damaged_or_mismatched_model_is_invalid_data() {
        Assert.Throws<InvalidDataException>(static () => StemSeparators.Open(
                StemModel.FourStems,
                "/nonexistent/model.onnx"
            )
        );
        var damaged = Path.GetTempFileName();
        try {
            File.WriteAllBytes(damaged, [1, 2, 3, 4]);
            Assert.Throws<InvalidDataException>(() => StemSeparators.Open(StemModel.FourStems, damaged));
        } finally {
            File.Delete(damaged);
        }

        Assert.Throws<InvalidDataException>(static () =>
            StemSeparators.Open(StemModel.FourStems, Signals.Fixture("htdemucs_6s-fixture.onnx"))
        );
        using var six = StemSeparators.Open(StemModel.SixStems, Signals.Fixture("htdemucs_6s-fixture.onnx"));
        Assert.Equal(StemSeparators.StemsOf(StemModel.SixStems), six.Stems);
    }

    /// <summary>Progress reported on the calling thread (<see cref="Progress{T}" /> posts to the pool).</summary>
    sealed class Inline(Action<double> report) : IProgress<double> {
        public void Report(double value) => report(value);
    }
}
