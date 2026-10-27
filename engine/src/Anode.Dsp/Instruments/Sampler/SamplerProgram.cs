using Anode.Dsp.IO;

namespace Anode.Dsp.Instruments.Sampler;

/// <summary>
///     A sample in memory, each channel padded with a frame of silence before and <see cref="PadAfter" /> after it so
///     the cubic interpolation can read past either end. A mono sample's right channel is its left.
/// </summary>
internal sealed class SampleBuffer {
    public const int PadBefore = 1;
    public const int PadAfter = 3;

    public SampleBuffer(WavData wav) {
        Frames = wav.Frames;
        SampleRate = wav.SampleRate;
        Stereo = wav.Channels.Length > 1;
        Left = Pad(wav.Channels[0]);
        Right = Stereo ? Pad(wav.Channels[1]) : Left;
        RootNote = wav.RootNote;
        RootCents = wav.RootCents;
        LoopStart = wav.LoopStart;
        LoopEnd = wav.LoopEnd;
    }

    public float[] Left { get; }
    public float[] Right { get; }
    public bool Stereo { get; }
    public int Frames { get; }
    public int SampleRate { get; }

    /// <summary>The <c>smpl</c> chunk's unity note and its fine tune, or −1 and 0.</summary>
    public int RootNote { get; }

    public double RootCents { get; }

    /// <summary>The <c>smpl</c> chunk's first loop (inclusive), or −1.</summary>
    public long LoopStart { get; }

    public long LoopEnd { get; }

    static float[] Pad(float[] samples) {
        var padded = new float[samples.Length + PadBefore + PadAfter];
        samples.CopyTo(padded, PadBefore);
        return padded;
    }
}

/// <summary>A region ready to play: its sample, its resolved loop, and its level and pitch offsets.</summary>
internal sealed class SamplerZone {
    public SamplerZone(SfzRegion region, SampleBuffer sample) {
        Region = region;
        Sample = sample;
        var start = region.LoopStart >= 0 ? region.LoopStart : sample.LoopStart;
        var end = region.LoopEnd >= 0 ? region.LoopEnd : sample.LoopEnd;
        var valid = start >= 0 && end > start && end < sample.Frames;
        Mode = region.LoopMode ?? (valid ? LoopMode.Continuous : LoopMode.NoLoop);
        if (!valid && Mode is LoopMode.Continuous or LoopMode.Sustain) {
            Mode = LoopMode.NoLoop; // a loop mode without a usable loop plays the sample through
        }

        LoopStart = valid ? start : 0;
        LoopEnd = valid ? end : sample.Frames - 1;
        Semitones = region.Transpose + region.Tune / 100 - region.KeyCenter;
        Gain = MathF.Pow(10, (float)region.Volume / 20);
        var pan = region.Pan / 100;
        if (sample.Stereo) {
            // A stereo sample's pan is a balance.
            GainLeft = (float)Math.Min(1, 1 - pan);
            GainRight = (float)Math.Min(1, 1 + pan);
        } else {
            var angle = (pan + 1) * Math.PI / 4;
            GainLeft = (float)(Math.Sqrt(2) * Math.Cos(angle));
            GainRight = (float)(Math.Sqrt(2) * Math.Sin(angle));
        }
    }

    public SfzRegion Region { get; }
    public SampleBuffer Sample { get; }
    public LoopMode Mode { get; }
    public long LoopStart { get; }
    public long LoopEnd { get; }

    /// <summary>The pitch offset from the played note: transpose and tune less the key centre.</summary>
    public double Semitones { get; }

    public float Gain { get; }
    public float GainLeft { get; }
    public float GainRight { get; }
}

/// <summary>What a sampler plays: its zones, from an SFZ file or a single WAV.</summary>
internal sealed class SamplerProgram {
    /// <summary>The root of a WAV without a <c>smpl</c> chunk: middle C (C3 in Logic's naming, c4 in SFZ's).</summary>
    public const int DefaultRoot = 60;

    /// <summary>The release of the region a single WAV gets, in seconds.</summary>
    public const double SingleSampleRelease = 0.1;

    SamplerProgram(string path, SamplerZone[] zones) {
        Path = path;
        Zones = zones;
    }

    public string Path { get; }
    public SamplerZone[] Zones { get; }

    /// <summary>
    ///     Loads a single WAV, or an SFZ (every sample it names into memory; regions whose sample cannot be read are
    ///     left out).
    /// </summary>
    public static SamplerProgram Load(string path) {
        var full = System.IO.Path.GetFullPath(path);
        if (!IsWav(full)) {
            var samples = new Dictionary<string, SampleBuffer?>(StringComparer.Ordinal);
            var zones = new List<SamplerZone>();
            foreach (var region in SfzParser.ParseFile(full)) {
                if (!samples.TryGetValue(region.Sample, out var sample)) {
                    sample = TryLoad(region.Sample);
                    samples[region.Sample] = sample;
                }

                if (sample is not null) {
                    zones.Add(new(region, sample));
                }
            }

            return zones.Count == 0
                ? throw new InvalidDataException($"No playable region in {full}.")
                : new(full, [..zones]);
        }

        var wav = new SampleBuffer(WavReader.Read(full));
        var single = new SfzRegion {
            Sample = full,
            KeyCenter = wav.RootNote >= 0 ? wav.RootNote : DefaultRoot,
            Tune = -wav.RootCents,
            LoVel = 0,
            Release = SingleSampleRelease
        };
        return new(full, [new(single, wav)]);
    }

    /// <summary>Whether the file is RIFF/WAVE (by its content, whatever its name); anything else is read as SFZ.</summary>
    static bool IsWav(string path) {
        Span<byte> header = stackalloc byte[12];
        using var file = File.OpenHandle(path);
        return RandomAccess.Read(file, header, 0) == 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..].SequenceEqual("WAVE"u8);
    }

    static SampleBuffer? TryLoad(string path) {
        try {
            return new(WavReader.Read(path));
        } catch (IOException) {
            return null;
        } catch (InvalidDataException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }
}
