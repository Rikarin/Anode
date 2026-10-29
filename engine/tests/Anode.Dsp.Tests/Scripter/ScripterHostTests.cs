using Anode.Dsp.MidiEffects;
using Anode.Dsp.MidiEffects.Scripter;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Scripter;

/// <summary>The engine's view of the Scripter: <see cref="IScriptHost" /> as workstream A drives it.</summary>
public sealed class ScripterHostTests {
    static IScriptHost Host(out IMidiEffect effect) {
        Scripts.SkipWithoutNative();
        effect = BuiltinMidiEffects.Create(ScripterEffect.Id)!;
        effect.Prepare((float)Scripts.Rate, 1_024);
        ((ScripterEffect)effect).BudgetMicroseconds = Scripts.RelaxedBudget;
        return Assert.IsAssignableFrom<IScriptHost>(effect);
    }

    [Fact]
    public void A_compile_gives_the_parameters_and_a_compile_error_is_not_a_run_time_one() {
        var host = Host(out var effect);
        using (effect) {
            Assert.Null(host.Compile("var PluginParameters = [{ name: \"Mix\" }];\nTrace(\"hi\");"));
            Assert.False(host.Faulted);
            Assert.Equal(["Mix"], host.RuntimeParameters.Select(static p => p.Id));
            var lines = new List<string>();
            Assert.Equal(0, host.DrainTrace(lines));
            Assert.Equal(["hi"], lines);
        }

        host = Host(out effect);
        using (effect) {
            Assert.Equal(
                new ScriptError("SyntaxError: unexpected token in expression: ')'", 2),
                host.Compile("var a;\nvar b = );")
            );
            Assert.True(host.Faulted);
            Assert.Null(host.TakeError());
        }
    }

    [Fact]
    public void A_run_time_error_faults_once_with_its_line() {
        var host = Host(out var effect);
        using (effect) {
            Assert.Null(host.Compile("function HandleMIDI(e) {\n  e.send();\n  null.x;\n}"));
            Scripts.Run((ScripterEffect)effect, 64, []);
            Assert.False(host.Faulted);
            Scripts.Run((ScripterEffect)effect, 64, [TimedEvent.On(0, 60)]);
            Assert.True(host.Faulted);
            var error = host.TakeError();
            Assert.Equal(3, error?.Line);
            Assert.Contains("null", error?.Message, StringComparison.Ordinal);
            Assert.Null(host.TakeError());
            Assert.True(host.Faulted);
        }
    }

    [Fact]
    public void Idle_collects_garbage_without_waiting() {
        var host = Host(out var effect);
        using (effect) {
            var scripter = (ScripterEffect)effect;
            Assert.Null(
                host.Compile("function HandleMIDI(e) { for (var i = 0; i < 200; i++) { var a = {}; a.self = a; } }")
            );
            Scripts.Run(scripter, 64 * 50, [..Enumerable.Range(0, 50).Select(static i => TimedEvent.On(i * 64, 60))]);
            var before = scripter.MemoryUsed;
            host.Idle();
            Assert.True(scripter.MemoryUsed < before - 100_000, $"{scripter.MemoryUsed} of {before}");
        }
    }
}
