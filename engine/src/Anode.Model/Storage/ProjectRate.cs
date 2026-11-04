using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Time;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Anode.Model.Storage;

/// <summary>
///     A project at another sample rate (docs/01-implementation-plan.md §5.6, "Changing the sample rate"). Ticks are
///     tempo-relative and stay; everything stored in frames is rewritten. <c>Convert</c> keeps the sound: each file is a
///     new, converted file, and frames scale by the ratio, so what plays at a second plays there after. <c>KeepSamples</c>
///     keeps the samples: each file is a copy labelled with the new rate, frames stay, and the tempo scales by the ratio,
///     so every position lands on the very same sample while the whole project plays faster or slower, audio pitch with
///     it. Files are never rewritten in place: backups and alternatives go on naming the ones they know.
/// </summary>
public static class ProjectRate {
    /// <summary>Which files a change writes: the rest stay as they are.</summary>
    public static IEnumerable<AudioFile> FilesToWrite(ProjectState state, int rate, SampleRateChangeMode mode) =>
        mode == SampleRateChangeMode.Convert
            ? state.Files.Values.Where(f => f.SampleRate != rate)
            : state.Files.Values.Where(f => f.SampleRate == state.SampleRate);

    /// <summary>Why <paramref name="state" /> cannot change as asked, or null.</summary>
    public static string? Refusal(ProjectState state, int rate, SampleRateChangeMode mode) {
        if (mode != SampleRateChangeMode.KeepSamples) {
            return null;
        }

        var ratio = (double)rate / state.SampleRate;
        return (state.Settings.TempoEvents ?? []).Any(e => e.Bpm * ratio is < TempoMap.MinBpm or > TempoMap.MaxBpm)
            ? $"Keeping the samples at {rate} Hz takes a tempo outside {TempoMap.MinBpm}…{TempoMap.MaxBpm} BPM."
            : null;
    }

    /// <summary>A frame count at <paramref name="to" /> for one at <paramref name="from" />, rounded.</summary>
    public static long Scale(long frames, int from, int to) => (long)(((Int128)frames * to * 2 + from) / (from * 2L));

    /// <summary>A file as it will be: new id, path, frames and rate from <paramref name="written" />, its tempo carried over.</summary>
    public static AudioFile Written(
        AudioFile source,
        string id,
        string path,
        WavInfo written,
        SampleRateChangeMode mode
    ) =>
        new(
            id,
            source.Name,
            path,
            written.SampleRate,
            written.Channels,
            written.Frames,
            written.BitDepth,
            Tempo(source.Tempo, source.SampleRate, written.SampleRate, mode),
            0
        );

    /// <summary>A cached analysis of <paramref name="source" /> as the analysis of the file written from it.</summary>
    public static FileAnalysis Written(
        FileAnalysis analysis,
        AudioFile source,
        AudioFile written,
        int hop,
        SampleRateChangeMode mode
    ) {
        var (from, to) = (source.SampleRate, written.SampleRate);
        var shift = Cents(from, to);
        if (mode == SampleRateChangeMode.KeepSamples) {
            return analysis with {
                FileId = written.Id,
                Tempo = written.Tempo,
                Pitch = [..analysis.Pitch.Select(p => p + (float)shift)],
                Notes = [..analysis.Notes.Select(n => n with { Cents = n.Cents + shift })]
            };
        }

        var count = (int)Math.Round(analysis.Pitch.Count * (double)analysis.Hop * to / from / hop);
        return analysis with {
            FileId = written.Id,
            Hop = hop,
            Transients = [..analysis.Transients.Select(t => Scale(t, from, to))],
            Tempo = written.Tempo,
            Pitch = [
                ..Enumerable.Range(0, count)
                    .Select(i => analysis.Pitch[
                            Math.Min(
                                analysis.Pitch.Count - 1,
                                (int)Math.Round(i * (double)hop * from / to / analysis.Hop)
                            )
                        ]
                    )
            ],
            Notes = [
                ..analysis.Notes.Select(n => n with {
                        StartFrame = Scale(n.StartFrame, from, to), EndFrame = Scale(n.EndFrame, from, to)
                    }
                )
            ]
        };
    }

    /// <summary>
    ///     <paramref name="state" /> at <paramref name="rate" />, with each file of <paramref name="files" /> (by the id
    ///     it replaces) in place of the one it was written from, and everything that names one following it.
    /// </summary>
    public static ProjectState Changed(
        ProjectState state,
        int rate,
        SampleRateChangeMode mode,
        IReadOnlyDictionary<string, AudioFile> files
    ) {
        var change = new Change(state, rate, mode, files);
        return change.Apply();
    }

    /// <summary>
    ///     Writes a copy of a WAV, bit for bit but for the rate its header says (and the byte rate that follows from it).
    /// </summary>
    public static void Relabel(string source, string target, int rate) {
        ProjectCopy.CopyFile(source, target, CancellationToken.None);
        using var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> header = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        file.Position = 12;
        while (file.Read(header) == header.Length) {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (Encoding.ASCII.GetString(header[..4]) != "fmt ") {
                file.Position += size + (size & 1);
                continue;
            }

            file.ReadExactly(format);
            var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
            BinaryPrimitives.WriteInt32LittleEndian(format[4..], rate);
            BinaryPrimitives.WriteInt32LittleEndian(format[8..], rate * blockAlign);
            file.Position -= format.Length;
            file.Write(format);
            file.Flush(true);
            return;
        }

        throw new WavFormatException($"'{source}' has no format chunk.");
    }

    static FileTempo? Tempo(FileTempo? tempo, int from, int to, SampleRateChangeMode mode) =>
        tempo is null ? null
        : mode == SampleRateChangeMode.KeepSamples ? tempo with { Bpm = tempo.Bpm * to / from }
        : tempo with {
            Beats = [..tempo.Beats.Select(b => Scale(b, from, to))],
            Downbeats = [..tempo.Downbeats.Select(b => Scale(b, from, to))]
        };

    static double Cents(int from, int to) => 1200 * Math.Log2((double)to / from);

    /// <summary>One change, with what it needs to hand.</summary>
    sealed class Change(
        ProjectState state,
        int rate,
        SampleRateChangeMode mode,
        IReadOnlyDictionary<string, AudioFile> files
    ) {
        readonly bool keeps = mode == SampleRateChangeMode.KeepSamples;
        readonly int from = state.SampleRate;

        public ProjectState Apply() {
            var regions = state.Regions.SetItems(
                state.Regions.Select(r => KeyValuePair.Create(r.Key, Region(r.Value)))
            );
            var liveLoops = state.LiveLoops with {
                Cells = [
                    ..state.LiveLoops.Cells.Select(c => c.Content.Audio is { } audio
                            ? c with { Content = c.Content with { Audio = Region(audio) } }
                            : c
                    )
                ]
            };
            return state with {
                SampleRate = rate,
                Settings = keeps ? Faster(state.Settings) : state.Settings,
                Tracks = [..state.Tracks.Select(Track)],
                Regions = regions,
                Files = state.Files.Values.Select(f => files.GetValueOrDefault(f.Id, f))
                    .ToImmutableSortedDictionary(static f => f.Id, static f => f, StringComparer.Ordinal),
                TakeFolders = state.TakeFolders.SetItems(
                    state.TakeFolders.Select(f => KeyValuePair.Create(f.Key, Folder(f.Value)))
                ),
                LiveLoops = liveLoops
            };
        }

        /// <summary>The tempo that keeps every tick on the sample it was on.</summary>
        ProjectSettings Faster(ProjectSettings settings) {
            var ratio = (double)rate / from;
            return settings with {
                Tempo = settings.Tempo * ratio,
                TempoEvents = [..settings.TempoEvents.Select(e => e with { Bpm = e.Bpm * ratio })]
            };
        }

        Track Track(Track track) =>
            track with {
                FrozenFileId = track.FrozenFileId is { } frozen && files.TryGetValue(frozen, out var file)
                    ? file.Id
                    : track.FrozenFileId,
                Alternatives = [
                    ..track.Alternatives.Select(a => a with {
                            Regions = [..a.Regions.Select(Region)], TakeFolders = [..a.TakeFolders.Select(Folder)]
                        }
                    )
                ]
            };

        TakeFolder Folder(TakeFolder folder) =>
            folder with {
                Takes = [..folder.Takes.Select(Take)],
                Crossfade = keeps ? folder.Crossfade : Scale(folder.Crossfade, from, rate)
            };

        Take Take(Take take) {
            if (!files.TryGetValue(take.FileId, out var file)) {
                return take;
            }

            var (offset, length) = Span(take.FileId, file, take.FileOffset, take.Length);
            return take with { FileId = file.Id, FileOffset = offset, Length = length };
        }

        AudioRegion Region(AudioRegion region) {
            if (!files.TryGetValue(region.FileId, out var file)) {
                return region;
            }

            var (offset, length) = Span(region.FileId, file, region.FileOffset, region.Length);
            var shift = Cents(from, rate);
            if (keeps) {
                return region with {
                    FileId = file.Id,
                    PitchNotes = [
                        ..region.PitchNotes.Select(n => n with {
                                DetectedCents = n.DetectedCents + shift, TargetCents = n.TargetCents + shift
                            }
                        )
                    ]
                };
            }

            var fadeIn = Math.Min(Scale(region.FadeIn, from, rate), length);
            return region with {
                FileId = file.Id,
                FileOffset = offset,
                Length = length,
                FadeIn = fadeIn,
                FadeOut = Math.Min(Scale(region.FadeOut, from, rate), length - fadeIn),
                FlexMarkers = Markers(region.FlexMarkers, region.FileId),
                PitchNotes = [
                    ..region.PitchNotes.Select(n => n with {
                            StartFrame = Frame(region.FileId, n.StartFrame), EndFrame = Frame(region.FileId, n.EndFrame)
                        }
                    )
                ]
            };
        }

        /// <summary>A span of a file in its new frames: inside the file, and never empty.</summary>
        (long Offset, long Length) Span(string fileId, AudioFile file, long offset, long length) {
            if (keeps) {
                return (offset, length);
            }

            var start = Math.Min(Frame(fileId, offset), Math.Max(0, file.Frames - 1));
            var end = Math.Min(Frame(fileId, offset + length), file.Frames);
            return (start, Math.Max(1, end - start));
        }

        long Frame(string fileId, long frame) => Scale(frame, state.Files[fileId].SampleRate, rate);

        /// <summary>Flex markers in new frames, still strictly in order where rounding would bring two together.</summary>
        List<FlexMarker> Markers(IReadOnlyList<FlexMarker> markers, string fileId) {
            var scaled = new List<FlexMarker>(markers.Count);
            long last = -1;
            foreach (var marker in markers) {
                last = Math.Max(last + 1, Frame(fileId, marker.FileFrame));
                scaled.Add(marker with { FileFrame = last });
            }

            return scaled;
        }
    }
}
