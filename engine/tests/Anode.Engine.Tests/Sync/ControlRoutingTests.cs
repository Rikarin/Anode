using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 8's control routing on the MIDI thread (docs/09-phase-8.md, "Routing"): a control port's messages (SysEx
///     included) go to the server and never to tracks; a message a matcher matches is consumed, with the 14-bit and NRPN
///     families and a note's offs; the rest play as before; in Learn everything is forwarded and still plays; and a
///     surface's feedback is sent at once.
/// </summary>
public sealed class ControlRoutingTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly FakeMidi midi = new();
    readonly EngineController controller;
    readonly OfflineDriver driver;
    readonly List<EngineControlEvent> forwarded = [];

    public ControlRoutingTests() {
        var (registry, _) = MidiFixtures.Registry();
        controller = new(new(Rate, Block), registry, midi);
        driver = new(controller, Block);
        controller.Midi.ControlMidi = batch => forwarded.AddRange(batch.Events);
        using var segment = TestAudio.Segment([new(1, [], [])], 1);
        controller.LoadModel(
            TestAudio.Load(segment, 1, true, [MidiFixtures.InstrumentTrack(1, armed: true)], [], dir)
        );
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    TestInstrument Instrument =>
        (TestInstrument)controller.Engine.Root.Tracks[controller.Engine.Root.IndexOf(1)].Instrument!.Instrument;

    /// <summary>What the track played, as (status, data1), after a pass and a callback.</summary>
    List<(int Status, int Data1)> Played() {
        controller.Midi.Pump();
        driver.Process();
        return [..Instrument.Events.Select(static e => ((int)e.Status, (int)e.Data1))];
    }

    List<string> Forwarded() => [..forwarded.Select(static e => $"{e.Port} {Convert.ToHexString(e.Data)}")];

    [Fact]
    public void A_control_ports_messages_go_to_the_server_and_never_to_tracks() {
        controller.SetControlRouting(new(["in-b"], [], false));
        midi.Play(1, 0xE0, 0x00, 0x40, 1_000); // a Mackie Control fader on the surface's port
        midi.Play(1, 0x90, 0x5E, 0x7F, 2_000); // its Play button
        midi.Play(1, 0xF8, 0, 0, 3_000); // even system messages
        midi.PlaySysEx(1, [0xF0, 0x00, 0x00, 0x66, 0x14, 0x01, 0xF7], 4_000);
        midi.PlaySysEx(0, [0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7], 5_000); // SysEx elsewhere: dropped
        midi.Play(0, 0x90, 60, 100, 6_000); // another port plays as before

        Assert.Equal([(0x90, 60)], Played());
        Assert.Equal(
            ["in-b E00040", "in-b 905E7F", "in-b F8", "in-b F00000661401F7"],
            Forwarded()
        );
        Assert.Equal([1_000L, 2_000, 3_000, 4_000], forwarded.Select(static e => e.TimeNs));
    }

    [Fact]
    public void Matched_messages_are_consumed_with_their_families_and_the_rest_play() {
        controller.SetControlRouting(
            new(
                [],
                [
                    new(null, 0xB0, -1, 21), // CC 21, any port, any channel
                    new("in-a", 0x90, 9, 36), // note 36 on channel 10 from in-a: its offs too
                    new(null, 0xB0, 0, 7, EngineMatcherFamily.ControlChange14), // CC 7 and 39 on channel 1
                    new(null, 0xB0, 1, -1, EngineMatcherFamily.Nrpn) // NRPN on channel 2
                ],
                false
            )
        );
        midi.Play(0, 0xB0, 21, 64);
        midi.Play(1, 0xB5, 21, 10); // any port, any channel
        midi.Play(0, 0xB0, 22, 64); // plays
        midi.Play(0, 0x99, 36, 100);
        midi.Play(0, 0x89, 36, 0);
        midi.Play(1, 0x99, 36, 100); // another port: plays
        midi.Play(0, 0x99, 37, 100); // another key: plays
        midi.Play(0, 0xB0, 7, 100);
        midi.Play(0, 0xB0, 39, 5);
        midi.Play(0, 0xB1, 7, 100); // another channel: plays
        foreach (var cc in new byte[] { 99, 98, 6, 38, 96, 97 }) {
            midi.Play(0, 0xB1, cc, 1);
        }

        midi.Play(0, 0xB2, 99, 1); // NRPN on another channel: plays
        midi.Play(0, 0xB1, 10, 1); // a CC outside the family: plays

        Assert.Equal([(0xB0, 22), (0x99, 36), (0x99, 37), (0xB1, 7), (0xB2, 99), (0xB1, 10)], Played());
        Assert.Equal(
            [
                "in-a B01540", "in-b B5150A", "in-a 992464", "in-a 892400", "in-a B00764", "in-a B02705",
                "in-a B16301", "in-a B16201", "in-a B10601", "in-a B12601", "in-a B16001", "in-a B16101"
            ],
            Forwarded()
        );
    }

    [Fact]
    public void Learn_forwards_every_channel_message_and_still_plays_the_unmatched() {
        controller.SetControlRouting(new([], [new(null, 0xB0, -1, 21)], true));
        midi.Play(0, 0xB0, 22, 64);
        midi.Play(0, 0x90, 60, 100);
        midi.Play(0, 0xB0, 21, 1); // matched: forwarded, not played
        midi.Play(0, 0xF8, 0, 0); // not a channel message: neither
        Assert.Equal([(0xB0, 22), (0x90, 60)], Played());
        Assert.Equal(["in-a B01640", "in-a 903C64", "in-a B01501"], Forwarded());

        // Learn off again: everything but the matcher's plays, nothing else is forwarded.
        forwarded.Clear();
        controller.SetControlRouting(new([], [new(null, 0xB0, -1, 21)], false));
        midi.Play(0, 0x90, 61, 100);
        Assert.Equal([(0xB0, 22), (0x90, 60), (0x90, 61)], Played());
        Assert.Empty(forwarded);
    }

    [Fact]
    public void Forwarded_messages_come_in_one_batch_a_pass() {
        var batches = 0;
        controller.Midi.ControlMidi = batch => {
            batches++;
            forwarded.AddRange(batch.Events);
        };
        controller.SetControlRouting(new(["in-a"], [], false));
        for (var i = 0; i < 10; i++) {
            midi.Play(0, 0xB0, (byte)i, 1);
        }

        controller.Midi.Pump();
        controller.Midi.Pump(); // nothing new: no batch
        Assert.Equal((1, 10), (batches, forwarded.Count));
    }

    [Fact]
    public void Surface_feedback_is_sent_at_once_to_its_port() {
        byte[] feedback = [0xF0, 0x00, 0x00, 0x66, 0x14, 0x12, 0x00, 0x41, 0x42, 0xF7, 0xE0, 0x00, 0x40];
        Assert.True(controller.SendMidi("out-a", feedback));
        var (uid, message, time) = Assert.Single(midi.SentNow());
        Assert.Equal(("out-a", 0L), (uid, time));
        Assert.Equal(feedback, message);
    }
}
