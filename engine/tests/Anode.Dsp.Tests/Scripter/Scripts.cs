using Anode.Dsp.MidiEffects.Scripter;
using Anode.Dsp.Tests.Support;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Tests.Scripter;

/// <summary>Builds Scripter instances for the tests and reads what they trace.</summary>
internal static class Scripts {
    public const double Rate = MidiRun.Rate;

    /// <summary>
    ///     A generous budget for tests that are not about the budget: the machine running the suite is loaded, and a
    ///     block preempted past 25 % of 1.3 ms would stop the script.
    /// </summary>
    public const long RelaxedBudget = 2_000_000;

    public static void SkipWithoutNative() =>
        Assert.SkipUnless(ScripterEffect.Available, "an_script is not built (run engine/native/build.sh)");

    /// <summary>A prepared instance running <paramref name="source" />, which must compile.</summary>
    public static ScripterEffect Create(
        string source,
        ulong seed = 1,
        nuint arenaBytes = 0,
        long budget = RelaxedBudget
    ) {
        SkipWithoutNative();
        var effect = new ScripterEffect(seed, arenaBytes) { BudgetMicroseconds = budget };
        effect.Prepare((float)Rate, 1_024);
        var problem = effect.Compile(source);
        Assert.Null(problem);
        Assert.True(effect.Running);
        return effect;
    }

    /// <summary>Every trace line waiting.</summary>
    public static List<string> Trace(ScripterEffect effect) {
        var lines = new List<string>();
        while (effect.TryReadTrace(out var line)) {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>One block of <paramref name="frames" /> frames at <paramref name="quarter" />.</summary>
    public static TransportContext Block(double quarter = 0, int frames = 64, double bpm = 120, bool playing = true) =>
        new(playing, 0, quarter, bpm, 4, 4, 0, Rate, frames);

    /// <summary>Runs one effect over <paramref name="frames" /> frames at a constant tempo (MidiRun.Run).</summary>
    public static List<OutputEvent> Run(
        ScripterEffect effect,
        int frames,
        IReadOnlyList<TimedEvent> events,
        double bpm = 120,
        bool playing = true,
        int block = 64
    ) =>
        MidiRun.Run(effect, frames, events, bpm, playing, block);
}
