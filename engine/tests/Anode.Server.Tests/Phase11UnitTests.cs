using Anode.Contracts;
using Anode.Ipc.Telemetry;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 11's server pieces without a server (docs/12-phase-11.md): what a Session Player region's generation input
///     holds (only what lies over its range, with what sounds at its start), its rhythm source's onsets from MIDI and
///     from audio transients, the cache by input hash, and a monitor's telemetry ring read oldest first.
/// </summary>
public sealed class Phase11UnitTests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    static PitchName N(PitchStep step) => new(step, 0);

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, styles: new FakeSessionPlayers().Catalog).State;

    [Fact]
    public void A_region_s_input_holds_what_lies_over_its_range_and_what_sounds_at_its_start() {
        Apply(
            new AddSectionCommand("Intro", 0, 3840),
            new AddSectionCommand("Verse", 3840, 7680),
            new AddSectionCommand("Chorus", 11_520, 7680),
            new AddChordsCommand(
                [
                    new(0, N(PitchStep.C), ChordQualities.Major), new(1920, N(PitchStep.D), ChordQualities.Minor),
                    new(7680, N(PitchStep.G), ChordQualities.Major), new(15_360, N(PitchStep.F), ChordQualities.Major)
                ]
            ),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Keyboard, Regions: false)
        );
        Apply(new AddSessionPlayerRegionCommand(state.Tracks[0].Id, 3840, 7680));
        var input = SessionPlayers.Input(
            state,
            state.SessionPlayerRegions.Values.Single(),
            new Dictionary<string, FileAnalysis>()
        );
        Assert.Equal((SessionPlayerKind.Keyboard, 3840L, 7680L), (input.Player, input.StartTick, input.LengthTicks));
        // D minor sounds at its start; F starts after its end.
        Assert.Equal([1920L, 7680], input.Chords.Chords.Select(static c => c.Tick));
        // The section before it and the one it covers; not the chorus after it.
        Assert.Equal(["Intro", "Verse"], input.Sections.Select(static s => s.Name));
        Assert.Single(input.Signatures);
        Assert.Null(input.RhythmOnsets);
    }

    [Fact]
    public void A_rhythm_source_s_onsets_are_its_note_starts_and_its_audio_s_transients_over_the_range() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Audio));
        var (midi, audio) = (state.Tracks[0].Id, state.Tracks[1].Id);
        state = state.Apply(
            new AudioFileUpserted(new("aud_1", "Loop", "Audio Files/Loop.wav", 48_000, 1, 96_000, 24, null, 0))
        );
        Apply(new AddMidiRegionCommand(midi, 0, 3840), new AddRegionCommand(audio, "aud_1", 3840));
        Apply(
            new AddNotesCommand(
                state.MidiRegions.Keys.Single(),
                [new(40, 100, 1, 0, 240), new(40, 100, 1, 1920, 240), new(40, 100, 1, 4000, 240)]
            )
        );
        var empty = new Dictionary<string, FileAnalysis>();
        Assert.Equal([0L, 1920], SessionPlayers.Onsets(state, midi, 0, 3840, empty));
        Assert.Equal([1920L], SessionPlayers.Onsets(state, midi, 960, 3840, empty));
        // Transients at file frames 0 and 24 000: half a second (960 ticks at 120 BPM) apart, from the region's start.
        var analyses = new Dictionary<string, FileAnalysis> {
            ["aud_1"] = new("aud_1", 256, [0, 24_000], null, [], [], false)
        };
        Assert.Equal([3840L, 4800], SessionPlayers.Onsets(state, audio, 0, 7680, analyses));
        Assert.Empty(SessionPlayers.Onsets(state, audio, 0, 7680, empty));
    }

    [Fact]
    public void Regions_are_generated_once_per_input_and_a_failing_generator_gives_none() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass));
        var library = new FakeSessionPlayers();
        var players = new SessionPlayers(library, NullLogger<SessionPlayers>.Instance);
        var view = new OpenProjectView(
            1,
            "/tmp",
            state,
            Model.Compile.EngineIds.Empty.With(state),
            1,
            System.Collections.Immutable.ImmutableDictionary<string, SavedState>.Empty,
            System.Collections.Immutable.ImmutableDictionary<string, FileAnalysis>.Empty
        );
        var first = players.Played(view);
        Assert.Same(first.Values.Single(), players.Played(view).Values.Single());
        Assert.Equal((1, 1), (library.Generated, players.Generations));
        var reseeded = view with {
            State = state.Apply(
                new SessionPlayerRegionUpserted(state.SessionPlayerRegions.Values.Single() with { Seed = 99 })
            )
        };
        Assert.NotSame(first.Values.Single(), players.Played(reseeded).Values.Single());
        Assert.Equal(2, library.Generated);
        Assert.Empty(new SessionPlayers(new BuiltinSessionPlayers(), NullLogger<SessionPlayers>.Instance).Played(view));
    }

    [Fact]
    public unsafe void A_monitor_s_ring_reads_oldest_first() {
        var entry = new EnvironmentMonitorEntry { Object = 1, Count = 10 };
        for (var n = 0; n < 10; n++) {
            entry.Events[2 * (n % EnvironmentMonitorEntry.Capacity)] = n;
            entry.Events[2 * (n % EnvironmentMonitorEntry.Capacity) + 1] = 0x90 | (n << 8) | (100 << 16);
        }

        var events = EngineConnection.Monitored(entry);
        Assert.Equal(EnvironmentMonitorEntry.Capacity, events.Length);
        Assert.Equal([2L, 3, 4, 5, 6, 7, 8, 9], events.Select(static e => e.Sample));
        Assert.All(events, static e => Assert.Equal((0x90, 100), (e.Status, e.Data2)));
    }
}
