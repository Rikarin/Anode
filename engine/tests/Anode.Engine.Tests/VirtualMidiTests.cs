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
}
