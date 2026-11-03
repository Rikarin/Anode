using Anode.Contracts;

namespace Anode.Generative.Tests;

/// <summary>
///     Placeholder until workstream D's determinism, density, fill, chord-tone and rhythm tests
///     (docs/12-phase-11.md).
/// </summary>
public sealed class GeneratorTests {
    [Fact]
    public void Each_Session_Player_track_kind_plays_its_player() {
        Assert.Equal(SessionPlayerKind.Drummer, SessionPlayerGenerator.PlayerOf(TrackKind.Drummer));
        Assert.Equal(SessionPlayerKind.Bass, SessionPlayerGenerator.PlayerOf(TrackKind.BassPlayer));
        Assert.Equal(SessionPlayerKind.Keyboard, SessionPlayerGenerator.PlayerOf(TrackKind.KeyboardPlayer));
        Assert.Null(SessionPlayerGenerator.PlayerOf(TrackKind.Instrument));
    }
}
