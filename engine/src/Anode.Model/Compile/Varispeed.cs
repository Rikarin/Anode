using Anode.Contracts;
using Anode.Ipc.Segments;
using System.Runtime.CompilerServices;

namespace Anode.Model.Compile;

/// <summary>
///     Varispeed as the compiler applies it (docs/06-phase-5.md, "Compilation"): every position of a compiled track is
///     scaled by 1/speed — events (MIDI, clicks, automation) and playlist entries with their fades. Every audio entry
///     becomes warped: an unwarped one with a two-point warp map; with speed only, unflexed audio stretches through
///     Polyphonic and warped audio keeps its algorithm; with speed and pitch, all audio is resampled (Speed); with MIDI
///     too, notes are transposed by round(12·log2(speed)) semitones. <see cref="None" /> leaves a model as it is.
/// </summary>
public sealed record Varispeed(double Speed, VarispeedMode Mode) {
    static readonly ConditionalWeakTable<TrackModel, Tuple<Varispeed, TrackModel>> Cache = new();

    public static Varispeed None { get; } = new(1, VarispeedMode.SpeedOnly);

    public bool IsNone => Speed == 1;

    /// <summary>The semitones MIDI notes move by (the MIDI mode only).</summary>
    public int Semitones => Mode == VarispeedMode.SpeedAndPitchMidi ? (int)Math.Round(12 * Math.Log2(Speed)) : 0;

    public static Varispeed Of(ProjectState state) =>
        state.Settings.Varispeed is { Enabled: true, Speed: > 0 and not 1 } v ? new(v.Speed, v.Mode) : None;

    /// <summary>A position at speed 1 as it plays at <see cref="Speed" />.</summary>
    public long Sample(long sample) => IsNone ? sample : (long)Math.Round(sample / Speed);

    /// <summary>The model at <see cref="Speed" />: the very instance for the same model and speed.</summary>
    public TrackModel Scaled(TrackModel model) {
        if (IsNone) {
            return model;
        }

        if (Cache.TryGetValue(model, out var cached) && cached.Item1 == this) {
            return cached.Item2;
        }

        var scaled = Scale(model);
        Cache.AddOrUpdate(model, Tuple.Create(this, scaled));
        return scaled;
    }

    TrackModel Scale(TrackModel model) {
        var semitones = Semitones;
        var events = new PlaybackEvent[model.Events.Length];
        for (var i = 0; i < events.Length; i++) {
            var e = model.Events[i] with { SamplePos = Sample(model.Events[i].SamplePos) };
            if (semitones != 0 && e.Kind is PlaybackEventKind.NoteOn or PlaybackEventKind.NoteOff) {
                e = e with { Data1 = (byte)Math.Clamp(e.Data1 + semitones, 0, 127) };
            }

            events[i] = e;
        }

        var warp = new List<WarpPoint>();
        var playlist = new AudioPlaylistEntry[model.Playlist.Length];
        for (var i = 0; i < playlist.Length; i++) {
            var e = model.Playlist[i];
            var start = Sample(e.Start);
            var length = Math.Max(1, Sample(e.Start + e.Length) - start);
            var points = new List<WarpPoint> { new() { Offset = 0, FileFrame = Frame(model, e, 0) } };
            if (e.WarpCount > 0) {
                for (var k = 1; k < e.WarpCount - 1; k++) {
                    var p = model.Warp![e.WarpStart + k];
                    var offset = Sample(e.Start + p.Offset) - start;
                    if (offset > points[^1].Offset && offset < length) {
                        points.Add(new() { Offset = offset, FileFrame = p.FileFrame });
                    }
                }
            }

            points.Add(new() { Offset = length, FileFrame = Frame(model, e, e.Length) });
            var fadeIn = (int)Math.Min(Sample(e.FadeIn), length);
            playlist[i] = e with {
                Start = start,
                Length = length,
                FadeIn = fadeIn,
                FadeOut = (int)Math.Min(Sample(e.FadeOut), length - fadeIn),
                WarpStart = warp.Count,
                WarpCount = points.Count,
                Algorithm = Mode == VarispeedMode.SpeedOnly
                    ? e.Algorithm == FlexAlgorithm.None ? FlexAlgorithm.Polyphonic : e.Algorithm
                    : FlexAlgorithm.Speed
            };
            warp.AddRange(points);
        }

        Crossfaded(playlist);
        return new(model.TrackId, events, playlist, [..warp], model.Pitch ?? []);
    }

    /// <summary>Where scaling made neighbours overlap past their fades, the fades grow to the overlap.</summary>
    static void Crossfaded(AudioPlaylistEntry[] playlist) {
        for (var i = 1; i < playlist.Length; i++) {
            var (previous, current) = (playlist[i - 1], playlist[i]);
            var overlap = previous.Start + previous.Length - current.Start;
            if (overlap <= 0) {
                continue;
            }

            if (overlap > previous.FadeOut && overlap <= previous.Length - previous.FadeIn) {
                playlist[i - 1] = previous with { FadeOut = (int)overlap };
            }

            if (overlap > current.FadeIn && overlap <= current.Length - current.FadeOut) {
                playlist[i] = current with { FadeIn = (int)overlap };
            }
        }
    }

    /// <summary>The file frame an entry plays at offset <paramref name="offset" /> (at speed 1).</summary>
    static double Frame(TrackModel model, AudioPlaylistEntry e, long offset) =>
        e.WarpCount == 0 ? e.FileOffset + offset : Warps.FrameAt(model.Warp![e.WarpStart..(e.WarpStart + e.WarpCount)], offset);
}
