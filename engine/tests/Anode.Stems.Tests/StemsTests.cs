using Anode.Contracts;

namespace Anode.Stems.Tests;

/// <summary>Placeholder until workstream E's STFT, chunking and SDR tests (docs/09-phase-8.md).</summary>
public sealed class StemsTests {
    [Fact]
    public void A_separator_names_its_stems_with_the_contract() =>
        Assert.Equal(typeof(IReadOnlyList<StemKind>), typeof(IStemSeparator).GetProperty("Stems")!.PropertyType);
}
