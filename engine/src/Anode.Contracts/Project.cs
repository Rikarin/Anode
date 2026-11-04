using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>A project in the recent list.</summary>
public sealed record ProjectSummary(string Id, string Name, string Path, DateTimeOffset Modified);

/// <summary>
///     The open project (docs/01-implementation-plan.md §5). Musical positions are ticks at 960 PPQ; audio offsets,
///     lengths and fades are frames at the project's <c>sampleRate</c>. <c>revision</c> grows with every edit and is
///     the ETag; <c>dirty</c> says whether it has unsaved edits. Phase 6 (docs/07-phase-6.md): <c>patternRegions</c>
///     are the Step Sequencer's regions; <c>liveLoops</c> is the Live Loops grid. Phase 7 (docs/08-phase-7.md):
///     <c>grooves</c> are the project's groove templates, <c>articulationSets</c> its articulation sets and
///     <c>laneSets</c> its Step Editor lane sets. Phase 9 (docs/10-phase-9.md): <c>score</c> is its stored score content
///     (staff styles, symbols, lyrics, chord symbols, score sets, layout). Phase 10 (docs/11-phase-10.md): <c>movie</c>
///     is its movie (null: none). Phase 11 (docs/12-phase-11.md): <c>chordTrack</c> is its chord track (chords, key and
///     scale changes), <c>sessionPlayerRegions</c> the Session Player regions of its Drummer, Bass Player and Keyboard
///     Player tracks, and <c>environment</c> its MIDI Environment (empty: physical input to sequencer input).
///     <c>offline</c> is what the disk lacks of it (not stored; the <c>offlineChanged</c> event follows it).
/// </summary>
public sealed record Project(
    string Id,
    string Name,
    string Path,
    long Revision,
    bool Dirty,
    int SampleRate,
    ProjectSettings Settings,
    ChannelStrip Master,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<AudioRegion> Regions,
    IReadOnlyList<AudioFile> AudioFiles,
    IReadOnlyList<MidiRegion> MidiRegions,
    IReadOnlyList<TakeFolder> TakeFolders,
    IReadOnlyList<Marker> Markers,
    IReadOnlyList<ArrangementMarker> Arrangement,
    string Notes,
    IReadOnlyList<MixerGroup> Groups,
    IReadOnlyList<AutomationLane> Automation,
    IReadOnlyList<PatternRegion> PatternRegions,
    LiveLoopsGrid LiveLoops,
    IReadOnlyList<GrooveTemplate> Grooves,
    IReadOnlyList<ArticulationSet> ArticulationSets,
    IReadOnlyList<LaneSet> LaneSets,
    ProjectScore Score,
    Movie? Movie,
    ChordTrack ChordTrack,
    IReadOnlyList<SessionPlayerRegion> SessionPlayerRegions,
    MidiEnvironment Environment,
    OfflineReport Offline
);

/// <summary>
///     Global settings. <c>tempo</c> and <c>signature</c> are the values at the project start (the first of
///     <c>tempoEvents</c> and <c>signatures</c>, which are the global tempo and signature tracks).
///     <c>endTick</c> is the project end marker. <c>mergeMidiRecording</c>: a MIDI recording over an
///     existing MIDI region adds its notes to that region instead of making a new one. <c>smartTempo</c> and
///     <c>varispeed</c> (Phase 5): the project tempo mode and Varispeed. <c>grooveTrackId</c> (Phase 7) is the groove
///     track (null: none): tracks whose <c>matchGroove</c> is on quantise to the groove of its first region. <c>sync</c>
///     (Phase 8, docs/09-phase-8.md) is the project's synchronisation: MIDI Clock, MTC and Link.
///     <c>surroundFormat</c> (Phase 10, docs/11-phase-10.md) is the project's channel format: the master's, and what
///     strips feeding the master output (stereo: as before Phase 10).
/// </summary>
public sealed record ProjectSettings(
    double Tempo,
    TimeSignature Signature,
    CycleRange Cycle,
    MetronomeSettings Metronome,
    long EndTick,
    bool MergeMidiRecording,
    IReadOnlyList<TempoEvent> TempoEvents,
    IReadOnlyList<SignatureEvent> Signatures,
    IReadOnlyList<KeyEvent> Keys,
    RecordingSettings Recording,
    MixerSettings Mixer,
    SmartTempoSettings SmartTempo,
    VarispeedSettings Varispeed,
    string? GrooveTrackId,
    SyncSettings Sync,
    ChannelFormat SurroundFormat
);

public sealed record TimeSignature(int Numerator, int Denominator);

public sealed record CycleRange(bool Enabled, long StartTick, long EndTick);

/// <summary>What a count-in or a pre-roll is counted in: bars or beats of the signature in force.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MusicalUnit>))]
public enum MusicalUnit {
    [JsonStringEnumMemberName("bars")]
    Bars,

    [JsonStringEnumMemberName("beats")]
    Beats
}

/// <summary>
///     The click: on or off, a count-in before recording from a stop, and its level in dB. The count-in lasts
///     <c>countInLength</c> (1…32) of <c>countInUnit</c>, measured at where recording starts.
/// </summary>
public sealed record MetronomeSettings(
    bool Enabled,
    bool CountIn,
    double LevelDb,
    int CountInLength = 1,
    MusicalUnit CountInUnit = MusicalUnit.Bars
);

[JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
public enum TrackKind {
    [JsonStringEnumMemberName("audio")]
    Audio,

    /// <summary>A software instrument: MIDI regions played by <c>instrument</c>.</summary>
    [JsonStringEnumMemberName("instrument")]
    Instrument,

    /// <summary>External MIDI: MIDI regions sent to <c>midiOutput</c>.</summary>
    [JsonStringEnumMemberName("midi")]
    Midi,

    /// <summary>
    ///     A folder track stack: the tracks whose <c>parentId</c> is this track. Its strip's volume offsets theirs, its
    ///     mute and solo apply to all of them; it plays nothing itself.
    /// </summary>
    [JsonStringEnumMemberName("stack")]
    Stack,

    /// <summary>An aux channel: it plays what reaches bus <c>inputBus</c> through its strip.</summary>
    [JsonStringEnumMemberName("aux")]
    Aux,

    /// <summary>A VCA: its volume, mute and solo control the tracks whose strip names it in <c>vcaId</c>.</summary>
    [JsonStringEnumMemberName("vca")]
    Vca,

    /// <summary>
    ///     Phase 11 (docs/12-phase-11.md): a Drummer track. It is a Drum Machine Designer (<c>drumMachine</c>, its kit;
    ///     its pads are member instrument tracks, so the kit is multi-output) that plays Session Player regions of a
    ///     drum style, and MIDI and pattern regions as a DMD does.
    /// </summary>
    [JsonStringEnumMemberName("drummer")]
    Drummer,

    /// <summary>
    ///     Phase 11: a Bass Player track: an instrument track (<c>instrument</c>) that plays Session Player regions of a
    ///     bass style, and MIDI regions.
    /// </summary>
    [JsonStringEnumMemberName("bassPlayer")]
    BassPlayer,

    /// <summary>
    ///     Phase 11: a Keyboard Player track: an instrument track that plays Session Player regions of a keys style
    ///     (Synth Player styles included), and MIDI regions.
    /// </summary>
    [JsonStringEnumMemberName("keyboardPlayer")]
    KeyboardPlayer
}

/// <summary>
///     A track and its channel strip. <c>color</c> is a CSS colour; <c>input</c> is where an audio track records from.
///     An instrument track has an <c>instrument</c>; instrument and external MIDI tracks take MIDI from
///     <c>midiInput</c>, and an external MIDI track sends its regions to <c>midiOutput</c>. Phase 5: <c>flexOn</c> and
///     <c>flexMode</c> are its flex algorithm; <c>alternatives</c> are its inactive track alternatives, and
///     <c>alternativeId</c> names the active one (null until the track has alternatives). Phase 6: a Drum Machine
///     Designer stack has a <c>drumMachine</c> (null on every other track); <c>smartControls</c> are its Smart Controls
///     (null until set up: the editor shows the 8-knob layout, unnamed, at 0 and unmapped, and the first Smart Controls
///     command sets them up); <c>liveLoopsActive</c>: the track plays its Live Loops cells, not its Tracks-area
///     regions. Phase 7: <c>matchGroove</c>: its quantise follows the groove track (<c>settings.grooveTrackId</c>);
///     <c>articulationSetId</c> is its articulation set (null: none). Phase 9: <c>staffStyleId</c> is its staff style
///     in the score (a factory id or one of <c>score.staffStyles</c>; null: <c>StaffStyleIds.Default</c>).
/// </summary>
public sealed record Track(
    string Id,
    string Name,
    TrackKind Kind,
    string Color,
    TrackInput Input,
    bool RecordArmed,
    bool InputMonitoring,
    ChannelStrip Strip,
    InstrumentSlot? Instrument,
    MidiInputSettings MidiInput,
    MidiOutputSettings? MidiOutput,
    string? ParentId,
    bool Collapsed,
    string Notes,
    int InputBus,
    AutomationMode AutomationMode,
    string? FrozenFileId,
    bool FlexOn,
    FlexMode FlexMode,
    IReadOnlyList<TrackAlternative> Alternatives,
    string? AlternativeId,
    DrumMachine? DrumMachine,
    SmartControls? SmartControls,
    bool LiveLoopsActive,
    bool MatchGroove,
    string? ArticulationSetId,
    string? StaffStyleId
);

/// <summary>The device input: 1-based first channel (0: none); a stereo input takes that channel and the next.</summary>
public sealed record TrackInput(int Channel, bool Stereo);

/// <summary>
///     A channel strip: volume in dB (−∞ as −144), pan −1 (left) … 1 (right), mute and solo; its insert effects in
///     order; its sends to buses; where it goes (<c>output</c>: <c>master</c>, <c>bus:N</c> for bus 1…64, or
///     <c>output:N</c> for the device's stereo output pair N); the mixer groups it belongs to (1…32); and the VCA track
///     that offsets its volume, if any. The master strip uses only volume, pan, mute and its inserts. Phase 6:
///     <c>midiEffects</c> are its MIDI FX slots (instrument and external MIDI tracks only; empty elsewhere), in the
///     same shape as inserts: they run in slot order on the block's MIDI (regions, live and step input) before the
///     instrument; bypassed slots pass it through.
///     <para>
///         Phase 10 (docs/11-phase-10.md): <c>inputFormat</c> is the format of the signal the strip processes before its
///         panner (an audio track's recording and regions, an instrument's output, an aux's bus: an aux's input format
///         is its bus's format); <c>outputFormat</c> is its destination's format, kept by the server (the master's is
///         the project's <c>surroundFormat</c>, a bus's its aux's input format, a device output pair stereo; the
///         master's input and output are the project's). <c>panner</c> maps the input channels to the output: stereo
///         pan (<c>pan</c>) for a stereo output, the Surround Panner, or an object (<see cref="PannerMode" />); an input
///         of the output's format passes straight through. Sends to a multichannel bus are panned by the strip's panner
///         (their <c>pan</c> applies to stereo buses only). Every strip of a project from before Phase 10 is stereo in
///         and out with the stereo panner.
///     </para>
///     <para>
///         The strip's input stage (a track with a full strip, never the master or a VCA): <c>trimDb</c> and
///         <c>polarityInverted</c> apply to its source before the inserts, on every channel; <c>delayMs</c> moves it in
///         time after the inserts, before the fader (negative: earlier, which delay compensation pays for by delaying
///         everything else). <c>dualPan</c> pans a stereo signal's two channels on their own, to <c>panLeft</c> and
///         <c>panRight</c> (−1…1), instead of balancing it with <c>pan</c>; at −1 and 1 the signal passes as it is. They
///         stay when it is off. A mono signal always takes <c>pan</c>, a surround output its panner.
///     </para>
///     <para>
///         <c>soloSafe</c> (a track with a full strip, a bus's aux above all): the strip stays audible when others are
///         soloed in place.
///     </para>
///     <para><c>meter</c> is what the strip's meter shows and where it reads the signal, the master's too.</para>
/// </summary>
public sealed record ChannelStrip(
    double VolumeDb,
    double Pan,
    bool Mute,
    bool Solo,
    IReadOnlyList<Insert> Inserts,
    IReadOnlyList<Send> Sends,
    string Output,
    IReadOnlyList<int> Groups,
    string? VcaId,
    IReadOnlyList<Insert> MidiEffects,
    ChannelFormat InputFormat,
    ChannelFormat OutputFormat,
    Panner Panner,
    double TrimDb,
    bool PolarityInverted,
    double DelayMs,
    bool DualPan,
    double PanLeft,
    double PanRight,
    bool SoloSafe,
    StripMeter Meter
);

/// <summary>
///     A region of an audio file on a track: it starts at <c>startTick</c> and plays <c>length</c> frames of the file
///     from <c>fileOffset</c>, at <c>gainDb</c>, with fades of <c>fadeIn</c> and <c>fadeOut</c> frames whose curves run
///     from −1 (slow start, exponential-looking) through 0 (linear) to 1 (fast start, logarithmic-looking): a fade-in's
///     gain at position x (0…1) is x^(2^(−2c)), a fade-out the mirror. A muted region
///     does not play. Two regions on a track may overlap only as a crossfade: where the earlier fades out and the later
///     fades in.
/// </summary>
/// <remarks>
///     Phase 5 (docs/06-phase-5.md, <c>RegionTiming</c>): a region is <em>warped</em> when its track's flex is on, its
///     <c>follow</c> is not <c>off</c>, or it has a <c>transpose</c> (semitones), a <c>fineTune</c> (cents) or
///     <c>pitchNotes</c>; these need Flex or Follow, and the server refuses them otherwise. An unwarped region plays
///     <c>fileOffset + n</c> at <c>startTick</c> plus n frames, as before. A warped region's file frames map through
///     its base map (by <c>follow</c> and the file's <c>tempo</c>), scaled linearly between its <c>flexMarkers</c>;
///     its end tick is where its last file frame lands (derived, never stored). Fades stay in project frames.
///     <para>
///         Phase 11 (docs/12-phase-11.md): <c>pitchSource</c> is how it follows the chord track (<c>off</c> or
///         <c>root</c>: each chord segment transposed, crossfaded; <c>chords</c> is MIDI only), and <c>key</c> its own
///         key (null: the project's at its start), which Pitch Source transposes from.
///     </para>
///     <para>
///         <c>loopTicks</c>: it plays again and again, end to end, for that many ticks after its end (0: it does not
///         loop), the last repeat cut there, stopping where the next region on the track starts. Repeats are the region
///         again, fades included. <c>convertLoops</c> turns them into regions of their own; a looped region is not
///         split or joined. MIDI and pattern regions have the same.
///     </para>
///     <para>
///         <c>stretch</c>: how many times longer its audio lasts (1: as recorded; 0.25…4), scaling its base map whatever
///         its follow. A region stretched at all is warped; its file is untouched (<c>stretchRegion</c>).
///     </para>
/// </remarks>
public sealed record AudioRegion(
    string Id,
    string TrackId,
    string FileId,
    string Name,
    long StartTick,
    long FileOffset,
    long Length,
    double GainDb,
    long FadeIn,
    long FadeOut,
    double FadeInCurve,
    double FadeOutCurve,
    bool Muted,
    FlexFollow Follow,
    IReadOnlyList<FlexMarker> FlexMarkers,
    int Transpose,
    int FineTune,
    IReadOnlyList<PitchNote> PitchNotes,
    PitchSourceMode PitchSource,
    RegionKey? Key,
    long LoopTicks = 0,
    double Stretch = 1
);

/// <summary>
///     An audio file of the project; <c>path</c> is relative to the project folder. <c>tempo</c> is its File Tempo
///     (Smart Tempo), null until analysed. <c>version</c> (Phase 7) counts the Audio File Editor's operations on it: 0
///     is the file as imported or recorded; operation N wrote <c>path</c> as <c>&lt;name&gt;.vN.wav</c> beside it, and
///     undo points <c>path</c> and <c>version</c> back at the previous one. <c>channels</c> 0 is a file the project
///     lost its entry for: its regions kept it, <c>frames</c> is as far as they reach, and its format is not known until
///     it is relinked.
/// </summary>
public sealed record AudioFile(
    string Id,
    string Name,
    string Path,
    int SampleRate,
    int Channels,
    long Frames,
    int BitDepth,
    FileTempo? Tempo,
    int Version
);

/// <summary>
///     A new project. <c>template</c> names one of <c>GET /library/project-templates</c> (left out: none). Its
///     <c>sampleRate</c> is the one asked for, else a template's own, else the device's; a template holding audio takes
///     only its own.
/// </summary>
public sealed record CreateProjectRequest(string Name, string? Directory, int? SampleRate, string? Template = null);

public sealed record OpenProjectRequest(string Path);

/// <summary>
///     Where Save As or Save a Copy writes the package: a folder on the server's disk that does not exist yet
///     (<c>.anode</c> is added when left out). Its name is the new project's.
/// </summary>
public sealed record SaveProjectAsRequest(string Path);

/// <summary>
///     A track of a project on the server's disk, for the Import from Project browser: how much comes with it
///     (<c>regions</c> counts audio, MIDI, pattern and Session Player regions and take folders).
/// </summary>
public sealed record ProjectTrackInfo(
    string Id,
    string Name,
    TrackKind Kind,
    string Color,
    string? ParentId,
    int Regions,
    int Inserts,
    int Sends,
    int Lanes
);

/// <summary>What a project on the server's disk holds, read without opening it.</summary>
public sealed record ProjectContents(
    string Name,
    string Path,
    int SampleRate,
    double Tempo,
    IReadOnlyList<ProjectTrackInfo> Tracks
);

/// <summary>
///     Import from Project: <c>trackIds</c> of the package at <c>path</c> join the open project after its tracks, as one
///     undo step, with their stack members and, with sends, the auxes those reach. Left out, each part comes along:
///     <c>regions</c> (and take folders, alternatives and frozen files), <c>inserts</c>, <c>sends</c> and
///     <c>automation</c>. Their media is copied in, converted when the source is at another rate.
/// </summary>
public sealed record ImportProjectTracksRequest(
    string Path,
    IReadOnlyList<string> TrackIds,
    bool? Regions = null,
    bool? Inserts = null,
    bool? Sends = null,
    bool? Automation = null
);

/// <summary>What happens to the audio when a project's sample rate changes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SampleRateChangeMode>))]
public enum SampleRateChangeMode {
    /// <summary>
    ///     Every file is converted to a new file at the new rate, and frames scale with it: the project sounds the same,
    ///     at the same pitch and tempo.
    /// </summary>
    [JsonStringEnumMemberName("convert")]
    Convert,

    /// <summary>
    ///     Every file keeps its samples and is labelled with the new rate, and the tempo scales with it: every position
    ///     stays on its sample, and the whole project plays faster or slower, audio pitch with it.
    /// </summary>
    [JsonStringEnumMemberName("keepSamples")]
    KeepSamples
}

/// <summary>A change of the open project's sample rate.</summary>
public sealed record ChangeSampleRateRequest(int SampleRate, SampleRateChangeMode Mode);

/// <summary>
///     A project whose edits since its last save survive in an autosave, offered after a crash (docs/04-phase-3.md,
///     "Autosave and crash recovery"). <c>autosaved</c> is when they were last written.
/// </summary>
public sealed record RecoverableProject(string Id, string Name, string Path, DateTimeOffset Autosaved);

/// <summary>Import an audio file the server can read from its own disk (the desktop app, a headless box's library).</summary>
public sealed record ImportFileRequest(string Path);
