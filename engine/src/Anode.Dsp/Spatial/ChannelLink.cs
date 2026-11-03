namespace Anode.Dsp.Spatial;

/// <summary>The speaker groups a dynamics insert on a surround strip can link by (<see cref="ChannelLink" />).</summary>
public enum SpeakerGroup {
    /// <summary>The ear layer's front speakers (|azimuth| &lt; 90°: L, R, C and the wides).</summary>
    Front,

    Lfe,

    /// <summary>The ear layer's surrounds (|azimuth| ≥ 90°: sides and rears).</summary>
    Surround,

    /// <summary>The upper layer.</summary>
    Height
}

/// <summary>
///     The Compressor's and Limiter's channel link on a surround strip (docs/12-phase-11.md, "Not contracted here"):
///     their <c>link</c> parameter, <see cref="All" /> (every channel's level drives one gain, as Phase 10's linked
///     policy does) or <see cref="ByGroup" /> (each <see cref="SpeakerGroup" /> its own gain, from its own channels).
///     The engine's linked policy takes the groups from <see cref="Groups" />.
/// </summary>
public static class ChannelLink {
    public const int All = 0;
    public const int ByGroup = 1;

    /// <summary>The link parameter's values, as the effects name them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["All", "By Speaker Group"];

    /// <summary>The group of channel <paramref name="channel" /> of <paramref name="layout" />.</summary>
    public static SpeakerGroup GroupOf(SpeakerLayout layout, int channel) =>
        channel == layout.LfeIndex ? SpeakerGroup.Lfe
        : layout.Elevations[channel] > 0 ? SpeakerGroup.Height
        : Math.Abs(layout.Azimuths[channel]) >= 90 ? SpeakerGroup.Surround
        : SpeakerGroup.Front;

    /// <summary>
    ///     For each channel of <paramref name="layout" />, the index of the gain it shares under <paramref name="link" />:
    ///     0 for every channel with <see cref="All" />, else its group's (as <see cref="SpeakerGroup" /> numbers them).
    /// </summary>
    public static int[] Groups(SpeakerLayout layout, int link) {
        var groups = new int[layout.ChannelCount];
        if (link == ByGroup) {
            for (var c = 0; c < groups.Length; c++) {
                groups[c] = (int)GroupOf(layout, c);
            }
        }

        return groups;
    }
}
