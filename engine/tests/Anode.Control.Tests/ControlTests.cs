using Anode.Control.Osc;

namespace Anode.Control.Tests;

/// <summary>Placeholder until workstream D's vectors and properties (docs/09-phase-8.md).</summary>
public sealed class ControlTests {
    [Fact]
    public void The_OSC_address_space_is_under_anode() => Assert.Equal("/anode", OscAddressSpace.Root);
}
