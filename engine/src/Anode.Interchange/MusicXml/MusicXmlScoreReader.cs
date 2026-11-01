using Anode.Contracts;
using Anode.Model;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     Reads a partwise document: the header (title, composer, the part list), each part through
///     <see cref="MusicXmlPartReader" />, and the staff styles, tempo, meter, key and markers gathered from them.
/// </summary>
sealed class MusicXmlScoreReader {
    readonly XElement root;
    readonly Dictionary<long, double> tempoByTick = [];
    readonly Dictionary<string, StaffStyle> customStyles = [];

    public MusicXmlScoreReader(XDocument document) {
        root = document.Root ?? throw new FormatException("An empty XML document.");
        if (root.Name.LocalName == "score-timewise") {
            throw new FormatException(
                "score-timewise MusicXML is not imported: convert it to score-partwise (MusicXML's parttime.xsl does)."
            );
        }

        if (root.Name.LocalName != "score-partwise") {
            throw new FormatException($"Not a MusicXML score: the document is <{root.Name.LocalName}>.");
        }
    }

    public SequentialIds Ids { get; } = new();

    public List<ScoreSymbol> Symbols { get; } = [];

    public List<LyricSyllable> Lyrics { get; } = [];

    public List<ChordSymbol> Chords { get; } = [];

    public List<Marker> Markers { get; } = [];

    long nextNoteId;

    public long NextNoteId() => ++nextNoteId;

    public void AddTempo(long tick, double bpm) {
        if (bpm is > 0 and < 10000 && double.IsFinite(bpm)) {
            tempoByTick.TryAdd(tick, bpm);
        }
    }

    public void AddMarker(long tick, string text) {
        if (!Markers.Any(m => m.Tick == tick && m.Name == text)) {
            Markers.Add(new(Ids.Next(IdPrefix.Marker), text, tick, 0, null, string.Empty));
        }
    }

    public ImportedContent Read() {
        var parts = root.Elements("part").ToList();
        if (parts.Count == 0) {
            throw new FormatException("The MusicXML score has no parts.");
        }

        var list = PartList();
        var tracks = new List<ImportedTrack>();
        IReadOnlyList<SignatureEvent>? signatures = null;
        IReadOnlyList<KeyEvent>? keys = null;
        for (var i = 0; i < parts.Count; i++) {
            var id = (string?)parts[i].Attribute("id") ?? string.Empty;
            var info = list.GetValueOrDefault(id) ?? new PartInfo(string.Empty, string.Empty, new Dictionary<string, int>());
            var trackId = Ids.Next(IdPrefix.Track);
            var reader = new MusicXmlPartReader(this, parts[i], info, trackId);
            var result = reader.Read();
            if (i == 0) {
                signatures = result.Signatures;
                keys = result.Keys;
            }

            var name = info.Name.Length > 0 ? info.Name : info.Abbreviation.Length > 0 ? info.Abbreviation : $"Part {i + 1}";
            var region = new MidiRegion(
                Ids.Next(IdPrefix.MidiRegion),
                trackId,
                name,
                0,
                Math.Max(1, result.LengthTicks),
                ProjectDefaults.Playback,
                result.Notes,
                [],
                false,
                RegionNotation.Default
            );
            tracks.Add(new(trackId, name, TrackKind.Instrument, Style(result, name), [region], []));
        }

        var tempos = tempoByTick.OrderBy(t => t.Key).Select(t => new TempoEvent(t.Key, t.Value, TempoCurve.Step)).ToList();
        if (tempos.Count > 0 && tempos[0].Tick > 0) {
            tempos.Insert(0, new(0, 120, TempoCurve.Step));
        }

        return new(
            tracks,
            tempos.Count > 0 ? tempos : null,
            signatures,
            keys,
            Markers.Count > 0 ? [..Markers.OrderBy(m => m.Tick)] : null,
            new(
                [..customStyles.Values],
                Symbols,
                Lyrics,
                Chords,
                Text(Title()),
                Text(Composer())
            )
        );
    }

    /// <summary>A part's name, abbreviation, and its score instruments' MIDI keys (percussion).</summary>
    internal sealed record PartInfo(string Name, string Abbreviation, IReadOnlyDictionary<string, int> Unpitched);

    Dictionary<string, PartInfo> PartList() {
        var parts = new Dictionary<string, PartInfo>();
        foreach (var part in root.Element("part-list")?.Elements("score-part") ?? []) {
            var id = (string?)part.Attribute("id");
            if (id is null) {
                continue;
            }

            var unpitched = new Dictionary<string, int>();
            foreach (var midi in part.Elements("midi-instrument")) {
                if ((string?)midi.Attribute("id") is { } instrument
                    && MusicXmlPartReader.Integer(midi.Element("midi-unpitched")?.Value) is { } key and >= 1 and <= 128) {
                    unpitched[instrument] = key - 1;
                }
            }

            parts[id] = new(
                Text(part.Element("part-name")?.Value) ?? string.Empty,
                Text(part.Element("part-abbreviation")?.Value) ?? string.Empty,
                unpitched
            );
        }

        return parts;
    }

    string? Title() =>
        root.Element("work")?.Element("work-title")?.Value is { Length: > 0 } work
            ? work
            : root.Element("movement-title")?.Value is { Length: > 0 } movement
                ? movement
                : Credit("title");

    string? Composer() =>
        root.Element("identification")?.Elements("creator").FirstOrDefault(c => (string?)c.Attribute("type") == "composer")
            ?.Value
        ?? Credit("composer");

    string? Credit(string type) =>
        root.Elements("credit")
            .FirstOrDefault(c => c.Elements("credit-type").Any(t => t.Value.Trim() == type))
            ?.Element("credit-words")
            ?.Value;

    /// <summary>Text trimmed to what the score model holds (null: none).</summary>
    internal static string? Text(string? text) {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) {
            return null;
        }

        return trimmed.Length > ScoreSymbols.MaxTextLength ? trimmed[..ScoreSymbols.MaxTextLength] : trimmed;
    }

    // ---- Staff styles.

    /// <summary>
    ///     The part's staff style: a factory style when its staves match one (by clefs and transposition, the name telling
    ///     the B♭ trumpet from the clarinet and the cello from the bass), else a style of its own (one per distinct staff
    ///     setup).
    /// </summary>
    string Style(MusicXmlPartReader.Result part, string name) {
        var clefs = part.Clefs;
        var transpose = part.Transpose;
        if (clefs is [Clef.Treble8vb or Clef.Bass8vb] && transpose == 12) {
            // Written an octave above sounding with an octave clef: the clef is the octave (the factory guitar).
            transpose = 0;
        }

        var lower = name.ToLowerInvariant();
        var factory = (clefs, transpose) switch {
            ([Clef.Percussion], _) => StaffStyleIds.Drums,
            ([Clef.Treble or Clef.None], 0) => StaffStyleIds.Treble,
            ([Clef.Treble8vb], 0) => StaffStyleIds.Guitar,
            ([Clef.Bass8vb], 0) => StaffStyleIds.BassGuitar,
            ([Clef.Alto], 0) => StaffStyleIds.Viola,
            ([Clef.Bass], 0) => lower.Contains("cello", StringComparison.Ordinal) || lower.Contains("violoncello", StringComparison.Ordinal)
                ? StaffStyleIds.Cello
                : StaffStyleIds.Bass,
            ([Clef.Treble], 2) => lower.Contains("trumpet", StringComparison.Ordinal) ? StaffStyleIds.TrumpetBb : StaffStyleIds.ClarinetBb,
            ([Clef.Treble], 7) => StaffStyleIds.HornF,
            ([Clef.Treble], 9) => StaffStyleIds.AltoSax,
            ([Clef.Treble], 14) => StaffStyleIds.TenorSax,
            ([Clef.Treble, Clef.Bass], 0) => StaffStyleIds.Piano,
            _ => null
        };
        if (factory is not null) {
            return factory;
        }

        var staves = new List<Staff>();
        for (var s = 0; s < clefs.Count; s++) {
            int? split = s == clefs.Count - 1 ? null : Math.Clamp(part.LowestPitches[s] ?? DefaultSplit(clefs[s]), 0, 127);
            staves.Add(new(clefs[s], transpose, split, Math.Clamp(part.Voices[s], 1, 4)));
        }

        var key = string.Join(
            ";",
            staves.Select(s => FormattableString.Invariant($"{s.Clef}:{s.Transpose}:{s.SplitPitch}:{s.Voices}"))
        );
        if (!customStyles.TryGetValue(key, out var style)) {
            var brace = clefs.Count > 1 && (part.Brace ?? (clefs[0] is Clef.Treble && clefs[^1] is Clef.Bass));
            style = new(Ids.Next(IdPrefix.StaffStyle), name, staves, null, 240, 160, brace);
            customStyles[key] = style;
        }

        return style.Id;
    }

    static int DefaultSplit(Clef clef) =>
        clef switch {
            Clef.Bass or Clef.Bass8vb => 36,
            Clef.Alto or Clef.Tenor => 48,
            _ => 60
        };
}
