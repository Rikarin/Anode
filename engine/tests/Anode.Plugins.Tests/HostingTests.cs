using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>Plugins in sandboxes: processing, parameters, state, latency, sidechain, instruments.</summary>
public sealed class HostingTests : IDisposable {
    const int Frames = 64;
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    static readonly PluginOwner Owner = new(1, "insert-1");

    static Action<int> Automation(IAudioEffect effect) =>
        b => {
            // Gain and tone move every few blocks, as automation would.
            if (b % 7 == 0) {
                effect.SetParameter(0, 0.3f + 0.05f * (b % 9));
            }

            if (b % 11 == 0) {
                effect.SetParameter(1, b % 22 == 0 ? 1f : 0.4f + 0.01f * b % 0.5f);
            }
        };

    [Fact]
    public void A_sandboxed_effect_is_bit_exact_against_the_same_plugin_in_process() {
        using var sandboxed = Host(data);
        sandboxed.Processing = ProcessingMode.Offline;
        using var local = new PluginHost(Options(data.Path, SandboxMode.InHost));
        using var a = sandboxed.CreateEffect(GainId, Owner)!;
        using var b = local.CreateEffect(GainId, Owner)!;
        a.Prepare(48000, 512);
        b.Prepare(48000, 512);

        var x = Render(a, 300, Frames, Automation(a));
        var y = Render(b, 300, Frames, Automation(b));

        Assert.Equal(y.Left, x.Left);
        Assert.Equal(y.Right, x.Right);
        Assert.Equal(300, ((IHostedPlugin)a).Stats.Completed);
        Assert.Equal(0, ((IHostedPlugin)a).Stats.Misses);
        // Not a pass-through: the gain and filter did something.
        var (dry, _) = Signal(Frames * 300);
        Assert.NotEqual(dry, x.Left);
    }

    [Fact]
    public void The_gain_parameter_changes_the_level() {
        using var host = Host(data);
        host.Processing = ProcessingMode.Offline;
        using var gain = host.CreateEffect(GainId, Owner)!;
        gain.Prepare(48000, 512);
        gain.SetParameter(0, 1f); // +12 dB
        var (l, r) = Signal(Frames);
        var expected = l.Select(v => v * MathF.Pow(10, 12 / 20f)).ToArray();
        gain.Process(l, r, [], []);
        for (var i = 0; i < Frames; i++) {
            Assert.Equal(expected[i], l[i], 4);
        }

        Assert.Equal(1f, gain.GetParameter(0));
    }

    [Fact]
    public void State_round_trips_through_a_new_instance() {
        using var host = Host(data);
        host.Processing = ProcessingMode.Offline;
        byte[] state;
        float[] reference;
        using (var a = host.CreateEffect(GainId, Owner)!) {
            a.Prepare(48000, 512);
            a.SetParameter(0, 0.25f);
            a.SetParameter(1, 0.5f);
            Render(a, 2, Frames); // the processor takes the values on its next block
            state = a.SaveState()!;
            Assert.NotEmpty(state);
            a.Reset();
            reference = Render(a, 4, Frames).Left;
        }

        using var b = host.CreateEffect(GainId, Owner)!;
        b.Prepare(48000, 512);
        Assert.True(b.LoadState(state));
        Assert.Equal(0.25f, b.GetParameter(0), 6);
        Assert.Equal(0.5f, b.GetParameter(1), 6);
        b.Reset();
        Assert.Equal(reference, Render(b, 4, Frames).Left);

        // A state loaded before Prepare is applied once it is.
        using var c = host.CreateEffect(GainId, Owner)!;
        Assert.True(c.LoadState(state));
        c.Prepare(48000, 512);
        Assert.Equal(0.25f, c.GetParameter(0), 6);
        Assert.Equal(reference, Render(c, 4, Frames).Left);
    }

    [Fact]
    public void Latency_is_reported_and_the_sidechain_reaches_the_plugin() {
        using var host = Host(data);
        host.Processing = ProcessingMode.Offline;
        using var delay = host.CreateEffect(DelayId, Owner)!;
        Assert.True(delay.Descriptor.HasSidechain);
        delay.Prepare(48000, 512);
        Assert.Equal(64, delay.LatencySamples);

        var (inL, inR) = Signal(256);
        var sc = Enumerable.Repeat(0.125f, 256).ToArray();
        var l = (float[])inL.Clone();
        var r = (float[])inR.Clone();
        delay.Process(l, r, sc, sc);
        for (var i = 0; i < 256; i++) {
            var delayed = i >= 64 ? inL[i - 64] : 0f;
            Assert.Equal(delayed + 0.125f, l[i], 6);
        }

        // Without a sidechain source: only the delayed input.
        var l2 = (float[])inL.Clone();
        var r2 = (float[])inR.Clone();
        delay.Process(l2, r2, [], []);
        Assert.Equal(inL[256 - 64], l2[0], 6);
    }

    [Fact]
    public void A_sandboxed_instrument_plays_notes_bit_exact_against_in_process() {
        using var sandboxed = Host(data);
        sandboxed.Processing = ProcessingMode.Offline;
        using var local = new PluginHost(Options(data.Path, SandboxMode.InHost));
        using var a = sandboxed.CreateInstrument(SynthId, new(2, null))!;
        using var b = local.CreateInstrument(SynthId, new(2, null))!;
        Assert.Null(sandboxed.CreateEffect(SynthId, Owner));
        a.Prepare(48000, 512);
        b.Prepare(48000, 512);
        MidiEvent[] Events(int block) =>
            block switch {
                1 => [new(10, 0x90, 60, 100), new(20, 0x90, 67, 90)],
                30 => [new(0, 0x80, 60, 0)],
                _ => []
            };

        var x = Render(a, 60, Frames, Events);
        var y = Render(b, 60, Frames, Events);
        Assert.Equal(y.Left, x.Left);
        Assert.Contains(x.Left, v => MathF.Abs(v) > 0.05f);
        Assert.All(x.Left.Take(Frames), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Instances_of_one_vendor_share_a_sandbox_and_per_instance_mode_does_not() {
        using (var grouped = Host(data)) {
            using var a = grouped.CreateEffect(GainId, Owner)!;
            using var b = grouped.CreateEffect(DelayId, Owner)!;
            a.Prepare(48000, 512);
            b.Prepare(48000, 512);
            var box = Assert.Single(grouped.Sandboxes);
            Assert.Equal("Anode", box.Group);
            Assert.Equal(2, box.Instances);
        }

        using var separate = Host(data, SandboxMode.PerInstance);
        using var c = separate.CreateEffect(GainId, Owner)!;
        using var d = separate.CreateEffect(GainId, Owner)!;
        Assert.Equal(2, separate.Sandboxes.Count);
    }

    [Fact]
    public void An_unknown_plugin_is_null() {
        using var host = Host(data);
        Assert.Null(host.CreateEffect("vst3:00000000000000000000000000000000", Owner));
        Assert.Null(host.CreateInstrument(GainId, Owner));
        Assert.False(host.Knows("au:nope.nope.nope"));
        Assert.True(host.Knows(GainId));
    }

    [Fact]
    public void The_sandbox_audio_thread_is_real_time() {
        using var host = Host(data);
        host.ConfigureDevice(48000, 64, null);
        using var gain = host.CreateEffect(GainId, Owner)!;
        gain.Prepare(48000, 512);
        Render(gain, 10, Frames);
        var pid = ((IHostedPlugin)gain).Stats.SandboxPid;
        var status = host.SandboxStatus(pid)!.Value;
        Assert.Equal(0, status.RtStatus);
        Assert.True(status.Wakes >= 10);
    }
}
