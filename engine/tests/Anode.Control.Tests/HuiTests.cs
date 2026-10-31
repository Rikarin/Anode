using Anode.Contracts;
using Anode.Control.Surfaces;
using static Anode.Control.Tests.Views;

namespace Anode.Control.Tests;

/// <summary>HUI, byte by byte against the protocol (docs/control/README.md).</summary>
public sealed class HuiTests {
    const long Second = 1_000_000_000;

    static (Hui Surface, ProjectView View) Connected(int tracks = 16) {
        var surface = new Hui();
        var view = Tracks(tracks);
        surface.Decode([0x90, 0x00, 0x7F], 0);
        surface.Encode(view);
        return (surface, view);
    }

    static SurfaceAction One(Hui surface, string hex) => Assert.Single(surface.Decode(Bytes(hex), 0));

    [Fact]
    public void The_host_pings_every_second_and_a_silent_unit_goes_offline() {
        var surface = new Hui();
        Assert.Equal(["90 00 00"], Hex(surface.Poll(0)));
        Assert.False(surface.Connected);
        Assert.Empty(surface.Decode(Bytes("90 00 7F"), Second / 10));
        Assert.True(surface.Connected);
        Assert.Empty(surface.Poll(Second / 2));
        Assert.Equal(["90 00 00"], Hex(surface.Poll(Second)));
        surface.Decode(Bytes("90 00 7F"), Second + 1);
        Assert.Equal(["90 00 00"], Hex(surface.Poll(3 * Second)));
        Assert.True(surface.Connected);
        Assert.Equal(["90 00 00"], Hex(surface.Poll(4 * Second + 2)));
        Assert.False(surface.Connected);
    }

    [Fact]
    public void A_unit_that_pings_is_answered() {
        var surface = new Hui();
        surface.Decode(Bytes("90 00 00"), 0);
        Assert.Equal(["90 00 7F", "90 00 00"], Hex(surface.Poll(0)));
    }

    [Fact]
    public void Faders_are_14_bit_from_MSB_then_LSB() {
        var (surface, _) = Connected();
        Assert.Empty(surface.Decode(Bytes("B0 00 40"), 0));
        Assert.Equal(new(SurfaceActionKind.SetParameter, 0, "t1", "volume", 8192 / 16383.0), One(surface, "B0 20 00"));
        Assert.Equal(new(SurfaceActionKind.SetParameter, 1, "t2", "volume", 1), One(surface, "B0 01 7F 21 7F"));
    }

    [Fact]
    public void Switches_arrive_as_a_zone_then_a_port() {
        var (surface, _) = Connected();
        Assert.Equal(new(SurfaceActionKind.Touch, 0, "t1", "volume", 1), One(surface, "B0 0F 00 B0 2F 40"));
        Assert.Equal(new(SurfaceActionKind.Touch, 0, "t1", "volume", 0), One(surface, "B0 0F 00 2F 00"));
        Assert.Equal(new(SurfaceActionKind.Select, 1, "t2", Value: 1), One(surface, "B0 0F 01 2F 41"));
        Assert.Equal(new(SurfaceActionKind.Mute, 2, "t3", Value: 0), One(surface, "B0 0F 02 2F 42"));
        Assert.Equal(new(SurfaceActionKind.Solo, 0, "t1", Value: 1), One(surface, "B0 0F 00 2F 43"));
        Assert.Equal(new(SurfaceActionKind.RecordArm, 0, "t1", Value: 1), One(surface, "B0 0F 00 2F 47"));
        Assert.Empty(surface.Decode(Bytes("B0 0F 00 2F 07"), 0)); // A release.
        Assert.Equal(SurfaceActionKind.Play, One(surface, "B0 0F 0E 2F 44").Kind);
        Assert.Equal(SurfaceActionKind.Stop, One(surface, "B0 0F 0E 2F 43").Kind);
        Assert.Equal(SurfaceActionKind.Record, One(surface, "B0 0F 0E 2F 45").Kind);
        Assert.Equal(new(SurfaceActionKind.Rewind, Value: 1), One(surface, "B0 0F 0E 2F 41"));
        Assert.Equal(new(SurfaceActionKind.Rewind, Value: 0), One(surface, "B0 0F 0E 2F 01"));
        Assert.Equal(new(SurfaceActionKind.Forward, Value: 1), One(surface, "B0 0F 0E 2F 42"));
        Assert.Equal(SurfaceActionKind.Cycle, One(surface, "B0 0F 0F 2F 43").Kind);
        Assert.Equal(SurfaceActionKind.CursorUp, One(surface, "B0 0F 0D 2F 44").Kind);
        Assert.Equal(new(SurfaceActionKind.Zoom, Value: 1), One(surface, "B0 0F 0D 2F 42"));
        Assert.Equal(new(SurfaceActionKind.Jog, Value: -2), One(surface, "B0 0D 02"));
        Assert.Equal(
            new(SurfaceActionKind.SetAutomationMode, TrackId: "t1", Automation: AutomationMode.Touch),
            One(surface, "B0 0F 18 2F 45")
        );
    }

    [Fact]
    public void Banking_moves_by_8_and_1() {
        var (surface, _) = Connected();
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 8), One(surface, "B0 0F 0A 2F 43"));
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 9), One(surface, "B0 0F 0A 2F 42"));
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 1), One(surface, "B0 0F 0A 2F 41"));
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 0), One(surface, "B0 0F 0A 2F 40"));
        Assert.Equal("t1", One(surface, "B0 00 00 20 00").TrackId);
    }

    [Fact]
    public void VPots_step_pan_or_a_send() {
        var (surface, view) = Connected();
        Assert.Equal(new(SurfaceActionKind.StepParameter, 0, "t1", "pan", 1), One(surface, "B0 40 41"));
        Assert.Equal(new(SurfaceActionKind.StepParameter, 0, "t1", "pan", -3), One(surface, "B0 40 03"));
        Assert.Equal(new(SurfaceActionKind.SetParameter, 0, "t1", "pan", 0.5), One(surface, "B0 0F 00 2F 45"));
        Assert.Empty(surface.Decode(Bytes("B0 0F 0B 2F 46"), 0)); // Send B.
        Assert.Equal("send:s2", One(surface, "B0 40 41").Address);
        surface.Decode(Bytes("B0 0F 0B 2F 47"), 0); // Send A.
        Assert.Equal(VPotMode.Send, surface.Mode);
        Assert.Equal("send:s1", One(surface, "B0 40 41").Address);
        var output = Hex(surface.Encode(view));
        Assert.Contains("B0 10 26", output);
        Assert.Contains("B0 0C 0B B0 2C 47", output);
        Assert.Contains("B0 0C 0B B0 2C 02", output);
    }

    [Fact]
    public void A_full_refresh_sends_names_faders_rings_LEDs_and_the_timecode() {
        var output = Hex(new Hui().Encode(Tracks(16)));
        Assert.Equal("F0 00 00 66 05 00 10 00 " + Hex([.."Trc1"u8]) + " F7", output[0]);
        Assert.Equal("F0 00 00 66 05 00 10 07 " + Hex([.."Trc8"u8]) + " F7", output[7]);
        Assert.Equal(
            [
                "B0 00 10 B0 20 00", "B0 10 04", "B0 0C 00 B0 2C 41", "B0 0C 00 B0 2C 02", "B0 0C 00 B0 2C 03",
                "B0 0C 00 B0 2C 07"
            ],
            output[8..14]
        );
        Assert.Contains("B0 0C 02 B0 2C 42", output); // Track 3 muted.
        Assert.Contains("B0 0C 0E B0 2C 43", output); // Stopped.
        Assert.Contains("B0 0C 18 B0 2C 42", output); // Read.
        Assert.Equal("F0 00 00 66 05 00 11 00 00 00 01 00 00 00 00 F7", output[^1]);
    }

    [Fact]
    public void Only_changes_are_sent_and_a_touched_fader_is_left_alone() {
        var (surface, view) = Connected();
        Assert.Empty(surface.Encode(view));
        surface.Decode(Bytes("B0 0F 00 2F 40"), 0);
        var moved = With(view, 0, t => t with { Fader = 1, Solo = true });
        Assert.Equal(["B0 0C 00 B0 2C 43"], Hex(surface.Encode(moved)));
        surface.Decode(Bytes("B0 0F 00 2F 00"), 0);
        Assert.Equal(["B0 00 7F B0 20 7F"], Hex(surface.Encode(moved)));
    }

    [Fact]
    public void Meters_are_poly_pressure_on_both_sides() {
        var (surface, view) = Connected();
        var output = Hex(surface.EncodeMeters(Playing(view)));
        Assert.Equal("A0 00 01 A0 00 11", output[0]);
        Assert.Equal("A0 07 0A A0 07 1A", output[7]);
        Assert.Equal("A0 00 00 A0 00 10", Hex(surface.EncodeMeters(view))[0]);
    }

    [Fact]
    public void Group_banking_works_for_HUI_too() {
        var group = new SurfaceGroup([new Hui()]);
        group.Encode(Tracks(16));
        Assert.Equal(
            [new SurfaceAction(SurfaceActionKind.Banked, Value: 8)],
            group.Decode(0, Bytes("B0 0F 0A 2F 43"), 0)
        );
        Assert.Equal(8, group.Units[0].BankOffset);
    }
}
