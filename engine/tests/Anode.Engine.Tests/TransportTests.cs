namespace Anode.Engine.Tests;

public sealed class TransportTests {
    [Fact]
    public void Advances_only_while_playing() {
        var transport = new Transport();
        transport.Advance(64);
        Assert.Equal(0, transport.Position);

        transport.Play();
        transport.Advance(64);
        transport.Advance(64);
        Assert.Equal(128, transport.Position);

        transport.Stop();
        transport.Advance(64);
        Assert.Equal(128, transport.Position);
    }

    [Fact]
    public void Locate_moves_the_playhead() {
        var transport = new Transport();
        transport.Locate(48_000);
        Assert.Equal(48_000, transport.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.Locate(-1));
    }
}
