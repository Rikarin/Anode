using Anode.Contracts;

namespace Anode.Notation;

/// <summary>
///     The factory staff styles as data (docs/10-phase-9.md; their ids are <see cref="StaffStyleIds" />), in
///     <see cref="StaffStyleIds.All" />'s order: <c>GET /library/staff-styles</c>.
/// </summary>
public static class FactoryStaffStyles {
    public static IReadOnlyList<StaffStyle> All => throw Pending.WorkstreamA();

    /// <summary>The factory style <paramref name="id" />, or null.</summary>
    public static StaffStyle? Find(string id) => throw Pending.WorkstreamA();

    /// <summary>
    ///     The style a track with <paramref name="styleId" /> uses: one of <paramref name="score" />'s own, else a factory
    ///     one; null or an unknown id gives <see cref="StaffStyleIds.Default" />.
    /// </summary>
    public static StaffStyle Resolve(ProjectScore score, string? styleId) => throw Pending.WorkstreamA();
}
