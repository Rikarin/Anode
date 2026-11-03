namespace Anode.ContentBuilder;

/// <summary>
///     The tape-replay instrument's tape sets (docs/12-phase-11.md, workstream E): strings, flutes and choir, each key's
///     recording (up to the tape's 8 s) rendered from Anode's own instruments through a tape model.
/// </summary>
static class TapeContent {
    /// <summary>Whether this build makes the tape sets: false until workstream E implements <see cref="AddTapeSets" />.</summary>
    public static bool Ready => false;

    /// <summary>Adds the tape sets to <paramref name="pack" /> (the test pack: one, over a few keys).</summary>
    public static void AddTapeSets(PackBuilder pack, bool test) => throw Pending.WorkstreamE();
}
