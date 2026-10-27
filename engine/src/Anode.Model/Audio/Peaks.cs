using Anode.Contracts;
using Anode.Model.Storage;
using System.Buffers.Binary;

namespace Anode.Model.Audio;

/// <summary>
///     Waveform peaks (<see cref="PeakFormat" />): built once per audio file at every level of
///     <see cref="PeakFormat.Levels" /> into the package's <c>Peaks/</c> folder, one file per level in the very format
///     the API serves (<c>&lt;fileId&gt;.&lt;framesPerPeak&gt;.anpk</c>), and sliced from there.
/// </summary>
public static class Peaks {
    public const string Extension = ".anpk";

    /// <summary>The default resolution when a request names none.</summary>
    public const int DefaultFramesPerPeak = 1024;

    public static string PathOf(string peaksFolder, string fileId, int framesPerPeak) =>
        Path.Combine(peaksFolder, $"{fileId}.{framesPerPeak}{Extension}");

    /// <summary>Whether every level of <paramref name="fileId" /> is there.</summary>
    public static bool Exist(string peaksFolder, string fileId) {
        foreach (var level in PeakFormat.Levels) {
            if (!File.Exists(PathOf(peaksFolder, fileId, level))) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The kept resolution nearest to <paramref name="framesPerPeak" /> (on a log scale).</summary>
    public static int NearestLevel(int? framesPerPeak) {
        var wanted = framesPerPeak is > 0 and var f ? f : DefaultFramesPerPeak;
        var best = PeakFormat.Levels[0];
        foreach (var level in PeakFormat.Levels) {
            if (Math.Abs(Math.Log((double)level / wanted)) < Math.Abs(Math.Log((double)best / wanted))) {
                best = level;
            }
        }

        return best;
    }

    /// <summary>Reads <paramref name="wavPath" /> once and writes every level's file (atomically).</summary>
    public static void Build(string wavPath, string peaksFolder, string fileId, CancellationToken cancellationToken = default) {
        Directory.CreateDirectory(peaksFolder);
        using var reader = WavReader.Open(wavPath);
        var channels = reader.Info.Channels;
        var levels = PeakFormat.Levels;
        var finest = levels[0];
        var finestPeaks = (reader.Info.Frames + finest - 1) / finest;
        var data = new short[finestPeaks * channels * 2];
        var buffer = new float[finest * channels * 64];
        var min = new float[channels];
        var max = new float[channels];
        long peak = 0, inPeak = 0;
        int frames;
        while ((frames = reader.Read(buffer)) > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < frames; i++) {
                if (inPeak == 0) {
                    Array.Fill(min, float.MaxValue);
                    Array.Fill(max, float.MinValue);
                }

                for (var c = 0; c < channels; c++) {
                    var x = buffer[i * channels + c];
                    min[c] = Math.Min(min[c], x);
                    max[c] = Math.Max(max[c], x);
                }

                if (++inPeak == finest) {
                    Store(data, peak++, min, max);
                    inPeak = 0;
                }
            }
        }

        if (inPeak > 0 && peak < finestPeaks) {
            Store(data, peak++, min, max);
        }

        var level = data.AsSpan(0, (int)(peak * channels * 2)).ToArray();
        var framesPerPeak = finest;
        for (var l = 0; l < levels.Length; l++) {
            if (l > 0) {
                var factor = levels[l] / framesPerPeak;
                level = Merge(level, channels, factor);
                framesPerPeak = levels[l];
            }

            AtomicRename.Write(PathOf(peaksFolder, fileId, framesPerPeak), Encode(level, channels, framesPerPeak));
        }
    }

    /// <summary>
    ///     Peaks <paramref name="start" /> … <paramref name="start" /> + <paramref name="count" /> of a level file (all
    ///     of them when <paramref name="count" /> is null), as the API's body; the range is clipped to the file.
    /// </summary>
    public static byte[] Slice(string levelPath, long start, long? count) {
        using var stream = new FileStream(levelPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Span<byte> header = stackalloc byte[PeakFormat.HeaderBytes];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != PeakFormat.Magic) {
            throw new InvalidDataException($"'{levelPath}' is not a peak file.");
        }

        var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var framesPerPeak = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        var first = Math.Clamp(start, 0, total);
        var n = Math.Clamp(count ?? total, 0, total - first);
        var peakBytes = channels * 4L;
        var body = new byte[PeakFormat.HeaderBytes + n * peakBytes];
        WriteHeader(body, channels, (int)framesPerPeak, first, n);
        stream.Position = PeakFormat.HeaderBytes + first * peakBytes;
        stream.ReadExactly(body, PeakFormat.HeaderBytes, (int)(n * peakBytes));
        return body;
    }

    static void Store(short[] data, long peak, float[] min, float[] max) {
        var channels = min.Length;
        for (var c = 0; c < channels; c++) {
            var at = (peak * channels + c) * 2;
            data[at] = ToInt16(min[c]);
            data[at + 1] = ToInt16(max[c]);
        }
    }

    static short ToInt16(float x) => (short)Math.Round(Math.Clamp(x, -1f, 1f) * 32767);

    static short[] Merge(short[] finer, int channels, int factor) {
        var finerPeaks = finer.Length / (channels * 2);
        var peaks = (finerPeaks + factor - 1) / factor;
        var coarser = new short[peaks * channels * 2];
        for (var p = 0; p < peaks; p++) {
            for (var c = 0; c < channels; c++) {
                short lo = short.MaxValue, hi = short.MinValue;
                for (var k = p * factor; k < Math.Min(finerPeaks, (p + 1) * factor); k++) {
                    lo = Math.Min(lo, finer[(k * channels + c) * 2]);
                    hi = Math.Max(hi, finer[(k * channels + c) * 2 + 1]);
                }

                coarser[(p * channels + c) * 2] = lo;
                coarser[(p * channels + c) * 2 + 1] = hi;
            }
        }

        return coarser;
    }

    static byte[] Encode(short[] level, int channels, int framesPerPeak) {
        var count = level.Length / (channels * 2);
        var bytes = new byte[PeakFormat.HeaderBytes + level.Length * 2];
        WriteHeader(bytes, channels, framesPerPeak, 0, count);
        for (var i = 0; i < level.Length; i++) {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(PeakFormat.HeaderBytes + i * 2), level[i]);
        }

        return bytes;
    }

    static void WriteHeader(Span<byte> bytes, int channels, int framesPerPeak, long first, long count) {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, PeakFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], PeakFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], (uint)framesPerPeak);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], (uint)first);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], (ulong)count);
    }
}
