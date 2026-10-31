using Anode.Contracts;
using Anode.Server.Control;
using Anode.Server.Projects;

namespace Anode.Server.Tests;

/// <summary>The control service's pure parts: the fader law, timecode, and the settings' checks.</summary>
public sealed class ControlUnitTests {
    [Theory]
    [InlineData(-144d, 0d)]
    [InlineData(-60d, 0.02)]
    [InlineData(-6d, 0.6)]
    [InlineData(0d, 0.78)]
    [InlineData(6d, 1d)]
    public void The_fader_law_is_the_editors(double db, double position) {
        Assert.Equal(position, Taper.Position(db), 9);
        Assert.Equal(db, Taper.Db(position), 9);
    }

    [Fact]
    public void Positions_and_levels_round_trip() {
        for (var p = 0.01; p < 1; p += 0.01) {
            Assert.Equal(p, Taper.Position(Taper.Db(p)), 9);
        }

        Assert.Equal((0d, 1d), (Taper.Meter(0), Taper.Meter(1)));
        Assert.Equal(0.5, Taper.Meter(Math.Pow(10, -30 / 20.0)), 9);
        Assert.Equal(
            ("−∞ dB", "0.0 dB", "−6.0 dB", "+2.5 dB"),
            (Taper.FormatDb(-144), Taper.FormatDb(0), Taper.FormatDb(-6), Taper.FormatDb(2.5))
        );
        Assert.Equal(("L64", "C", "R63"), (Taper.FormatPan(-1), Taper.FormatPan(0), Taper.FormatPan(1)));
    }

    [Theory]
    [InlineData(MtcFrameRate.Fps25, 3661.5, 1, 1, 1, 12)]
    [InlineData(MtcFrameRate.Fps24, 59.99, 0, 0, 59, 23)]
    [InlineData(MtcFrameRate.Fps30, 86_400.0, 0, 0, 0, 0)]
    // 29.97 drop frame: frames 00 and 01 are skipped at each minute but every tenth.
    [InlineData(MtcFrameRate.Fps2997Drop, 60.06, 0, 1, 0, 2)]
    [InlineData(MtcFrameRate.Fps2997Drop, 600.0, 0, 10, 0, 0)]
    public void Timecode_counts_frames_at_the_projects_rate(
        MtcFrameRate rate,
        double seconds,
        int hours,
        int minutes,
        int secs,
        int frames
    ) =>
        Assert.Equal(new(hours, minutes, secs, frames), ControlService.Timecode(seconds, rate));

    [Fact]
    public void Assignments_are_checked_for_ids_inputs_targets_and_modes() {
        var target = new ControlTarget(ControlTargetKind.Parameter, null, "volume");

        ControlAssignment Assignment(string id, ControlInput input, ControlTarget? t = null) =>
            new(id, null, input, t ?? target, ControlValueMode.Absolute, 0, 1, false);

        static ControllerAssignments Zone(params ControlAssignment[] a) =>
            new([new("z", "Z", [new("m", "M", a)], "m")]);

        var cc = new ControlInput(null, ControlInputKind.ControlChange, null, 7);

        Assert.Same(ControllerAssignments.Empty, ControlValidation.Check(ControllerAssignments.Empty));
        ControlValidation.Check(Zone(Assignment("a", cc)));
        Assert.Throws<ProjectException>(() => ControlValidation.Check(Zone(Assignment("z", cc))));
        Assert.Throws<ProjectException>(() => ControlValidation.Check(Zone(Assignment("a", cc with { Channel = 17 }))));
        Assert.Throws<ProjectException>(() => ControlValidation.Check(Zone(Assignment("a", cc with { Number = 128 }))));
        Assert.Throws<ProjectException>(() => ControlValidation.Check(
                Zone(Assignment("a", cc with { Kind = ControlInputKind.ControlChange14, Number = 32 }))
            )
        );
        Assert.Throws<ProjectException>(() => ControlValidation.Check(
                Zone(Assignment("a", cc, new(ControlTargetKind.Transport)))
            )
        );
        Assert.Throws<ProjectException>(() => ControlValidation.Check(
                Zone(Assignment("a", cc, new(ControlTargetKind.ZoneMode, ZoneId: "z", ModeId: "other")))
            )
        );
        ControlValidation.Check(Zone(Assignment("a", cc, new(ControlTargetKind.ZoneMode, ZoneId: "z", ModeId: "m"))));
        Assert.Throws<ProjectException>(() => ControlValidation.Check(Zone(Assignment("a", cc) with { Min = -0.1 })));
        Assert.Throws<ProjectException>(static () => ControlValidation.Check(
                new ControllerAssignments([new("z", "Z", [new("m", "M", [])], "gone")])
            )
        );
    }

    [Fact]
    public void Osc_settings_are_checked_and_clients_tidied() {
        var settings = ControlValidation.Check(new OscSettings(true, 9000, [new(" phone ", 9001), new("phone", 9001)]));
        Assert.Equal([new OscClient("phone", 9001)], settings.Clients);
        Assert.Throws<ProjectException>(static () => ControlValidation.Check(new OscSettings(true, 70_000, [])));
        Assert.Throws<ProjectException>(static () =>
            ControlValidation.Check(new OscSettings(true, 9000, [new(string.Empty, 1)]))
        );
    }

    [Fact]
    public void Sync_settings_map_onto_the_hosts_terms() {
        var message = Engine.SyncService.Message(
            new(SyncSource.Mtc, [new("out", false, true, -2)], MtcFrameRate.Fps2997Drop, 10, true, null, "in")
        );
        Assert.Equal(
            (Ipc.EngineSyncSource.Mtc, Ipc.EngineMtcRate.Fps2997Drop, 10d, true, (string?)null, "in"),
            (message.Source, message.FrameRate, message.StartOffsetSeconds, message.LinkStartStop, message.ClockInPort,
                message.MtcInPort)
        );
        Assert.Equal([new Ipc.EngineSyncDestination("out", false, true, -2)], message.Destinations);
    }
}
