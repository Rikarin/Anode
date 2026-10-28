using Anode.Contracts;

namespace Anode.Model.Audio;

/// <summary>What a bounce's file is: format, bit depth (16, 24, or 32 for float), rate, dither and normalisation.</summary>
public sealed record BounceFormat(
    AudioFormat Format,
    int BitDepth,
    int SampleRate,
    DitherMode Dither,
    NormalizeMode Normalize,
    double LoudnessLufs,
    double PeakCeilingDb
) {
    /// <summary>The file extension of <see cref="Format" />.</summary>
    public string Extension => Format == AudioFormat.Aiff ? ".aif" : ".wav";
}

/// <summary>
///     A bounce's post-processing (docs/05-phase-4.md, "Bounce"): the host's 32-bit float render is converted to the
///     file's rate (<see cref="SampleRateConverter" />), normalised — peak: to the ceiling on the 4× oversampled true
///     peak; loudness: to the target integrated loudness (EBU R128 gating), then limited to the ceiling
///     (<see cref="Limiter" />) — then dithered to its word length (below 32 bits) and written as WAV or AIFF.
/// </summary>
public static class BounceProcessor {
    /// <summary>A WAV file as planar floats, and its rate.</summary>
    public static (float[][] Channels, int SampleRate) Read(string path) {
        using var reader = WavReader.Open(path);
        var info = reader.Info;
        var channels = new float[info.Channels][];
        for (var c = 0; c < channels.Length; c++) {
            channels[c] = new float[info.Frames];
        }

        var block = new float[4096 * info.Channels];
        long at = 0;
        int frames;
        while ((frames = reader.Read(block)) > 0) {
            for (var i = 0; i < frames; i++) {
                for (var c = 0; c < channels.Length; c++) {
                    channels[c][at + i] = block[i * channels.Length + c];
                }
            }

            at += frames;
        }

        return (channels, info.SampleRate);
    }

    /// <summary>Rate conversion, then normalisation: the samples the file will hold, before dither.</summary>
    public static float[][] Process(float[][] channels, int fromRate, BounceFormat format) {
        var audio = fromRate == format.SampleRate
            ? [..channels.Select(static c => (float[])c.Clone())]
            : SampleRateConverter.Convert(channels, fromRate, format.SampleRate);
        Normalise(audio, format.SampleRate, format.Normalize, format.LoudnessLufs, format.PeakCeilingDb);
        return audio;
    }

    /// <summary>Normalises in place: peak to the ceiling (true peak), or loudness to the target then limited to the ceiling.</summary>
    public static void Normalise(float[][] audio, int sampleRate, NormalizeMode mode, double lufs, double ceilingDb) {
        switch (mode) {
            case NormalizeMode.Peak:
                var peak = TruePeak.Measure(audio);
                if (peak > 0) {
                    Scale(audio, Math.Pow(10, ceilingDb / 20) / peak);
                }

                break;
            case NormalizeMode.Loudness:
                var loudness = Loudness.Integrated(audio, sampleRate);
                if (double.IsFinite(loudness)) {
                    Scale(audio, Math.Pow(10, (lufs - loudness) / 20));
                    Limiter.Apply(audio, sampleRate, ceilingDb);
                }

                break;
        }
    }

    /// <summary>Writes the file: dithered integers at 16 or 24 bits, floats at 32.</summary>
    public static void Write(string path, float[][] audio, BounceFormat format, int seed = 0) {
        if (format.BitDepth == 32) {
            AudioFileWriter.WriteFloat(path, format.Format, format.SampleRate, audio);
        } else {
            AudioFileWriter.WriteInteger(
                path,
                format.Format,
                format.SampleRate,
                format.BitDepth,
                Dither.Quantize(audio, format.BitDepth, format.Dither, seed)
            );
        }
    }

    static void Scale(float[][] audio, double gain) {
        var g = (float)gain;
        foreach (var channel in audio) {
            for (var i = 0; i < channel.Length; i++) {
                channel[i] *= g;
            }
        }
    }
}
