namespace Anode.ContentBuilder;

/// <summary>
///     The Session Players' content (docs/12-phase-11.md, workstream D): the three Drummer kits (acoustic-style,
///     electronic, brushes), rendered from Anode's own instruments (the Drum Synth, and the physical model once it is
///     in) as multi-output Drum Machine Designer kits with their pads on <c>DrumKitNotes</c>. The styles themselves are
///     data in Anode.Generative, not pack content.
/// </summary>
static class SessionPlayerContent {
    /// <summary>Whether this build makes the kits: false until workstream D implements <see cref="AddKits" />.</summary>
    public static bool Ready => false;

    /// <summary>
    ///     Adds the Drummer kits to <paramref name="pack" /> (the test pack: one kit), drums from the T1 synth when
    ///     <paramref name="t1" />, as <see cref="Content" /> does.
    /// </summary>
    public static void AddKits(PackBuilder pack, bool test, bool t1) => throw Pending.WorkstreamD();
}
