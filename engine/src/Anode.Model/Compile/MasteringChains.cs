using Anode.Contracts;
using Anode.Dsp.Mastering;
using Anode.Ipc;

namespace Anode.Model.Compile;

/// <summary>
///     Designs a Mastering Assistant's chain (docs/12-phase-11.md): what its analysis sets at full strength for a
///     character (<see cref="EngineMasteringChain" />). A seam: the server's is <see cref="DspMasteringDesigner" />
///     (workstream E's <see cref="MasteringChainDesigner" />), tests use doubles.
/// </summary>
public interface IMasteringDesigner {
    EngineMasteringChain Design(MasteringAnalysis analysis, MasteringCharacter character);
}

/// <summary><see cref="IMasteringDesigner" /> over <c>Anode.Dsp.Mastering</c> (workstream E).</summary>
public sealed class DspMasteringDesigner : IMasteringDesigner {
    public static DspMasteringDesigner Instance { get; } = new();

    public EngineMasteringChain Design(MasteringAnalysis analysis, MasteringCharacter character) {
        var chain = MasteringChainDesigner.Design(Measurement(analysis), (MasteringCharacterKind)character);
        return new(
            [..chain.EqGainsDb],
            chain.CompressorThresholdDb,
            chain.CompressorRatio,
            chain.LoudnessLufs,
            chain.ChainGainDb
        );
    }

    /// <summary>The DSP's measurement from a stored analysis (the contract's without its range).</summary>
    public static MasteringMeasurement Measurement(MasteringAnalysis analysis) =>
        new(
            analysis.IntegratedLufs,
            analysis.MaxShortTermLufs,
            analysis.LoudnessRange,
            analysis.TruePeakDbtp,
            analysis.CrestFactorDb,
            analysis.Width,
            [..analysis.SpectrumDb.Select(static v => (float)v)]
        );
}

/// <summary>
///     The chains of a strip's analysed Mastering Assistants, designed once per analysis and character (the designer is
///     pure; a new analysis, or a character change, designs again). A designer that fails gives no chain (the assistant
///     then only limits to its ceiling), and the failure is kept for the caller to report.
/// </summary>
public sealed class MasteringChains(IMasteringDesigner designer) {
    readonly Lock gate = new();

    readonly Dictionary<(MasteringAnalysis Analysis, MasteringCharacter Character), EngineMasteringChain?> designed =
        new(ReferenceKeys.Instance);

    /// <summary>The last design failure's message, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>The chain of <paramref name="insert" /> (null: not an analysed Mastering Assistant).</summary>
    public EngineMasteringChain? Of(Insert insert) {
        if (insert.Mastering is not { } analysis || !Editing.Edits.IsMasteringAssistant(insert)) {
            return null;
        }

        var key = (analysis, Editing.Edits.MasteringSettingsOf(insert).Character);
        lock (gate) {
            if (designed.TryGetValue(key, out var chain)) {
                return chain;
            }

            try {
                chain = designer.Design(analysis, key.Character);
                LastError = null;
            } catch (Exception e) when (e is not OutOfMemoryException) {
                chain = null;
                LastError = e.Message;
            }

            // The analyses of a long session: a few per assistant; forget them all now and then.
            if (designed.Count > 64) {
                designed.Clear();
            }

            designed[key] = chain;
            return chain;
        }
    }

    /// <summary>Keys by the analysis instance (an assistant's analysis is replaced, never changed) and character.</summary>
    sealed class ReferenceKeys : IEqualityComparer<(MasteringAnalysis Analysis, MasteringCharacter Character)> {
        public static ReferenceKeys Instance { get; } = new();

        public bool Equals(
            (MasteringAnalysis Analysis, MasteringCharacter Character) x,
            (MasteringAnalysis Analysis, MasteringCharacter Character) y
        ) =>
            ReferenceEquals(x.Analysis, y.Analysis) && x.Character == y.Character;

        public int GetHashCode((MasteringAnalysis Analysis, MasteringCharacter Character) key) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Analysis), key.Character);
    }
}
