using Anode.Contracts;
using Anode.Dsp.Resampling;
using Anode.Dsp.Spatial;
using SpeakerLayout = Anode.Dsp.Spatial.SpeakerLayout;

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
    /// <summary>A WAV file as planar floats, and its rate (see <see cref="ReadLayout" /> for the channels' order).</summary>
    public static (float[][] Channels, int SampleRate) Read(string path) {
        var (channels, rate, _) = ReadLayout(path);
        return (channels, rate);
    }

    /// <summary>
    ///     A WAV file as planar floats, its rate, and the speaker layout of a <c>WAVE_FORMAT_EXTENSIBLE</c> file whose
    ///     mask is one of Anode's layouts (a surround render, docs/11-phase-10.md): its channels are then returned in the
    ///     layout's (ADM) order, not the file's (the mask's, 7.x's rears before its sides). Any other file's channels
    ///     come in file order, with no layout.
    /// </summary>
    public static (float[][] Channels, int SampleRate, SpeakerLayout? Layout) ReadLayout(string path) {
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

        var layout = WaveChannels.LayoutOf(info.ChannelMask, info.Channels);
        if (layout is not null) {
            // File channel k holds layout channel order[k].
            var order = WaveChannels.Of(layout).Order;
            var planar = new float[channels.Length][];
            for (var k = 0; k < order.Length; k++) {
                planar[order[k]] = channels[k];
            }

            channels = planar;
        }

        return (channels, info.SampleRate, layout);
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
            case NormalizeMode.Off:
                break;
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
    /// <summary>
    ///     Writes the processed samples; a WAV of a surround <paramref name="layout" /> (channels in its ADM order) is
    ///     <c>WAVE_FORMAT_EXTENSIBLE</c> with the layout's mask (<see cref="AudioFileWriter" />).
    /// </summary>
    public static void Write(
        string path,
        float[][] audio,
        BounceFormat format,
        int seed = 0,
        SpeakerLayout? layout = null
    ) {
        if (format.BitDepth == 32) {
            AudioFileWriter.WriteFloat(path, format.Format, format.SampleRate, audio, layout);
        } else {
            AudioFileWriter.WriteInteger(
                path,
                format.Format,
                format.SampleRate,
                format.BitDepth,
                Dither.Quantize(audio, format.BitDepth, format.Dither, seed),
                layout
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
