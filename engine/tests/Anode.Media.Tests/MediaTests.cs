using Anode.Contracts;
using Anode.Media.Opus;

namespace Anode.Media.Tests;

/// <summary>Placeholder until workstream E's operation, remux, Opus and cancellation tests (docs/11-phase-10.md).</summary>
public sealed class MediaTests {
    [Fact]
    public void A_listen_in_frame_fits_a_header_and_the_largest_packet() =>
        Assert.Equal(16 + 1275, ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes);
}
