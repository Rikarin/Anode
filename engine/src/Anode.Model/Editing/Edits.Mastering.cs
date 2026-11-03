using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 11's Mastering Assistant (docs/12-phase-11.md): an insert on the master (<c>anode.mastering-assistant</c>)
///     whose settings are its parameters (<see cref="MasteringParameters" />; <c>setMastering</c>) and whose analysis is
///     its <c>mastering</c> (<c>setMasteringAnalysis</c>, from the analysis job). Its chain is not stored: it follows from
///     the analysis and the settings when the strip is compiled.
/// </summary>
public static partial class Edits {
    public const double MinTargetLufs = -24;
    public const double MaxTargetLufs = -6;
    public const double MinCeilingDbtp = -3;
    public const double MaxCeilingDbtp = 0;
    public const double MaxMasteringWidth = 2;

    static DeltaOp[]? MasteringOps(ProjectState state, EditCommand command) =>
        command switch {
            SetMasteringCommand c => SetMastering(state, c),
            SetMasteringAnalysisCommand c => SetMasteringAnalysis(state, c),
            _ => null
        };

    /// <summary>Whether <paramref name="insert" /> is a Mastering Assistant.</summary>
    public static bool IsMasteringAssistant(Insert insert) => insert.PluginId == MasteringParameters.PluginId;

    /// <summary>
    ///     The master's Mastering Assistant <paramref name="insertId" />, or (null) its last one; null when there is
    ///     none.
    /// </summary>
    public static Insert? MasteringAssistantOf(ProjectState state, string? insertId = null) =>
        insertId is null
            ? state.Master.Inserts.LastOrDefault(IsMasteringAssistant)
            : state.Master.Inserts.FirstOrDefault(i => i.Id == insertId && IsMasteringAssistant(i));

    /// <summary>A new Mastering Assistant insert at its default settings (not analysed yet).</summary>
    public static Insert NewMasteringAssistant(IIdSource ids) =>
        new(
            ids.Next(IdPrefix.Insert),
            MasteringParameters.PluginId,
            "Mastering Assistant",
            false,
            PluginCatalog.Sorted(Parameters(MasteringSettings.Default)),
            null
        );

    /// <summary>
    ///     The operation that puts a new Mastering Assistant last on the master, and the insert; 422 when the master is
    ///     full.
    /// </summary>
    public static (DeltaOp Op, Insert Insert) AddMasteringAssistant(ProjectState state, IIdSource ids) {
        if (state.Master.Inserts.Count >= MaxInserts) {
            throw new EditException($"A channel strip holds at most {MaxInserts} inserts.");
        }

        var insert = NewMasteringAssistant(ids);
        return (new MasterChanged(state.Master with { Inserts = [..state.Master.Inserts, insert] }), insert);
    }

    /// <summary>A Mastering Assistant's settings, as its parameters hold them (missing ones at their defaults).</summary>
    public static MasteringSettings MasteringSettingsOf(Insert insert) {
        var p = insert.Parameters;
        var defaults = MasteringSettings.Default;
        double Get(string id, double fallback) => p.TryGetValue(id, out var v) && double.IsFinite(v) ? v : fallback;
        var character = (int)Math.Round(Get(MasteringParameters.Character, 0));
        return new(
            Enum.IsDefined((MasteringCharacter)character) ? (MasteringCharacter)character : defaults.Character,
            Get(MasteringParameters.TargetLufs, defaults.TargetLufs),
            Get(MasteringParameters.CeilingDbtp, defaults.CeilingDbtp),
            Get(MasteringParameters.Eq, defaults.Eq),
            Get(MasteringParameters.Dynamics, defaults.Dynamics),
            Get(MasteringParameters.Width, defaults.Width),
            Get(MasteringParameters.MatchedBypass, 0) >= 0.5
        );
    }

    static Dictionary<string, double> Parameters(MasteringSettings settings) =>
        new(StringComparer.Ordinal) {
            [MasteringParameters.Character] = (int)settings.Character,
            [MasteringParameters.TargetLufs] = settings.TargetLufs,
            [MasteringParameters.CeilingDbtp] = settings.CeilingDbtp,
            [MasteringParameters.Eq] = settings.Eq,
            [MasteringParameters.Dynamics] = settings.Dynamics,
            [MasteringParameters.Width] = settings.Width,
            [MasteringParameters.MatchedBypass] = settings.MatchedBypass ? 1 : 0
        };

    static DeltaOp[] SetMastering(ProjectState state, SetMasteringCommand c) {
        var insert = MasteringAssistantOf(state, c.InsertId)
            ?? throw new EditException(
                c.InsertId is null ? "The master has no Mastering Assistant." : $"No Mastering Assistant '{c.InsertId}'."
            );
        var old = MasteringSettingsOf(insert);
        var settings = new MasteringSettings(
            c.Character ?? old.Character,
            c.TargetLufs ?? old.TargetLufs,
            c.CeilingDbtp ?? old.CeilingDbtp,
            c.Eq ?? old.Eq,
            c.Dynamics ?? old.Dynamics,
            c.Width ?? old.Width,
            c.MatchedBypass ?? old.MatchedBypass
        );
        CheckMasteringSettings(settings);
        var parameters = insert.Parameters.ToDictionary(StringComparer.Ordinal);
        foreach (var (id, value) in Parameters(settings)) {
            parameters[id] = value;
        }

        var changed = insert with { Parameters = PluginCatalog.Sorted(parameters) };
        return [
            new MasterChanged(
                state.Master with { Inserts = [..state.Master.Inserts.Select(i => i.Id == insert.Id ? changed : i)] }
            )
        ];
    }

    static void CheckMasteringSettings(MasteringSettings s) {
        if (!Enum.IsDefined(s.Character)) {
            throw new EditException($"Unknown character {s.Character}.");
        }

        if (!(s.TargetLufs is >= MinTargetLufs and <= MaxTargetLufs)) {
            throw new EditException($"The loudness target is {MinTargetLufs}…{MaxTargetLufs} LUFS, not {s.TargetLufs}.");
        }

        if (!(s.CeilingDbtp is >= MinCeilingDbtp and <= MaxCeilingDbtp)) {
            throw new EditException($"The ceiling is {MinCeilingDbtp}…{MaxCeilingDbtp} dBTP, not {s.CeilingDbtp}.");
        }

        CheckUnit(s.Eq, "EQ amount");
        CheckUnit(s.Dynamics, "dynamics amount");
        if (!(s.Width is >= 0 and <= MaxMasteringWidth)) {
            throw new EditException($"The width is 0…{MaxMasteringWidth}, not {s.Width}.");
        }
    }

    static DeltaOp[] SetMasteringAnalysis(ProjectState state, SetMasteringAnalysisCommand c) {
        var insert = MasteringAssistantOf(state, c.InsertId ?? string.Empty)
            ?? throw new EditException($"No Mastering Assistant '{c.InsertId}'.");
        var analysis = CheckMasteringAnalysis(c.Analysis);
        var changed = insert with { Mastering = analysis };
        return [
            new MasterChanged(
                state.Master with { Inserts = [..state.Master.Inserts.Select(i => i.Id == insert.Id ? changed : i)] }
            )
        ];
    }

    /// <summary>An analysis as an assistant keeps it: a range, finite readings, a value per band for each list.</summary>
    public static MasteringAnalysis CheckMasteringAnalysis(MasteringAnalysis? analysis) {
        if (analysis is null) {
            throw new EditException("Name the analysis.");
        }

        CheckTick(analysis.StartTick, "analysis start");
        CheckTick(analysis.EndTick, "analysis end");
        if (analysis.EndTick <= analysis.StartTick || analysis.Revision < 0) {
            throw new EditException("An analysis ends after it starts, at a revision of 0 or more.");
        }

        double[] readings = [
            analysis.IntegratedLufs, analysis.MaxShortTermLufs, analysis.LoudnessRange, analysis.TruePeakDbtp,
            analysis.CrestFactorDb, analysis.Width
        ];
        if (readings.Any(static v => !double.IsFinite(v)) || analysis.Width is < 0 or > 1) {
            throw new EditException("An analysis has finite readings and a width of 0…1.");
        }

        var bands = MasteringBands.CentresHz.Count;
        if (analysis.SpectrumDb?.Count != bands
            || analysis.CurveDb?.Count != bands
            || analysis.SpectrumDb.Concat(analysis.CurveDb).Any(static v => !double.IsFinite(v))) {
            throw new EditException($"An analysis has a finite spectrum and curve of {bands} bands each.");
        }

        return analysis with { SpectrumDb = [..analysis.SpectrumDb], CurveDb = [..analysis.CurveDb] };
    }
}
