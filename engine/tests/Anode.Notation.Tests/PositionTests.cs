using Anode.Contracts;
using CsCheck;
using static Anode.Notation.Tests.TestProject;

namespace Anode.Notation.Tests;

/// <summary>Staff positions ↔ pitch, keys and transposition, the display grid, and the factory staff styles.</summary>
public sealed class PositionTests {
    static readonly KeyEvent C = new(0, 0, KeyMode.Major);
    static readonly KeyEvent G = new(0, 7, KeyMode.Major);
    static readonly KeyEvent F = new(0, 5, KeyMode.Major);

    [Theory]
    [InlineData(Clef.Treble, 0, 64)]
    [InlineData(Clef.Treble, 8, 77)]
    [InlineData(Clef.Treble, -2, 60)]
    [InlineData(Clef.Bass, 0, 43)]
    [InlineData(Clef.Bass, 10, 60)]
    [InlineData(Clef.Alto, 4, 60)]
    [InlineData(Clef.Tenor, 0, 50)]
    [InlineData(Clef.Treble8vb, 0, 52)]
    [InlineData(Clef.Treble8va, 0, 76)]
    [InlineData(Clef.Bass8vb, 0, 31)]
    [InlineData(Clef.Percussion, 0, 64)]
    public void A_staff_position_is_the_key_s_note_on_the_clef(Clef clef, int position, int pitch) =>
        Assert.Equal(pitch, StaffPositions.PitchAt(clef, 0, C, position));

    [Fact]
    public void The_key_and_an_accidental_alter_the_note_at_a_position() {
        Assert.Equal(66, StaffPositions.PitchAt(Clef.Treble, 0, G, 1));
        Assert.Equal(65, StaffPositions.PitchAt(Clef.Treble, 0, G, 1, Accidental.Natural));
        Assert.Equal(70, StaffPositions.PitchAt(Clef.Treble, 0, F, 4));
        Assert.Equal(73, StaffPositions.PitchAt(Clef.Treble, 0, C, 5, Accidental.Sharp));
        Assert.Equal(74, StaffPositions.PitchAt(Clef.Treble, 0, C, 5, Accidental.DoubleSharp));
    }

    [Fact]
    public void A_transposing_staff_reads_its_written_key() {
        // B♭ clarinet in concert C: written D major; the F line is F♯, which sounds E.
        Assert.Equal(64, StaffPositions.PitchAt(Clef.Treble, 2, C, 1));
        Assert.Equal(new StaffPlacement(-1, new(PitchStep.D, 0, 4)), StaffPositions.PlacementOf(Clef.Treble, 2, C, 60));
        Assert.Equal(new StaffPlacement(-2, new(PitchStep.C, 1, 4)), StaffPositions.PlacementOf(Clef.Treble, 0, C, 61));
        Assert.Equal(new StaffPlacement(-1, new(PitchStep.D, -1, 4)), StaffPositions.PlacementOf(Clef.Treble, 0, F, 61));
        Assert.Equal(new StaffPlacement(-7, new(PitchStep.E, 0, 2)), StaffPositions.PlacementOf(Clef.Treble8vb, 0, C, 40));
    }

    [Fact]
    public void A_step_moves_to_the_next_note_of_the_key() {
        Assert.Equal(65, StaffPositions.Step(64, 1, 0, C));
        Assert.Equal(67, StaffPositions.Step(66, 1, 0, C));
        Assert.Equal(59, StaffPositions.Step(60, -1, 0, C));
        Assert.Equal(72, StaffPositions.Step(60, 7, 0, C));
        Assert.Equal(66, StaffPositions.Step(64, 1, 0, G));
        Assert.Equal(64, StaffPositions.Step(64, 1, 0, C, Accidental.Flat));
        Assert.Equal(62, StaffPositions.Step(60, 1, 2, C));
        Assert.Equal(60, StaffPositions.Step(60, 0, 0, C));
    }

    [Fact]
    public void Keys_have_conventional_signatures() {
        Assert.Equal(new ScoreKey(6, KeyMode.Major), StaffPositions.KeyOf(new(0, 6, KeyMode.Major)));
        Assert.Equal(new ScoreKey(-5, KeyMode.Major), StaffPositions.KeyOf(new(0, 1, KeyMode.Major)));
        Assert.Equal(new ScoreKey(-6, KeyMode.Minor), StaffPositions.KeyOf(new(0, 3, KeyMode.Minor)));
        Assert.Equal(new ScoreKey(5, KeyMode.Minor), StaffPositions.KeyOf(new(0, 8, KeyMode.Minor)));
        Assert.Equal(new ScoreKey(-5, KeyMode.Minor), StaffPositions.KeyOf(new(0, 10, KeyMode.Minor)));
        Assert.Equal(new ScoreKey(0, KeyMode.Minor), StaffPositions.KeyOf(new(0, 9, KeyMode.Minor)));
        Assert.Equal(new ScoreKey(2, KeyMode.Major), StaffPositions.WrittenKey(new(0, KeyMode.Major), 2));
        Assert.Equal(new ScoreKey(-5, KeyMode.Major), StaffPositions.WrittenKey(new(5, KeyMode.Major), 2));
        Assert.Equal(new ScoreKey(3, KeyMode.Major), StaffPositions.WrittenKey(new(0, KeyMode.Major), 9));
        Assert.Equal(new ScoreKey(1, KeyMode.Major), StaffPositions.WrittenKey(new(0, KeyMode.Major), 7));
        Assert.Equal([1, 4, 5, 8, -1], new[] { 2, 7, 9, 14, -2 }.Select(StaffPositions.DiatonicSteps));
    }

    [Fact]
    public void A_drum_staff_position_gives_the_drum_map_s_pitch() {
        Assert.Equal(42, StaffPositions.DrumPitchAt(FactoryStaffStyles.GeneralMidiDrums, 9));
        Assert.Equal(36, StaffPositions.DrumPitchAt(FactoryStaffStyles.GeneralMidiDrums, 1));
        Assert.Null(StaffPositions.DrumPitchAt(FactoryStaffStyles.GeneralMidiDrums, 20));
    }

    [Fact]
    public void Placement_and_pitch_round_trip_on_every_clef_key_and_transposition() =>
        Gen.Select(
                Gen.Enum<Clef>().Where(c => c != Clef.Percussion),
                Gen.Int[-7, 14],
                Gen.Int[0, 11],
                Gen.Bool,
                Gen.Int[21, 108]
            )
            .Sample((clef, transpose, tonic, minor, pitch) => {
                var key = new KeyEvent(0, tonic, minor ? KeyMode.Minor : KeyMode.Major);
                var placement = StaffPositions.PlacementOf(clef, transpose, key, pitch);
                var accidental = placement.Written.Alter switch {
                    1 => Accidental.Sharp,
                    -1 => Accidental.Flat,
                    2 => Accidental.DoubleSharp,
                    -2 => Accidental.DoubleFlat,
                    _ => Accidental.Natural
                };
                return StaffPositions.PitchAt(clef, transpose, key, placement.StaffPosition, accidental) == pitch
                    && StaffPositions.Step(pitch, 0, transpose, key, accidental) == pitch
                    && Math.Abs(placement.Written.Alter) <= 2;
            });

    [Fact]
    public void Grids_come_from_the_region_or_the_style_and_are_drawable() {
        var piano = FactoryStaffStyles.Find(StaffStyleIds.Piano)!;
        Assert.Equal((240, 160), DisplayGrid.GridsOf(RegionNotation.Default, piano));
        Assert.Equal((480, 320), DisplayGrid.GridsOf(Grid(480, 320), piano));
        Assert.Equal((480, 0), DisplayGrid.GridsOf(Grid(480), piano));
        Assert.Equal((60, 80), DisplayGrid.GridsOf(Grid(100, 100), piano));
        Assert.Equal((30, 0), DisplayGrid.GridsOf(RegionNotation.Default, piano with { DisplayQuantizeTicks = 0, TripletQuantizeTicks = 0 }));
        Assert.Equal((3840, 0), DisplayGrid.GridsOf(Grid(9999), piano));
    }

    [Fact]
    public void Display_ticks_and_moves_follow_the_display_grid() {
        var project = new TestProject();
        var track = project.Track("T");
        var id = project.Region(track, 0, 3840 * 2, Grid(240, 160), N(1, 60, 250, 200), N(2, 62, 965, 300), N(3, 64, 1290, 300), N(4, 65, 1595, 300));
        var region = project.State.MidiRegions[id];
        var style = FactoryStaffStyles.Find(StaffStyleIds.Piano)!;
        Assert.Equal(240, DisplayGrid.DisplayTick(project.State, region, style, 250));
        Assert.Equal(1280, DisplayGrid.DisplayTick(project.State, region, style, 1290));
        Assert.Equal(490, DisplayGrid.Move(project.State, region, style, 250, 1));
        Assert.Equal(10, DisplayGrid.Move(project.State, region, style, 250, -1));
        Assert.Equal(10, DisplayGrid.Move(project.State, region, style, 250, -5));
        Assert.Equal(1450, DisplayGrid.Move(project.State, region, style, 1290, 1));
        Assert.Equal(1930, DisplayGrid.Move(project.State, region, style, 1290, 4));
        Assert.Equal(3610, DisplayGrid.Move(project.State, region, style, 250, 16));
        Assert.Equal(250, DisplayGrid.Move(project.State, region, style, 3610, -16));
    }

    [Fact]
    public void The_factory_styles_are_the_contract_s_in_library_order() {
        Assert.Equal(StaffStyleIds.All, FactoryStaffStyles.All.Select(s => s.Id));
        Assert.All(FactoryStaffStyles.All, s => Assert.Same(s, FactoryStaffStyles.Find(s.Id)));
        Assert.Null(FactoryStaffStyles.Find("factory.none"));
        var piano = FactoryStaffStyles.Find(StaffStyleIds.Piano)!;
        Assert.True(piano.Brace);
        Assert.Equal([60, null], piano.Staves.Select(s => s.SplitPitch));
        Assert.Equal([Clef.Treble, Clef.Bass], piano.Staves.Select(s => s.Clef));
        Assert.Equal(
            [0, 0, 0, 0, 0, 0, 2, 2, 7, 9, 14, 0, 0],
            FactoryStaffStyles.All.Select(s => s.Staves[0].Transpose)
        );
        Assert.Equal(Clef.Treble8vb, FactoryStaffStyles.Find(StaffStyleIds.Guitar)!.Staves[0].Clef);
        Assert.Equal(Clef.Bass8vb, FactoryStaffStyles.Find(StaffStyleIds.BassGuitar)!.Staves[0].Clef);
        Assert.Equal(Clef.Alto, FactoryStaffStyles.Find(StaffStyleIds.Viola)!.Staves[0].Clef);
        Assert.Equal(Clef.Bass, FactoryStaffStyles.Find(StaffStyleIds.Cello)!.Staves[0].Clef);
        Assert.Equal(Clef.Percussion, FactoryStaffStyles.Find(StaffStyleIds.Drums)!.Staves[0].Clef);
        Assert.NotEmpty(FactoryStaffStyles.Find(StaffStyleIds.Drums)!.DrumMap!);
        Assert.All(FactoryStaffStyles.All, s => Assert.Null(s.Staves[^1].SplitPitch));
        Assert.All(FactoryStaffStyles.All, s => Assert.StartsWith(StaffStyleIds.FactoryPrefix, s.Id, StringComparison.Ordinal));
        Assert.Same(piano, FactoryStaffStyles.Resolve(ProjectScore.Empty, null));
        Assert.Same(piano, FactoryStaffStyles.Resolve(ProjectScore.Empty, "sts_missing"));
    }
}
