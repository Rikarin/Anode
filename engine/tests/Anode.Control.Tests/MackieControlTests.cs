using Anode.Contracts;
using Anode.Control.Surfaces;
using static Anode.Control.Tests.Views;

namespace Anode.Control.Tests;

/// <summary>Mackie Control and XT, byte by byte against the protocol (docs/control/README.md).</summary>
public sealed class MackieControlTests {
    static (MackieControl Surface, ProjectView View) Connected(int tracks = 16) {
        var surface = new MackieControl(false);
        var view = Tracks(tracks);
        surface.Decode(Bytes("F0 00 00 66 14 03 00 00 00 00 00 00 00 F7"), 0);
        surface.Encode(view);
        return (surface, view);
    }

    static string Scribble(IReadOnlyList<byte[]> output) =>
        Hex(output.First(static m => m is [0xF0, _, _, _, _, 0x12, ..]));

    static SurfaceAction One(MackieControl surface, string hex) => Assert.Single(surface.Decode(Bytes(hex), 0));

    [Fact]
    public void The_handshake_answers_the_challenge_with_the_published_response() {
        var surface = new MackieControl(false);
        Assert.False(surface.Connected);
        Assert.Empty(surface.Decode(Bytes("F0 00 00 66 14 01 41 42 43 44 45 46 47 01 02 03 04 F7"), 0));
        Assert.True(surface.Connected);
        Assert.Equal(["F0 00 00 66 14 02 41 42 43 44 45 46 47 05 05 7B 2F F7"], Hex(surface.Poll(0)));
        Assert.Empty(surface.Poll(10_000_000_000));
        surface.Decode(Bytes("F0 00 00 66 14 04 41 42 43 44 45 46 47 F7"), 0);
        Assert.False(surface.Connected);
    }

    [Fact]
    public void Until_it_answers_the_device_is_queried_every_two_seconds() {
        var surface = new MackieControl(false);
        Assert.Equal(["F0 00 00 66 14 00 F7"], Hex(surface.Poll(0)));
        Assert.Empty(surface.Poll(1_900_000_000));
        Assert.Equal(["F0 00 00 66 14 00 F7"], Hex(surface.Poll(2_000_000_000)));
        surface.Decode([0x90, 0x5E, 0x7F], 0); // Any input connects.
        Assert.True(surface.Connected);
        Assert.Empty(surface.Poll(10_000_000_000));
        Assert.Equal(["F0 00 00 66 15 00 F7"], Hex(new MackieControl(true).Poll(0)));
    }

    [Fact]
    public void Faders_arrive_as_14_bit_volumes_of_their_tracks() {
        var (surface, _) = Connected();
        Assert.Equal(new(SurfaceActionKind.SetParameter, 0, "t1", "volume", 8192 / 16383.0), One(surface, "E0 00 40"));
        Assert.Equal(new(SurfaceActionKind.SetParameter, 7, "t8", "volume", 1), One(surface, "E7 7F 7F"));
        Assert.Empty(surface.Decode(Bytes("E8 7F 7F"), 0)); // The master fader.
    }

    [Fact]
    public void A_full_refresh_sends_meter_modes_the_scribble_strip_faders_rings_and_LEDs() {
        var surface = new MackieControl(false);
        var output = Hex(surface.Encode(Tracks(16)));
        Assert.Equal("F0 00 00 66 14 20 00 01 F7", output[0]);
        Assert.Equal("F0 00 00 66 14 20 07 01 F7", output[7]);
        var top = "Track1 Track2 Track3 Track4 Track5 Track6 Track7 Track8 ";
        var bottom = string.Concat(
            new[] { "L50", "C", "R50", "R100", "L100", "L50", "C", "R50" }.Select(static v => v.PadLeft(6) + " ")
        );
        Assert.Equal("F0 00 00 66 14 12 00 " + Hex([..(top + bottom).Select(static c => (byte)c)]) + " F7", output[8]);

        // Strip 1: fader 1/8 (2048), pan −0.5 as a dot at 1 + round(0.25 × 10) = 4 (half away from zero), LEDs.
        Assert.Equal(["E0 00 10", "B0 30 04", "90 00 00", "90 08 00", "90 10 00", "90 18 7F"], output[9..15]);

        // Strip 3 is muted, 4 soloed, 5 armed.
        Assert.Contains("90 12 7F", output);
        Assert.Contains("90 0B 7F", output);
        Assert.Contains("90 04 7F", output);

        // Pan mode, read automation, stopped, SMPTE, then the assignment display "PN" and the timecode 00:00:10:00.
        Assert.Contains("90 2A 7F", output);
        Assert.Contains("90 4A 7F", output);
        Assert.Contains("90 5D 7F", output);
        Assert.Contains("90 5E 00", output);
        Assert.Contains("90 71 7F", output);
        Assert.Equal(
            [
                "B0 4B 10", "B0 4A 0E", "B0 49 20", "B0 48 20", "B0 47 70", "B0 46 30", "B0 45 70", "B0 44 31",
                "B0 43 70",
                "B0 42 20", "B0 41 30", "B0 40 30"
            ],
            output[^12..]
        );
    }

    [Fact]
    public void Only_changes_are_sent_and_invalidate_sends_everything_again() {
        var (surface, view) = Connected();
        Assert.Empty(surface.Encode(view));
        var muted = With(view, 1, t => t with { Mute = true, Name = "Bass" });
        Assert.Equal(["F0 00 00 66 14 12 07 42 61 73 73 20 20 F7", "90 11 7F"], Hex(surface.Encode(muted)));
        surface.Invalidate();
        Assert.True(surface.Encode(muted).Count > 50);
    }

    [Fact]
    public void A_touched_fader_receives_no_updates_until_released() {
        var (surface, view) = Connected();
        Assert.Equal(new(SurfaceActionKind.Touch, 0, "t1", "volume", 1), One(surface, "90 68 7F"));
        Assert.Equal(SurfaceActionKind.SetParameter, One(surface, "E0 00 40").Kind);
        var moved = With(view, 0, t => t with { Fader = 0.75 });
        Assert.Empty(surface.Encode(moved));
        Assert.Equal(new(SurfaceActionKind.Touch, 0, "t1", "volume", 0), One(surface, "90 68 00"));
        Assert.Equal(["E0 7F 5F"], Hex(surface.Encode(moved))); // round(0.75 × 16383) = 12287.
    }

    [Fact]
    public void A_fader_move_is_not_echoed_back() {
        var (surface, view) = Connected();
        surface.Decode(Bytes("E0 00 40"), 0);
        Assert.Empty(surface.Encode(With(view, 0, t => t with { Fader = 8192 / 16383.0 })));
    }

    [Fact]
    public void Transport_jog_cursor_and_zoom() {
        var (surface, _) = Connected();
        Assert.Equal(SurfaceActionKind.Play, One(surface, "90 5E 7F").Kind);
        Assert.Empty(surface.Decode(Bytes("90 5E 00"), 0));
        Assert.Equal(SurfaceActionKind.Stop, One(surface, "90 5D 7F").Kind);
        Assert.Equal(SurfaceActionKind.Record, One(surface, "90 5F 7F").Kind);
        Assert.Equal(SurfaceActionKind.Cycle, One(surface, "90 56 7F").Kind);
        Assert.Equal(new(SurfaceActionKind.Rewind, Value: 1), One(surface, "90 5B 7F"));
        Assert.Equal(new(SurfaceActionKind.Rewind, Value: 0), One(surface, "90 5B 00"));
        Assert.Equal(new(SurfaceActionKind.Forward, Value: 0), One(surface, "80 5C 00"));
        Assert.Equal(new(SurfaceActionKind.Jog, Value: -1), One(surface, "B0 3C 41"));
        Assert.Equal(new(SurfaceActionKind.Jog, Value: 3), One(surface, "B0 3C 03"));
        Assert.Equal(SurfaceActionKind.CursorLeft, One(surface, "90 62 7F").Kind);
        Assert.Equal(new(SurfaceActionKind.Zoom, Value: 1), One(surface, "90 64 7F"));
        Assert.Equal(["90 64 7F"], Hex(surface.Encode(Tracks(16))));
        Assert.Equal(new(SurfaceActionKind.Zoom, Value: 0), One(surface, "90 64 7F"));
    }

    [Fact]
    public void Strip_buttons_switch_their_tracks() {
        var (surface, _) = Connected();
        Assert.Equal(new(SurfaceActionKind.Mute, 2, "t3", Value: 0), One(surface, "90 12 7F")); // Track 3 is muted.
        Assert.Equal(new(SurfaceActionKind.Solo, 0, "t1", Value: 1), One(surface, "90 08 7F"));
        Assert.Equal(new(SurfaceActionKind.RecordArm, 4, "t5", Value: 0), One(surface, "90 04 7F"));
        Assert.Equal(new(SurfaceActionKind.Select, 1, "t2", Value: 1), One(surface, "90 19 7F"));
    }

    [Fact]
    public void Automation_buttons_set_the_selected_tracks_mode() {
        var (surface, _) = Connected();
        Assert.Equal(
            new(SurfaceActionKind.SetAutomationMode, TrackId: "t1", Automation: AutomationMode.Touch),
            One(surface, "90 4D 7F")
        );
        Assert.Equal(AutomationMode.Latch, One(surface, "90 4E 7F").Automation);
        Assert.Equal(AutomationMode.Write, One(surface, "90 4B 7F").Automation);
        Assert.Equal(AutomationMode.Off, One(surface, "90 4A 7F").Automation); // Read/Off on a track in read.
    }

    [Fact]
    public void VPots_step_pan_then_sends() {
        var (surface, view) = Connected();
        Assert.Equal(new(SurfaceActionKind.StepParameter, 0, "t1", "pan", -1), One(surface, "B0 10 41"));
        Assert.Equal(new(SurfaceActionKind.SetParameter, 0, "t1", "pan", 0.5), One(surface, "90 20 7F"));
        Assert.Empty(surface.Decode(Bytes("90 29 7F"), 0)); // Send mode: send 1.
        Assert.Equal(new(SurfaceActionKind.StepParameter, 0, "t1", "send:s1", 2), One(surface, "B0 10 02"));
        Assert.Empty(surface.Decode(Bytes("B0 11 02"), 0)); // Track 2 has no send.
        var output = Hex(surface.Encode(view));
        Assert.Contains("B0 30 26", output); // Send 1 of track 1 at 0.5: a fill to round(5.5) = 6.
        Assert.Contains("B0 4A 31", output); // "S1".
        surface.Decode(Bytes("90 29 7F"), 0); // Send 2.
        Assert.Equal("send:s2", One(surface, "B0 10 01").Address);
        surface.Decode(Bytes("90 2A 7F"), 0);
        Assert.Equal(VPotMode.Pan, surface.Mode);
    }

    [Fact]
    public void Name_value_shows_the_parameter_name_on_the_top_line() {
        var (surface, view) = Connected();
        surface.Decode(Bytes("90 34 7F"), 0);
        var output = Hex(surface.Encode(view));
        var top = string.Concat(Enumerable.Repeat("Pan    ", 8));
        Assert.Equal("F0 00 00 66 14 12 00 " + Hex([..top[..55].Select(static c => (byte)c)]) + " F7", output[0]);
    }

    [Fact]
    public void Plug_in_mode_chooses_an_insert_and_pages_its_parameters_by_8() {
        var (surface, view) = Connected();
        surface.Decode(Bytes("90 2B 7F"), 0);
        Assert.Equal(VPotMode.PlugIn, surface.Mode);
        Assert.Empty(surface.Decode(Bytes("B0 10 01"), 0)); // Choosing: the VPots do nothing.
        surface.Encode(view);
        Assert.Empty(surface.Decode(Bytes("90 20 7F"), 0)); // Choose "Channel EQ".
        Assert.Equal(new(SurfaceActionKind.StepParameter, 0, "t1", "insert:eq:p1", 1), One(surface, "B0 10 01"));
        Assert.Equal("insert:eq:p8", One(surface, "B0 17 01").Address);
        var output = Hex(surface.Encode(view));
        Assert.StartsWith("F0 00 00 66 14 12 00 " + Hex([.."Band 1 Band 2"u8]), output[0]);

        Assert.Empty(surface.Decode(Bytes("90 2F 7F"), 0)); // Bank right: the next page, not the next tracks.
        Assert.Equal(0, surface.BankOffset);
        Assert.Equal("insert:eq:p9", One(surface, "B0 10 01").Address);
        Assert.Equal("insert:eq:p10", One(surface, "B0 11 01").Address);
        Assert.Empty(surface.Decode(Bytes("B0 12 01"), 0));
        surface.Decode(Bytes("90 30 7F"), 0); // Channel left: back one.
        Assert.Equal("insert:eq:p8", One(surface, "B0 10 01").Address);

        // A new selection goes back to choosing.
        var other = With(With(view, 0, t => t with { Selected = false }), 1, t => t with { Selected = true });
        surface.Encode(other);
        Assert.Empty(surface.Decode(Bytes("B0 10 01"), 0));
    }

    [Fact]
    public void Meters_send_levels_and_fall_a_step_per_call() {
        var (surface, view) = Connected();
        var playing = Playing(view);
        Assert.Equal(
            ["D0 01", "D0 12", "D0 24", "D0 35", "D0 46", "D0 57", "D0 68", "D0 7A"],
            Hex(surface.EncodeMeters(playing))
        );
        Assert.Equal(
            ["D0 00", "D0 11", "D0 23", "D0 34", "D0 45", "D0 56", "D0 67", "D0 79"],
            Hex(surface.EncodeMeters(view))
        );
        for (var i = 0; i < 12; i++) {
            surface.EncodeMeters(view);
        }

        Assert.Empty(surface.EncodeMeters(view));
    }

    [Fact]
    public void The_beats_display_shows_bars_and_beats() {
        var (surface, view) = Connected();
        surface.Decode(Bytes("90 35 7F"), 0);
        var output = Hex(surface.Encode(Playing(view)));
        Assert.Contains("90 71 00", output);
        Assert.Contains("90 72 7F", output);

        // "  7 2 3120": bar 7, beat 2, division 3, ticks 120 (dots after each group but the last).
        Assert.Contains("B0 47 77", output);
        Assert.Contains("B0 45 72", output);
        Assert.Contains("B0 43 73", output);
        Assert.Contains("B0 42 31", output);
    }

    [Fact]
    public void An_extender_has_strips_alone_and_device_id_15() {
        var surface = new MackieControl(true);
        Assert.Equal(ControlSurfaceKind.MackieControlXt, surface.Kind);
        var output = Hex(surface.Encode(Tracks(16)));
        Assert.All(
            output.Where(static m => m.StartsWith("F0", StringComparison.Ordinal)),
            static m => Assert.StartsWith("F0 00 00 66 15", m)
        );
        Assert.DoesNotContain(output, static m => m.StartsWith("B0 4", StringComparison.Ordinal));
        Assert.DoesNotContain("90 5D 7F", output);
        Assert.Empty(surface.Decode(Bytes("90 5E 7F"), 0));
    }

    [Fact]
    public void Banking_moves_by_8_and_1_within_the_tracks() {
        var (surface, view) = Connected(12);
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 8), One(surface, "90 2F 7F"));
        // Change-only: the top line from its first change ("Track1" to "Track9", offset 5) to its last.
        Assert.StartsWith("F0 00 00 66 14 12 05 " + Hex([.."9 Trck10"u8]), Hex(surface.Encode(view)[0]));
        surface.Invalidate();
        Assert.StartsWith(
            "F0 00 00 66 14 12 00 " + Hex([.."Track9 Trck10 Trck11 Trck12        "u8]),
            Scribble(surface.Encode(view))
        );
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 11), One(surface, "90 2F 7F"));
        Assert.Empty(surface.Decode(Bytes("90 31 7F"), 0)); // Already at the last track.
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 10), One(surface, "90 30 7F"));
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 2), One(surface, "90 2E 7F"));
        Assert.Equal(new(SurfaceActionKind.Banked, Value: 0), One(surface, "90 2E 7F"));
    }

    [Fact]
    public void A_main_unit_and_two_extenders_bank_as_one() {
        MackieControl main = new(false), left = new(true), right = new(true);
        var group = new SurfaceGroup([left, main, right]);
        var view = Tracks(40);
        group.Encode(view);
        Assert.Equal([0, 8, 16], group.Units.Select(static u => u.BankOffset));

        Assert.Equal([new SurfaceAction(SurfaceActionKind.Banked, Value: 8)], group.Decode(1, Bytes("90 2F 7F"), 0));
        Assert.Equal([8, 16, 24], group.Units.Select(static u => u.BankOffset));
        foreach (var unit in group.Units) {
            unit.Invalidate();
        }

        var output = group.Encode(view);
        Assert.StartsWith("F0 00 00 66 15 12 00 " + Hex([.."Track9 "u8]), Scribble(output[0]));
        Assert.StartsWith("F0 00 00 66 14 12 00 " + Hex([.."Trck17 "u8]), Scribble(output[1]));
        Assert.StartsWith("F0 00 00 66 15 12 00 " + Hex([.."Trck25 "u8]), Scribble(output[2]));

        // The fader on the right extender's strip 0 is track 25's.
        Assert.Equal("t25", Assert.Single(group.Decode(2, Bytes("E0 00 40"), 0)).TrackId);
        Assert.Equal(7, group.Decode(1, Bytes("90 30 7F"), 0)[0].Value);
        Assert.Equal([7, 15, 23], group.Units.Select(static u => u.BankOffset));
        for (var i = 0; i < 5; i++) {
            group.Decode(0, Bytes("90 2F 7F"), 0); // The extender's own bank buttons are ignored (it has none) …
        }

        Assert.Equal(7, group.BankBase);
        for (var i = 0; i < 5; i++) {
            group.Decode(1, Bytes("90 2F 7F"), 0); // … the main unit's move all three, up to the last track.
        }

        Assert.Equal(39, group.BankBase);
        group.Encode(Tracks(20)); // Tracks went: the bank comes back within them.
        Assert.Equal(19, group.BankBase);
    }

    [Fact]
    public void Extenders_follow_the_main_units_vpot_mode_and_continue_its_plug_in_page() {
        MackieControl main = new(false), xt = new(true);
        var group = new SurfaceGroup([main, xt]);
        var view = Tracks(16);
        group.Encode(view);
        group.Decode(0, Bytes("90 29 7F"), 0);
        Assert.Equal(VPotMode.Send, xt.Mode);
        Assert.Equal("send:s1", Assert.Single(group.Decode(1, Bytes("B0 10 01"), 0)).Address); // Track 9.
        group.Decode(0, Bytes("90 2B 7F"), 0);
        group.Encode(view);
        group.Decode(0, Bytes("90 20 7F"), 0);
        Assert.Equal("insert:eq:p9", Assert.Single(group.Decode(1, Bytes("B0 10 01"), 0)).Address);
        Assert.Equal("insert:eq:p10", Assert.Single(group.Decode(1, Bytes("B0 11 01"), 0)).Address);
    }

    [Fact]
    public void Running_status_and_real_time_bytes_are_read() {
        var (surface, _) = Connected();
        var actions = surface.Decode(Bytes("90 5E 7F F8 5D 7F"), 0);
        Assert.Equal([SurfaceActionKind.Play, SurfaceActionKind.Stop], actions.Select(static a => a.Kind));
        Assert.Empty(surface.Decode(Bytes("90 5E"), 0));
        Assert.Empty(surface.Decode(Bytes("F0 00 00 66"), 0));
    }

    [Theory]
    [InlineData("Track 1", "Track1")]
    [InlineData("Track 10", "Trck10")]
    [InlineData("Bass Guitar", "BssGtr")]
    [InlineData("Kick", "Kick  ")]
    [InlineData("Sänger", "S?nger")]
    public void Names_are_abbreviated_to_six_characters(string name, string shown) =>
        Assert.Equal(shown, SurfaceText.Name(name, 6));

    [Theory]
    [InlineData("−12.0 dB", "-12.0")]
    [InlineData("-6.0 dB", "-6.0dB")]
    [InlineData("C", "     C")]
    public void Values_are_fitted_to_six_characters(string value, string shown) =>
        Assert.Equal(shown.PadLeft(6), SurfaceText.Value(value, 6));
}
