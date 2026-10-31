using Anode.Contracts;
using Anode.Control.Osc;
using CsCheck;
using static Anode.Control.Tests.Views;

namespace Anode.Control.Tests;

/// <summary>The OSC 1.0 codec and Anode's address space (docs/control/osc.md).</summary>
public sealed class OscTests {
    static readonly Gen<string> Text = Gen.String[Gen.Char.Where(static c => c != '\0' && !char.IsSurrogate(c)), 0, 12];

    static readonly Gen<OscArgument> Argument = Gen.OneOf(
        Gen.Int.Select(static i => (OscArgument)new OscInt(i)),
        Gen.Float.Select(static f => (OscArgument)new OscFloat(f)),
        Text.Select(static s => (OscArgument)new OscString(s)),
        Gen.Byte.Array[0, 9].Select(static b => (OscArgument)new OscBlob(b)),
        Gen.Bool.Select(static b => (OscArgument)new OscBool(b))
    );

    static readonly Gen<OscMessage> Message = Gen.Select(Text, Argument.List[0, 6])
        .Select(static (address, args) => new OscMessage("/" + address, args));

    static readonly Gen<OscPacket> Packet = Gen.Recursive<OscPacket>(static (depth, packet) =>
        depth >= 3
            ? Message.Select(static m => (OscPacket)m)
            : Gen.Frequency(
                (3, Message.Select(static m => (OscPacket)m)),
                (1,
                    Gen.Select(Gen.ULong, packet.List[0, 4])
                        .Select(static (tag, elements) => (OscPacket)new OscBundle(tag, elements)))
            )
    );

    static OscMessage Msg(string address, params OscArgument[] args) => new(address, args);

    [Fact]
    public void Packets_round_trip() =>
        Packet.Sample(static packet => {
                var bytes = OscCodec.Encode(packet);
                return bytes.Length % 4 == 0 && OscCodec.Decode(bytes).Equals(packet);
            }
        );

    [Fact]
    public void A_message_encodes_to_the_specifications_bytes() {
        // The OSC 1.0 specification's example: "/oscillator/4/frequency" ,f 440.0.
        var bytes = OscCodec.Encode(Msg("/oscillator/4/frequency", new OscFloat(440)));
        Assert.Equal(
            "2F 6F 73 63 69 6C 6C 61 74 6F 72 2F 34 2F 66 72 65 71 75 65 6E 63 79 00 2C 66 00 00 43 DC 00 00",
            Hex(bytes)
        );
        var all = OscCodec.Encode(
            Msg(
                "/a",
                new OscInt(1),
                new OscString("hi"),
                new OscBlob([1, 2, 3, 4, 5]),
                new OscBool(true),
                new OscBool(false)
            )
        );
        Assert.Equal(
            "2F 61 00 00 2C 69 73 62 54 46 00 00 00 00 00 01 68 69 00 00 00 00 00 05 01 02 03 04 05 00 00 00",
            Hex(all)
        );
    }

    [Fact]
    public void A_bundle_carries_its_time_tag_and_sized_elements() {
        var bundle = new OscBundle(OscBundle.Immediately, [Msg("/a"), Msg("/b", new OscInt(2))]);
        Assert.Equal(
            "23 62 75 6E 64 6C 65 00 00 00 00 00 00 00 00 01 00 00 00 08 2F 61 00 00 2C 00 00 00 00 00 00 0C 2F 62 00 00 2C 69 00 00 00 00 00 02",
            Hex(OscCodec.Encode(bundle))
        );
    }

    [Fact]
    public void Other_numeric_types_and_a_missing_type_tag_string_are_read() {
        Assert.Equal(Msg("/a"), OscCodec.Decode(Bytes("2F 61 00 00")));
        Assert.Equal(
            Msg("/a", new OscInt(7), new OscFloat(0.5f), new OscString("x")),
            OscCodec.Decode(
                Bytes("2F 61 00 00 2C 68 64 53 4E 00 00 00 00 00 00 00 00 00 00 07 3F E0 00 00 00 00 00 00 78 00 00 00")
            )
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("61 00 00 00")] // No leading '/'.
    [InlineData("2F 61 00")] // Unpadded.
    [InlineData("2F 61 62 63")] // No NUL.
    [InlineData("2F 61 00 00 69 00 00 00")] // No ','.
    [InlineData("2F 61 00 00 2C 69 00 00 00 00")] // A short int.
    [InlineData("2F 61 00 00 2C 62 00 00 7F FF FF FF")] // A huge blob.
    [InlineData("2F 61 00 00 2C 62 00 00 FF FF FF FF")] // A negative blob.
    [InlineData("2F 61 00 00 2C 7A 00 00")] // An unknown tag.
    [InlineData("2F 61 00 00 2C 00 00 00 00")] // Trailing bytes.
    [InlineData("23 62 75 6E 64 6C 65 00 00 00 00 00")] // A short time tag.
    [InlineData("23 62 75 6E 64 6C 65 00 00 00 00 00 00 00 00 01 00 00 00 09 2F 61 00 00 2C 00 00 00 00")] // Bad size.
    [InlineData("23 62 75 6E 64 6C 00 00 00 00 00 00 00 00 00 01")] // Not "#bundle".
    [InlineData("2F FF 00 00")] // Not UTF-8.
    public void Malformed_packets_are_format_errors(string hex) {
        Assert.Throws<FormatException>(() => OscCodec.Decode(Bytes(hex)));
        Assert.False(OscCodec.TryDecode(Bytes(hex), out var packet));
        Assert.Null(packet);
    }

    [Fact]
    public void Any_bytes_decode_or_fail_with_a_format_error() =>
        Gen.Byte.Array[0, 64]
            .Sample(static bytes => {
                    try {
                        OscCodec.Decode(bytes);
                    } catch (FormatException) {
                        // Rejected: the only exception Decode throws.
                    }
                }
            );

    [Fact]
    public void Corrupted_packets_decode_or_fail_with_a_format_error() =>
        Gen.Select(Packet, Gen.Int[0, 1000], Gen.Byte)
            .Sample(static (packet, at, value) => {
                    var bytes = OscCodec.Encode(packet);
                    if (bytes.Length > 0) {
                        bytes[at % bytes.Length] = value;
                    }

                    OscCodec.TryDecode(bytes, out _);
                    OscCodec.TryDecode(bytes.AsSpan(0, at % (bytes.Length + 1)), out _);
                }
            );

    [Fact]
    public void Encoding_refuses_what_OSC_cannot_hold() {
        Assert.Throws<ArgumentException>(static () => OscCodec.Encode(Msg("a")));
        Assert.Throws<ArgumentException>(static () => OscCodec.Encode(Msg("/a", new OscString("x\0y"))));
    }

    [Fact]
    public void Transport_addresses_press_their_functions() {
        var view = Tracks(4);
        var play = OscAddressSpace.Resolve(Msg("/anode/transport/play"), view);
        Assert.Equal(
            new OscRequest(
                OscRequestKind.Set,
                new(ControlTargetKind.Transport, Transport: ControlTransportAction.Play),
                1
            ),
            play
        );
        Assert.Equal(0, OscAddressSpace.Resolve(Msg("/anode/transport/rewind", new OscInt(0)), view)!.Value);
        Assert.Equal(
            ControlTransportAction.GoToStart,
            OscAddressSpace.Resolve(Msg("/anode/transport/goToStart", new OscBool(true)), view)!.Target!.Transport
        );
        Assert.Equal(
            new(OscRequestKind.Locate, Value: 16),
            OscAddressSpace.Resolve(Msg("/anode/transport/position", new OscFloat(16)), view)
        );
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/transport/position"), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/transport/dance"), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/transport/play", new OscString("now")), view));
    }

    [Fact]
    public void Track_addresses_resolve_by_index_and_id() {
        var view = Tracks(4);
        Assert.Equal(
            new OscRequest(OscRequestKind.Set, new(ControlTargetKind.Parameter, "t1", "volume"), 0.5),
            OscAddressSpace.Resolve(Msg("/anode/track/1/volume", new OscFloat(0.5f)), view)
        );
        Assert.Equal(
            new OscRequest(OscRequestKind.SetDb, new(ControlTargetKind.Parameter, "t3", "volume"), -6),
            OscAddressSpace.Resolve(Msg("/anode/track/id/t3/volume/db", new OscInt(-6)), view)
        );
        Assert.Equal(1, OscAddressSpace.Resolve(Msg("/anode/track/2/pan", new OscFloat(3)), view)!.Value); // Clamped.
        Assert.Equal(
            new OscRequest(
                OscRequestKind.Set,
                new(ControlTargetKind.TrackSwitch, "t2", Switch: TrackSwitchAction.Mute),
                1
            ),
            OscAddressSpace.Resolve(Msg("/anode/track/2/mute", new OscBool(true)), view)
        );
        Assert.Equal(
            TrackSwitchAction.Solo,
            OscAddressSpace.Resolve(Msg("/anode/track/2/solo", new OscInt(0)), view)!.Target!.Switch
        );
        Assert.Equal(
            TrackSwitchAction.RecordArm,
            OscAddressSpace.Resolve(Msg("/anode/track/2/arm", new OscInt(1)), view)!.Target!.Switch
        );
        Assert.Equal(
            "send:s2",
            OscAddressSpace.Resolve(Msg("/anode/track/1/send/2", new OscFloat(0.1f)), view)!.Target!.Address
        );
        Assert.Equal(
            OscRequestKind.SetDb,
            OscAddressSpace.Resolve(Msg("/anode/track/1/send/1/db", new OscFloat(-3)), view)!.Kind
        );
        Assert.Equal(
            new OscRequest(OscRequestKind.SetName, new(ControlTargetKind.Parameter, "t4", "name"), Text: "Bass"),
            OscAddressSpace.Resolve(Msg("/anode/track/4/name", new OscString("Bass")), view)
        );
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/5/volume", new OscFloat(0.5f)), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/0/volume", new OscFloat(0.5f)), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/id/nope/volume", new OscFloat(0.5f)), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/2/send/1", new OscFloat(0.5f)), view)); // No send.
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/1/volume"), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/1/volume", new OscFloat(float.NaN)), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/track/1/name", new OscInt(1)), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/other/track/1/volume", new OscFloat(0.5f)), view));
    }

    [Fact]
    public void The_selected_track_follows_the_selection() {
        var view = Tracks(4);
        Assert.Equal(
            new OscRequest(OscRequestKind.Set, new(ControlTargetKind.Parameter, null, "volume"), 0.25),
            OscAddressSpace.Resolve(Msg("/anode/selected/volume", new OscFloat(0.25f)), view)
        );
        Assert.Equal(
            "send:s1",
            OscAddressSpace.Resolve(Msg("/anode/selected/send/1", new OscFloat(0.5f)), view)!.Target!.Address
        );
        Assert.Equal(
            TrackSwitchAction.SelectNext,
            OscAddressSpace.Resolve(Msg("/anode/selected/next"), view)!.Target!.Switch
        );
        Assert.Equal(
            TrackSwitchAction.SelectPrevious,
            OscAddressSpace.Resolve(Msg("/anode/selected/previous"), view)!.Target!.Switch
        );
        var none = view with { Tracks = [..view.Tracks.Select(t => t with { Selected = false })] };
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/selected/volume", new OscFloat(0.25f)), none));
    }

    [Fact]
    public void Markers_smart_controls_key_commands_and_subscriptions() {
        var view = Tracks(2);
        Assert.Equal(
            new(OscRequestKind.GoToMarker, Number: 2),
            OscAddressSpace.Resolve(Msg("/anode/marker/2"), view)
        );
        Assert.Equal(
            new(OscRequestKind.GoToMarker, Number: 1),
            OscAddressSpace.Resolve(Msg("/anode/marker/goto", new OscInt(1)), view)
        );
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/marker/3"), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/marker/1", new OscInt(0)), view)); // A release.
        var smart = new OscRequest(OscRequestKind.Set, new(ControlTargetKind.SmartControl, Address: "knob2"), 0.5);
        Assert.Equal(smart, OscAddressSpace.Resolve(Msg("/anode/smart/knob2", new OscFloat(0.5f)), view));
        Assert.Equal(smart, OscAddressSpace.Resolve(Msg("/anode/smart/2", new OscFloat(0.5f)), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/smart/knob9", new OscFloat(0.5f)), view));
        Assert.Equal(
            new OscRequest(OscRequestKind.Set, new(ControlTargetKind.KeyCommand, KeyCommandId: "edit.undo"), 1),
            OscAddressSpace.Resolve(Msg("/anode/key/edit.undo"), view)
        );
        Assert.Equal(
            new(OscRequestKind.Subscribe, Number: 9000),
            OscAddressSpace.Resolve(Msg("/anode/subscribe", new OscInt(9000)), view)
        );
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/subscribe"), view));
        Assert.Null(OscAddressSpace.Resolve(Msg("/anode/subscribe", new OscInt(70000)), view));
        Assert.Equal(
            new(OscRequestKind.Unsubscribe),
            OscAddressSpace.Resolve(Msg("/anode/unsubscribe"), view)
        );
        Assert.Equal(
            new(OscRequestKind.Unsubscribe, Number: 9000),
            OscAddressSpace.Resolve(Msg("/anode/unsubscribe", new OscFloat(9000)), view)
        );
    }

    [Fact]
    public void Feedback_lists_every_address_with_its_value() {
        var view = Playing(Tracks(2));
        var feedback = OscAddressSpace.Feedback(view)
            .ToDictionary(static m => m.Address, static m => Assert.Single(m.Arguments));
        Assert.Equal(new OscInt(1), feedback["/anode/transport/play"]);
        Assert.Equal(new OscInt(0), feedback["/anode/transport/stop"]);
        Assert.Equal(new OscInt(0), feedback["/anode/transport/record"]);
        Assert.Equal(new OscInt(0), feedback["/anode/transport/cycle"]);
        Assert.Equal(new OscFloat(120), feedback["/anode/transport/tempo"]);
        Assert.Equal(new OscString("7.2.3.120"), feedback["/anode/transport/beats"]);
        Assert.Equal(new OscString("00:00:12:05"), feedback["/anode/transport/timecode"]);
        Assert.Equal(new OscInt(2), feedback["/anode/tracks"]);
        Assert.Equal(new OscString("Track 1"), feedback["/anode/track/1/name"]);
        Assert.Equal(new OscFloat(0.125f), feedback["/anode/track/1/volume"]);
        Assert.Equal(new OscFloat(-6), feedback["/anode/track/1/volume/db"]);
        Assert.Equal(new OscFloat(0.25f), feedback["/anode/track/1/pan"]);
        Assert.Equal(new OscInt(0), feedback["/anode/track/2/mute"]);
        Assert.Equal(new OscFloat(0.25f), feedback["/anode/track/1/send/2"]);
        Assert.Equal(new OscString("Bus 2"), feedback["/anode/track/1/send/2/name"]);
        Assert.Equal(new OscInt(1), feedback["/anode/selected/index"]);
        Assert.Equal(new OscString("Track 1"), feedback["/anode/selected/name"]);
        Assert.Equal(new OscInt(2), feedback["/anode/markers"]);
        Assert.Equal(new OscString("Verse"), feedback["/anode/marker/2/name"]);
        Assert.Equal(new OscFloat(0.75f), feedback["/anode/smart/knob1"]);
        Assert.Equal(new OscString("Cutoff"), feedback["/anode/smart/knob1/name"]);
        Assert.Equal(new OscString("2.4 kHz"), feedback["/anode/smart/knob1/display"]);

        // Every settable feedback address resolves back to a request on the same target.
        foreach (var (address, argument) in feedback) {
            if (address.EndsWith("/volume", StringComparison.Ordinal)
                || address.EndsWith("/pan", StringComparison.Ordinal)) {
                Assert.NotNull(OscAddressSpace.Resolve(new(address, [argument]), view));
            }
        }
    }
}
