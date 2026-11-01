using Anode.Contracts;

namespace Anode.Notation;

/// <summary>
///     A score view as MEI 5 (docs/10-phase-9.md, workstream A): the layout as the <c>scoreDef</c> (page size, margins,
///     staff size, title block, header and footer), parts as staff groups (braced as the view says), measures with their
///     staves, layers and events, symbols as control events (<c>dynam</c>, <c>hairpin</c>, <c>slur</c>, <c>artic</c>,
///     <c>fermata</c>, <c>trill</c>, <c>octave</c>, <c>pedal</c>, <c>dir</c>, <c>tempo</c>, <c>reh</c>), lyrics as
///     <c>verse</c>/<c>syl</c> and chord symbols as <c>harm</c>. Every element of the view carries its
///     <see cref="ScoreIds" /> as <c>xml:id</c>.
/// </summary>
public static class MeiWriter {
    /// <summary>The MEI document; the same view gives the same text, byte for byte.</summary>
    public static string Write(ScoreView view) => throw Pending.WorkstreamA();
}
