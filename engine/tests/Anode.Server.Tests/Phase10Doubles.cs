using Anode.Contracts;
using Anode.Interchange.Adm;
using Anode.Media.Ffmpeg;
using Anode.Model.Audio;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Anode.Server.Remote;
using System.Collections.Concurrent;

namespace Anode.Server.Tests;

/// <summary>
///     A stand-in for workstream E's FFmpeg operations: a probe it is told, scene cuts it is told, thumbnails as small
///     files, audio extraction as a WAV of a click every second (so where they land can be checked), and a remux that
///     records what it was given and copies the movie. Counts its calls.
/// </summary>
sealed class FakeMovieMedia : IMovieMedia {
    public bool Available { get; set; } = true;

    public MovieProbe Probe { get; set; } = new(
        "h264",
        640,
        360,
        new(25, 1),
        250,
        3600,
        10,
        new("aac", 2, 48_000)
    );

    public IReadOnlyList<long> Cuts { get; set; } = [50, 125];

    /// <summary>The channels the extracted audio has.</summary>
    public int AudioChannels { get; set; } = 2;

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The WAV the last remux was given, read back, and its other arguments.</summary>
    public (float[][] Audio, int Rate, bool Replace, MovieAudioCodec Codec, string Output)? Remuxed { get; private set; }

    public Task<MovieProbe> ProbeAsync(string moviePath, CancellationToken cancellation) {
        Calls.Enqueue($"probe {Path.GetFileName(moviePath)}");
        return Task.FromResult(Probe);
    }

    /// <summary>The extracted audio: silence with a full-scale click at the start of every second.</summary>
    public Task ExtractAudioAsync(
        string moviePath,
        string wavPath,
        int sampleRate,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) {
        Calls.Enqueue($"extract {sampleRate}");
        var frames = (long)(Probe.DurationSeconds * sampleRate);
        using var writer = new WavWriter(wavPath, AudioChannels, sampleRate, WavEncoding.Float32);
        var samples = new float[frames * AudioChannels];
        for (long s = 0; s < frames; s += sampleRate) {
            for (var c = 0; c < AudioChannels; c++) {
                samples[s * AudioChannels + c] = 1;
            }
        }

        writer.Write(samples);
        progress?.Report(1);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ThumbnailsAsync(
        string moviePath,
        IReadOnlyList<long> frames,
        int height,
        string folder,
        CancellationToken cancellation
    ) {
        Calls.Enqueue($"thumbnails {string.Join(',', frames)} {height}");
        Directory.CreateDirectory(folder);
        IReadOnlyList<string> paths = [..frames.Select(f => Path.Combine(folder, $"{f}.jpg"))];
        foreach (var path in paths) {
            File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        }

        return Task.FromResult(paths);
    }

    public Task<IReadOnlyList<long>> DetectScenesAsync(
        string moviePath,
        double threshold,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) {
        Calls.Enqueue(FormattableString.Invariant($"scenes {threshold}"));
        return Task.FromResult(Cuts);
    }

    public Task RemuxAsync(
        string moviePath,
        string wavPath,
        string outputPath,
        bool replace,
        MovieAudioCodec codec,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) {
        Calls.Enqueue($"remux {Path.GetFileName(outputPath)}");
        var (audio, rate) = BounceProcessor.Read(wavPath);
        Remuxed = (audio, rate, replace, codec, outputPath);
        File.Copy(moviePath, outputPath, true);
        return Task.CompletedTask;
    }
}

/// <summary>
///     A stand-in for workstream D's ADM BWF writer and reader: the writer keeps the scene and its sources' audio; the
///     reader answers the scene it is given, its audio copied into the media folder. The down-mix is BS.775's 5.1
///     matrix (L + 0.707 C + 0.707 Ls), or any format's first two channels.
/// </summary>
sealed class FakeSpatialFiles : ISpatialFiles {
    public AdmScene? Written { get; private set; }

    public Dictionary<string, float[][]> WrittenAudio { get; } = new(StringComparer.Ordinal);

    public int WrittenBitDepth { get; private set; }

    public AdmScene? ToRead { get; set; }

    public void WriteAdm(AdmScene scene, string path, int bitDepth) {
        Written = scene;
        WrittenBitDepth = bitDepth;
        foreach (var source in scene.Beds.Select(static b => b.AudioPath).Concat(scene.Objects.Select(static o => o.AudioPath))) {
            WrittenAudio[Path.GetFileName(source)] = BounceProcessor.Read(source).Channels;
        }

        File.WriteAllBytes(path, "RIFF"u8.ToArray());
    }

    public AdmScene ReadAdm(string path, string mediaFolder) {
        var scene = ToRead ?? throw new FormatException("No chna chunk.");
        string Copy(string source) {
            var target = Path.Combine(mediaFolder, Path.GetFileName(source));
            File.Copy(source, target, true);
            return target;
        }

        return scene with {
            Beds = [..scene.Beds.Select(b => b with { AudioPath = Copy(b.AudioPath) })],
            Objects = [..scene.Objects.Select(o => o with { AudioPath = Copy(o.AudioPath) })]
        };
    }

    public float[,] StereoDownmix(ChannelFormat from) {
        var count = SpeakerLayouts.ChannelCount(from);
        var gains = new float[2, count];
        gains[0, 0] = 1;
        gains[1, 1] = 1;
        if (from == ChannelFormat.Surround51) {
            gains[0, 2] = gains[1, 2] = 0.7071f;
            gains[0, 4] = gains[1, 5] = 0.7071f;
        }

        return gains;
    }
}

/// <summary>
///     A stand-in for workstream E's listen-in framer: every 960 frames of tap audio (at any rate) is a "frame" whose
///     packet is the first sample's value as a float and the run's flags, behind the real header; counts encoders made
///     and disposed.
/// </summary>
sealed class FakeEncoders : IListenInEncoders {
    int created;
    int disposed;

    public int Created => Volatile.Read(ref created);

    public int Disposed => Volatile.Read(ref disposed);

    public IListenInEncoder Create(int projectSampleRate) {
        Interlocked.Increment(ref created);
        return new Encoder(this, projectSampleRate);
    }

    sealed class Encoder(FakeEncoders owner, int rate) : IListenInEncoder {
        readonly List<float> pending = [];
        long transport = -1;
        long pendingStart = -1;
        bool playing;
        bool discontinuity;
        uint sequence;

        public ListenInFormat Format { get; } = new(
            ListenIn.SampleRate,
            ListenIn.Channels,
            ListenIn.FrameSamples,
            ListenIn.Bitrate,
            rate
        );

        public void Write(ReadOnlySpan<float> interleaved, long transportSample, bool isPlaying, bool isDiscontinuity) {
            if (pending.Count == 0) {
                pendingStart = transportSample;
            }

            discontinuity |= isDiscontinuity;
            playing = isPlaying;
            transport = transportSample;
            pending.AddRange(interleaved.ToArray());
        }

        public int Read(Span<byte> frame) {
            if (pending.Count < ListenIn.FrameSamples * 2) {
                return 0;
            }

            var packet = BitConverter.GetBytes(pending[0]);
            var flags = (byte)((playing ? ListenIn.FlagPlaying : 0) | (discontinuity ? ListenIn.FlagDiscontinuity : 0));
            ListenIn.WriteHeader(frame, sequence++, pendingStart, packet.Length, flags);
            packet.CopyTo(frame[ListenIn.HeaderBytes..]);
            pending.RemoveRange(0, ListenIn.FrameSamples * 2);
            pendingStart = pendingStart < 0 ? -1 : pendingStart + ListenIn.FrameSamples;
            discontinuity = false;
            _ = transport;
            return ListenIn.HeaderBytes + packet.Length;
        }

        public void Dispose() => Interlocked.Increment(ref owner.disposed);
    }
}

/// <summary>A master tap ring in process memory the test writes, as the host's audio callback would.</summary>
sealed class FakeTap : IMasterTapSource, IDisposable {
    public MasterTapWriter Writer { get; } = MasterTapWriter.InMemory(48_000, 1 << 14, 1 << 8);

    public string Key => "fake";

    public MasterTapReader Open() => MasterTapReader.Over(Writer.Region);

    public void Dispose() => Writer.Dispose();
}
