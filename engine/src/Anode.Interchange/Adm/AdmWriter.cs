using Anode.Contracts;

namespace Anode.Interchange.Adm;

/// <summary>
///     ADM BWF writer (docs/11-phase-10.md, workstream D): BW64 (RIFF/WAVE below 4 GB; <c>BW64</c> with <c>ds64</c>
///     above, ITU-R BS.2088) with the beds' and objects' channels interleaved in scene order, a <c>chna</c> chunk mapping
///     each track to its <c>audioTrackUID</c>, and an <c>axml</c> chunk with the ADM (BS.2076): a programme, content,
///     an <c>audioObject</c> per bed and per object, pack formats (common definitions for the beds' layouts), channel
///     formats with the objects' block formats. 24-bit PCM or 32-bit float.
/// </summary>
/// <remarks>
///     The ADM (docs/spatial/README.md, "ADM BWF"): BS.2076-2 inside <c>ebuCoreMain</c>; one <c>audioProgramme</c>
///     (<c>APR_1001</c>) and <c>audioContent</c> (<c>ACO_1001</c>) holding every <c>audioObject</c> (<c>AO_1001</c>…, beds
///     first); a bed references its layout's BS.2094 pack (<c>AP_00010017</c> for 7.1.4) or, for quad, 7.0, 7.1.2 and
///     9.1.6, an Anode pack (<c>AP_00011001</c>…) of BS.2094's channel formats, whose stream and track formats the common
///     definitions hold too, so the file does not repeat them; an object has its pack (<c>AP_00031001</c>…), channel
///     formats (<c>AC_00031001</c>…, one per channel, the same blocks), stream and track formats; every track an
///     <c>audioTrackUID</c> (<c>ATU_00000001</c>… in file order) here and in <c>chna</c>. Block times use BS.2076-2's
///     sample-exact fractional format. Sizes: <c>width</c> = <c>height</c> = 360° × size, <c>depth</c> 0.
/// </remarks>
public static class AdmWriter {
    const int Block = 4096;

    /// <summary>Writes <paramref name="scene" /> to <paramref name="path" />; the sources must match its rate and length.</summary>
    public static void Write(AdmScene scene, string path, int bitDepth = 24) => Write(scene, path, bitDepth, false);

    /// <summary>
    ///     As <see cref="Write(AdmScene, string, int)" />; <paramref name="forceDs64" /> writes BW64 with ds64 at any
    ///     size (tests).
    /// </summary>
    internal static void Write(AdmScene scene, string path, int bitDepth, bool forceDs64) {
        if (bitDepth is not (24 or 32)) {
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "24 (PCM) or 32 (float).");
        }

        Validate(scene);
        var sources = new List<Bw64Reader>();
        try {
            foreach (var bed in scene.Beds) {
                sources.Add(Source(bed.AudioPath, SpeakerLayouts.ChannelCount(bed.Format), scene));
            }

            foreach (var o in scene.Objects) {
                sources.Add(Source(o.AudioPath, o.Channels, scene));
            }

            var (axml, chna, channels) = Metadata(scene, bitDepth);
            using var writer = new Bw64Writer(
                path,
                channels,
                scene.SampleRate,
                bitDepth,
                [("chna", chna), ("axml", axml)],
                forceDs64
            );
            Interleave(scene.Frames, sources, writer, channels);
        } finally {
            foreach (var source in sources) {
                source.Dispose();
            }
        }
    }

    /// <summary>The sources' channels side by side, block by block, into <paramref name="writer" />.</summary>
    static void Interleave(long length, List<Bw64Reader> sources, Bw64Writer writer, int channels) {
        var output = new float[Block * channels];
        var inputs = sources.Select(static s => new float[Block * s.Channels]).ToArray();
        for (long done = 0; done < length;) {
            var frames = (int)Math.Min(Block, length - done);
            var at = 0;
            for (var s = 0; s < sources.Count; s++) {
                var width = sources[s].Channels;
                Fill(sources[s], inputs[s], frames);
                for (var f = 0; f < frames; f++) {
                    inputs[s].AsSpan(f * width, width).CopyTo(output.AsSpan(f * channels + at, width));
                }

                at += width;
            }

            writer.Write(output.AsSpan(0, frames * channels));
            done += frames;
        }
    }

    static void Fill(Bw64Reader source, float[] input, int frames) {
        for (var read = 0; read < frames;) {
            var n = source.Read(input.AsSpan(read * source.Channels, (frames - read) * source.Channels));
            read += n > 0 ? n : throw new InvalidDataException("A source ended before the scene's length.");
        }
    }

    static Bw64Reader Source(string path, int channels, AdmScene scene) {
        var reader = Bw64Reader.Open(path, true);
        if (reader.SampleRate != scene.SampleRate || reader.Channels != channels || reader.Frames != scene.Frames) {
            var found = $"{reader.Channels} channels at {reader.SampleRate} Hz, {reader.Frames} frames";
            reader.Dispose();
            throw new ArgumentException(
                $"{path}: {found}; the scene needs {channels} at {scene.SampleRate} Hz, {scene.Frames} frames."
            );
        }

        return reader;
    }

    static void Validate(AdmScene scene) {
        ArgumentOutOfRangeException.ThrowIfLessThan(scene.SampleRate, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(scene.Frames, 1);
        if (scene.Beds.Count + scene.Objects.Count == 0) {
            throw new ArgumentException("The scene has neither beds nor objects.", nameof(scene));
        }

        foreach (var o in scene.Objects) {
            if (o.Channels is not (1 or 2)) {
                throw new ArgumentException($"Object '{o.Name}' has {o.Channels} channels (1 or 2).", nameof(scene));
            }

            if (o.Blocks.Count == 0 || o.Blocks[0].StartFrame != 0) {
                throw new ArgumentException($"Object '{o.Name}''s blocks must start at frame 0.", nameof(scene));
            }

            for (var b = 0; b < o.Blocks.Count; b++) {
                var block = o.Blocks[b];
                if (block.DurationFrames <= 0
                    || b + 1 < o.Blocks.Count
                    && o.Blocks[b + 1].StartFrame != block.StartFrame + block.DurationFrames) {
                    throw new ArgumentException($"Object '{o.Name}''s blocks must be contiguous.", nameof(scene));
                }
            }
        }
    }

    /// <summary>The <c>axml</c> and <c>chna</c> chunks' bodies and the file's channel count.</summary>
    internal static (byte[] Axml, byte[] Chna, int Channels) Metadata(AdmScene scene, int bitDepth) =>
        new AdmMetadata(scene, bitDepth).Build();
}

/// <summary>
///     Object blocks from positions over time (for <see cref="AdmMapping" />): breakpoints of the object panner's
///     automation become <see cref="AdmBlock" />s, contiguous from 0 to the scene's end, a ramp cut into blocks of at
///     most <see cref="MaxRampFrames" /> (20 ms) that interpolate, a hold (or a step) one block that jumps.
/// </summary>
public static class AdmBlocks {
    /// <summary>The longest interpolating block, in seconds (docs/11-phase-10.md).</summary>
    public const double MaxRampSeconds = 0.020;

    /// <summary>The longest interpolating block at <paramref name="sampleRate" />.</summary>
    public static long MaxRampFrames(int sampleRate) => (long)Math.Floor(MaxRampSeconds * sampleRate);

    /// <summary>
    ///     The blocks of an object whose position is <paramref name="points" /> (by frame, ascending; the first at or
    ///     before 0): each point's position holds until the next, or, with <c>Ramp</c>, moves linearly to the next
    ///     point's, over <paramref name="frames" /> at <paramref name="sampleRate" />.
    /// </summary>
    public static IReadOnlyList<AdmBlock> FromPoints(
        IReadOnlyList<(long Frame, double Azimuth, double Elevation, double Distance, double Size, bool Ramp)> points,
        long frames,
        int sampleRate
    ) {
        if (points.Count == 0) {
            return [new(0, frames, 0, 0, 1, 0, false)];
        }

        var step = Math.Max(1, MaxRampFrames(sampleRate));
        var blocks = new List<AdmBlock>();
        for (var p = 0; p < points.Count; p++) {
            var from = points[p];
            var start = Math.Max(0, from.Frame);
            var end = p + 1 < points.Count ? Math.Min(frames, points[p + 1].Frame) : frames;
            if (end <= start || start >= frames) {
                continue;
            }

            if (blocks.Count == 0 && start > 0) {
                blocks.Add(new(0, start, from.Azimuth, from.Elevation, from.Distance, from.Size, false));
            }

            if (!from.Ramp || p + 1 >= points.Count) {
                blocks.Add(new(start, end - start, from.Azimuth, from.Elevation, from.Distance, from.Size, false));
                continue;
            }

            Ramp(blocks, from, points[p + 1], start, end, step);
        }

        if (blocks.Count == 0) {
            var last = points[^1];
            blocks.Add(new(0, frames, last.Azimuth, last.Elevation, last.Distance, last.Size, false));
        }

        return blocks;
    }

    /// <summary>
    ///     A ramp's blocks: ADM interpolates from the previous block's position, so a one-frame jump to the ramp's start
    ///     comes first unless the previous block ends there.
    /// </summary>
    static void Ramp(
        List<AdmBlock> blocks,
        (long Frame, double Azimuth, double Elevation, double Distance, double Size, bool Ramp) from,
        (long Frame, double Azimuth, double Elevation, double Distance, double Size, bool Ramp) to,
        long start,
        long end,
        long step
    ) {
        if (blocks.Count == 0 || !SamePosition(blocks[^1], from)) {
            var first = Math.Min(end, start + 1);
            blocks.Add(new(start, first - start, from.Azimuth, from.Elevation, from.Distance, from.Size, false));
            start = first;
        }

        double span = to.Frame - from.Frame;
        for (var at = start; at < end; at += step) {
            var stop = Math.Min(end, at + step);
            var t = (stop - from.Frame) / span;
            blocks.Add(
                new(
                    at,
                    stop - at,
                    from.Azimuth + t * (to.Azimuth - from.Azimuth),
                    from.Elevation + t * (to.Elevation - from.Elevation),
                    from.Distance + t * (to.Distance - from.Distance),
                    from.Size + t * (to.Size - from.Size),
                    true
                )
            );
        }
    }

    static bool SamePosition(
        AdmBlock block,
        (long Frame, double Azimuth, double Elevation, double Distance, double Size, bool Ramp) point
    ) =>
        Math.Abs(block.Azimuth - point.Azimuth) < 1e-9
        && Math.Abs(block.Elevation - point.Elevation) < 1e-9
        && Math.Abs(block.Distance - point.Distance) < 1e-9
        && Math.Abs(block.Size - point.Size) < 1e-9;
}
