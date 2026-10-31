using Anode.Audio.Midi;

namespace Anode.Engine.Tests;

public sealed class VirtualMidiTests {
    [Fact]
    public void Offers_one_input_and_one_output_and_counts_what_the_output_is_sent() {
        using var midi = new VirtualMidiBackend();
        Assert.True(midi.Open());
        Assert.Equal(2, midi.PortCount());
        Assert.Equal([true, false], midi.Ports().Select(static p => p.IsInput));

        Assert.True(midi.Send(VirtualMidiBackend.OutputUid, [0x90, 60, 100], 0));
        Assert.False(midi.Send(VirtualMidiBackend.InputUid, [0x90, 60, 100], 0));
        Assert.Equal(1, midi.Sent);

        // Nothing loops back to the input.
        Span<MidiInputMessage> read = stackalloc MidiInputMessage[4];
        Assert.Equal(0, midi.Read(read));
    }

    [Fact]
    public void Own_ports_only_hides_other_endpoints_and_renumbers_their_sources() {
        var inner = new FakeMidi();
        inner.SetPorts([
            new("keys", "Keys", true),
            new("anode:in", "Anode In", true),
            new("synth", "Synth", false),
            new("anode:out", "Anode Out", false)
        ]);
        using var midi = new OwnPortsMidiBackend(inner, "anode");
        Assert.True(midi.Open());
        Assert.Equal(["anode:in", "anode:out"], midi.Ports().Select(static p => p.Uid));

        // Input from a hidden device is dropped; Anode In's comes through as the shown list numbers it.
        inner.Play(0, 0x90, 60, 100);
        inner.Play(1, 0xB0, 21, 64);
        Span<MidiInputMessage> read = stackalloc MidiInputMessage[4];
        Assert.Equal(1, midi.Read(read));
        Assert.Equal(new(0, 0xB0, 21, 64, 0), read[0]);
        inner.PlaySysEx(0, [0xF0, 0x01, 0xF7]);
        inner.PlaySysEx(1, [0xF0, 0x02, 0xF7]);
        Span<byte> sysex = stackalloc byte[16];
        Assert.Equal(3, midi.ReadSystemExclusive(sysex, out _, out var source));
        Assert.Equal((0x02, 0), (sysex[1], source));

        // Only Anode's own outputs are sent to.
        Assert.False(midi.Send("synth", [0x90, 60, 100], 0));
        Assert.True(midi.Send("anode:out", [0x90, 60, 100], 0));
        Assert.Equal(["anode:out"], inner.Sent.Select(static s => s.Uid));
    }
}
