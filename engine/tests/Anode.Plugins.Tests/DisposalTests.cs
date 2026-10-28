using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using System.Collections.Concurrent;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>Disposing the host and its instances in either order is safe.</summary>
public sealed class DisposalTests : IDisposable {
    const int Frames = 64;
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    [Theory]
    [InlineData(SandboxMode.Grouped)]
    [InlineData(SandboxMode.PerInstance)]
    [InlineData(SandboxMode.InHost)]
    public void Instances_outliving_their_host_are_bypassed_and_dispose_harmlessly(SandboxMode mode) {
        var host = Host(data, mode);
        host.Processing = ProcessingMode.Offline;
        var problems = new ConcurrentQueue<PluginProblem>();
        host.Problem += problems.Enqueue;
        var gain = host.CreateEffect(GainId, new(1, "a"))!;
        var delay = host.CreateEffect(DelayId, new(1, "b"))!;
        var synth = host.CreateInstrument(SynthId, new(2, null))!;
        gain.Prepare(48000, 512);
        delay.Prepare(48000, 512);
        synth.Prepare(48000, 512);
        gain.SetParameter(0, 1f);
        Render(gain, 2, Frames); // working before

        host.Dispose();

        // Everything afterwards is a harmless no-op: blocks pass dry, the instrument is silent.
        var (l, r) = Signal(Frames);
        var dry = (float[])l.Clone();
        gain.SetParameter(0, 0.5f);
        gain.Reset();
        gain.Process(l, r, [], []);
        Assert.Equal(dry, l);
        Assert.True(((ISplitProcessing)delay).Begin(l, r, [], []) is false);
        Assert.False(((ISplitProcessing)delay).End(l, r));
        Assert.Equal(dry, l);
        var x = new float[Frames];
        var y = new float[Frames];
        Array.Fill(x, 1f);
        synth.Process([new MidiEvent(0, 0x90, 60, 100)], x, y);
        Assert.All(x, static v => Assert.Equal(0f, v));
        Assert.True(((IHostedPlugin)gain).IsFailed);
        Assert.Equal(-1, ((IHostedPlugin)gain).OpenEditor(true));
        Assert.False(gain.LoadState([1, 2, 3]));
        gain.SaveState();
        gain.Prepare(44100, 256);
        Assert.Empty(problems); // the host going away is not a plugin's problem

        gain.Dispose();
        delay.Dispose();
        synth.Dispose();
        gain.Dispose(); // twice, too
        host.Dispose();
    }

    [Fact]
    public void A_crashed_sandboxs_instances_outlive_their_host_safely() {
        var host = Host(data);
        host.Processing = ProcessingMode.Offline;
        using var crashed = new ManualResetEventSlim();
        host.Problem += _ => crashed.Set();
        var crasher = host.CreateEffect(MisbehaveId, new(1, "a"))!;
        crasher.Prepare(48000, 512);
        crasher.SetParameter(0, 1f);
        var (l, r) = Signal(Frames);
        crasher.Process(l, r, [], []);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        host.Dispose();
        crasher.Process(l, r, [], []);
        crasher.Dispose();
    }

    [Fact]
    public void Instances_disposed_before_their_host_then_the_host() {
        IAudioEffect effect;
        IInstrument instrument;
        using (var host = Host(data)) {
            effect = host.CreateEffect(GainId, new(1, "a"))!;
            instrument = host.CreateInstrument(SynthId, new(2, null))!;
            effect.Prepare(48000, 512);
            instrument.Prepare(48000, 512);
            Render(effect, 2, Frames);
            effect.Dispose();
            instrument.Dispose();
            Assert.Single(host.Sandboxes); // the vendor's sandbox stays for reuse until the host goes
        }

        effect.Dispose();
        instrument.Dispose();
    }
}
