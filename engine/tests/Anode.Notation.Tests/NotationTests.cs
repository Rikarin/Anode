using Anode.Contracts;

namespace Anode.Notation.Tests;

/// <summary>Placeholder until workstream A's golden views, MEI and properties (docs/10-phase-9.md).</summary>
public sealed class NotationTests {
    [Fact]
    public void The_default_staff_style_is_a_factory_one() => Assert.Contains(StaffStyleIds.Default, StaffStyleIds.All);
}
