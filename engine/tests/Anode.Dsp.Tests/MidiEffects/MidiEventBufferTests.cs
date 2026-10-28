using Anode.Dsp.MidiEffects;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Tests.MidiEffects;

/// <summary>The MIDI FX contract's buffer and transport context (docs/07-phase-6.md).</summary>
public sealed class MidiEventBufferTests {
    [Fact]
    public void A_full_buffer_refuses_more_events() {
        var buffer = new MidiEventBuffer();
        for (var i = 0; i < MidiEventBuffer.Capacity; i++) {
            Assert.True(buffer.Add(new(i, 0x90, 60, 100)));
        }

        Assert.False(buffer.Add(new(0, 0x80, 60, 0)));
        Assert.Equal(MidiEventBuffer.Capacity, buffer.Count);
        buffer.Clear();
        Assert.Equal(0, buffer.Events.Length);
    }

    [Fact]
    public void Sorting_keeps_the_order_of_events_at_one_frame() {
        var buffer = new MidiEventBuffer();
        buffer.Add(new(5, 0x90, 64, 1));
        buffer.Add(new(2, 0x90, 60, 2));
        buffer.Add(new(5, 0x80, 62, 3));
        buffer.Add(new(0, 0x90, 67, 4));
        buffer.Sort();
        Assert.Equal([4, 2, 1, 3], buffer.Events.ToArray().Select(static e => (int)e.Data2));
    }

    [Fact]
    public void The_context_converts_quarter_notes_to_offsets() {
        var context = new TransportContext(true, 48_000, 2, 120, 3, 4, 0, 48_000, 512);
        Assert.Equal(1.0 / 24_000, context.QuartersPerSample, 15);
        Assert.Equal(3, context.BarQuarters);
        Assert.Equal(240, context.OffsetOf(2.01), 6);
    }
}
