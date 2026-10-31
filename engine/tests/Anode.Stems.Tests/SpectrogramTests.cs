namespace Anode.Stems.Tests;

/// <summary>HTDemucs' STFT and iSTFT in C# (docs/adr/0010-stem-splitter.md).</summary>
public sealed class SpectrogramTests {
    const int Length = 343980;

    [Fact]
    public void The_round_trip_reconstructs_the_segment_inside_its_end_frames() {
        var spectrogram = new Spectrogram(Length);
        var signal = Signals.Noise(Length, 1)[0];
        var spectrum = new float[spectrogram.SpectrumLength];
        spectrogram.Forward(signal, 0, spectrum);
        var back = new float[Length];
        spectrogram.InverseAdd(spectrum, 0, back);

        // Every sample whose frames are all kept and none reaches into the reflected padding (whose kink puts energy
        // into the dropped Nyquist bin): from 4608 (frame 2 on) to 4096 before the end.
        Assert.True(Signals.MaxDifference(signal, back, 4608, Length - Spectrogram.Size) < 1e-5);
    }

    [Fact]
    public void Like_HTDemucs_the_ends_keep_only_the_frames_inside_the_segment() {
        // HTDemucs drops the frames that would complete the first and last 1536 samples, so the first sample comes
        // back at (w[1536]² + w[512]²) / 1.5 = 0.5 of itself.
        var spectrogram = new Spectrogram(Length);
        var signal = Signals.Sines(Length, 1, (440, 0.5))[0];
        var spectrum = new float[spectrogram.SpectrumLength];
        spectrogram.Forward(signal, 0, spectrum);
        var back = new float[Length];
        spectrogram.InverseAdd(spectrum, 0, back);
        Assert.Equal(0.5, back[0] / signal[0], 1e-3);
    }

    [Fact]
    public void The_layout_is_complex_as_channels() {
        var spectrogram = new Spectrogram(Length);
        var spectrum = new float[spectrogram.SpectrumLength];
        var left = Signals.Sines(Length, 1, (1000, 0.5))[0];
        spectrogram.Forward(left, 0, spectrum);
        spectrogram.Forward(new float[Length], 1, spectrum);
        var frames = spectrogram.Frames;
        var half = spectrum.Length / 2;

        // Channel 0's real and imaginary planes hold the tone near bin 1000 · 4096 / 44100 ≈ 93; channel 1 is silent.
        var bin = (int)Math.Round(1000.0 * 4096 / 44100);
        var t = frames / 2;
        var magnitude = Math.Sqrt(
            Math.Pow(spectrum[bin * frames + t], 2) + Math.Pow(spectrum[(2048 + bin) * frames + t], 2)
        );
        Assert.True(magnitude > 1, $"magnitude {magnitude}");
        Assert.All(spectrum.AsSpan(half).ToArray(), static v => Assert.Equal(0, v));
    }
}
