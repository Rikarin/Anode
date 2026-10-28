using Anode.Audio;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     Channel structure on the control thread: <c>setChannel</c> keeping instances by insert id, bypass, insert
///     parameters and states, the master's inserts, and the plugin factory seam workstream E plugs into (instances,
///     callbacks, editors, the catalogue); and the host's Phase 4 messages.
/// </summary>
public sealed class ChannelTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;

    public ChannelTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, 2 * Rate, 1);
    }

    public void Dispose() => Directory.Delete(dir, true);

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Dictionary<string, double> Gain(double gain) => new() { ["gain"] = gain };

    EngineController Load(TestFactory? factory = null, params EngineInsert[] inserts) {
        var registry = TestEffects.Registry();
        if (factory is not null) {
            registry.Factory = factory;
        }

        var controller = new EngineController(new(Rate, 64), registry);
        using var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, 2 * Rate, 0)])], 1);
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                [new(1, -1, false, false, false, new(1f, 0f, false, false, inserts))],
                [new(0, mono, 1, Rate, 2 * Rate)],
                dir
            )
        );
        return controller;
    }

    static float[] Play(EngineController controller, int frames) {
        using var driver = new OfflineDriver(controller, 64);
        driver.Send(new(EngineCommandKind.Stop));
        driver.PlayFrom(0);
        driver.Render(64); // past the gain ramps a change starts
        return driver.Render(frames).Left;
    }

    static Mixing.InsertSlot Slot(EngineController controller, int track, string id) =>
        (track == -1 ? controller.Engine.MasterChannel : controller.Engine.Root.Tracks[0].Channel).Config.Inserts
        .Single(i => i.Slot.Id == id)
        .Slot;

    [Fact]
    public void Set_channel_keeps_instances_by_insert_id_bypasses_and_replaces() {
        using var controller = Load(null, TestEffects.Insert("a", TestEffects.Gain, Gain(0.5)));
        var a = Slot(controller, 1, "a");
        Assert.Contains(Play(controller, 640), static v => v != 0);

        // Same id and plugin, now bypassed, plus a new insert: the instance stays.
        Assert.True(
            controller.SetChannel(
                1,
                new(1f, 0f, false, false, [
                    TestEffects.Insert("a", TestEffects.Gain, Gain(0.5), true),
                    TestEffects.Insert("b", TestEffects.Gain, Gain(0))
                ])
            )
        );
        Assert.Same(a, Slot(controller, 1, "a"));
        Assert.All(Play(controller, 640), static v => Assert.Equal(0f, v)); // b mutes it

        // Insert parameters by id; an unknown one is refused.
        Assert.True(controller.SetInsertParameter(1, "b", "gain", 1));
        Assert.False(controller.SetInsertParameter(1, "b", "nope", 1));
        Assert.False(controller.SetInsertParameter(1, "zz", "gain", 1));
        Assert.Contains(Play(controller, 640), static v => v != 0);

        // Another plugin under the same id: a new instance, the old one disposed.
        var b = (GainEffect)Slot(controller, 1, "b").Effect!;
        Assert.True(controller.SetChannel(1, new(1f, 0f, false, false, [TestEffects.Insert("b", TestEffects.Delay)])));
        Assert.True(b.Disposed);
        Assert.True(((GainEffect)a.Effect!).Disposed);
        Assert.IsType<DelayEffect>(Slot(controller, 1, "b").Effect);
        Assert.False(controller.SetChannel(42, new(1f, 0f, false, false)));
    }

    [Fact]
    public void The_masters_inserts_run_and_insert_states_are_saved_by_track_and_insert() {
        using var controller = Load(null, TestEffects.Insert("a", TestEffects.Gain, Gain(0.25)));
        Assert.True(controller.SetChannel(-1, new(1f, 0f, false, false, [TestEffects.Insert("m", TestEffects.Gain, Gain(2))])));
        var left = Play(controller, 640);
        for (var n = 0; n < 640; n++) {
            Assert.Equal(TestAudio.Sample(64 + n, 1) * 0.25f * 2f, left[n]);
        }

        var states = controller.InstrumentStates();
        Assert.Contains(states, static s => s is { TrackId: 1, InsertId: "a" } && s.State == Convert.ToBase64String(BitConverter.GetBytes(0.25f)));
        Assert.Contains(states, static s => s is { TrackId: -1, InsertId: "m" });
    }

    [Fact]
    public void The_factory_creates_hosted_plugins_and_relays_their_parameters_problems_and_editors() {
        var factory = new TestFactory();
        using var controller = Load(factory, TestEffects.Insert("h", TestFactory.Hosted, Gain(0.5)), TestEffects.Insert("x", "nobody.has.this"));
        var problems = new List<PluginProblemMessage>();
        var changes = new List<ParameterChangedMessage>();
        controller.PluginProblem = problems.Add;
        controller.ParameterChanged = changes.Add;
        Assert.Contains(controller.Plugins.Catalog(), static p => p.Id == TestFactory.Hosted && p.Format == "vst3");

        var (context, effect) = Assert.Single(factory.Created);
        Assert.Equal((1, "h", TestFactory.Hosted), (context.TrackId, context.InsertId, context.PluginId));
        Assert.Equal(0.5f, effect.GetParameter(0));
        Assert.True(controller.OpenPluginEditor(1, "h", true));
        Assert.True(effect.EditorOpen);
        Assert.False(controller.OpenPluginEditor(1, "x", true)); // failed to load: no editor
        Assert.False(controller.OpenPluginEditor(1, null, true)); // no instrument

        context.Callbacks.ParameterChanged("gain", 0.75);
        Assert.Equal(new ParameterChangedMessage(1, "h", "gain", 0.75), Assert.Single(changes));

        var left = Play(controller, 640);
        Assert.Equal(TestAudio.Sample(64, 1) * 0.5f, left[0]);
        context.Callbacks.Problem("crashed");
        Assert.Equal(new PluginProblemMessage(1, "h", TestFactory.Hosted, "crashed"), problems[^1]);
        left = Play(controller, 640);
        Assert.Equal(TestAudio.Sample(64, 1), left[0]); // bypassed: dry

        // The insert that failed to load was reported when the model loaded (before the handler was set), so a
        // structure change that re-adds it reports it again.
        controller.SetChannel(1, new(1f, 0f, false, false, [TestEffects.Insert("y", "nobody.has.this")]));
        Assert.Contains(problems, static p => p is { InsertId: "y", PluginId: "nobody.has.this" });
    }

    [Fact]
    public async Task The_host_takes_the_mixer_messages_and_renders_in_the_background() {
        using var host = new EngineHost.EngineHost(new NullAudioDevice(Rate, 64, 2, 2), "test", false, TestEffects.Registry());
        var pushed = new List<IpcMessage>();
        host.Push = m => {
            lock (pushed) {
                pushed.Add(m);
            }
        };
        host.Start();
        using (var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, 2 * Rate, 0)])], 1)) {
            Assert.IsType<ModelLoadedMessage>(
                await host.HandleAsync(
                    TestAudio.Load(segment, 1, true, [TestAudio.Track(1)], [new(0, mono, 1, Rate, 2 * Rate)], dir),
                    Token
                )
            );
        }

        EngineStrip strip = new(1f, 0f, false, false, [TestEffects.Insert("g", TestEffects.Gain)], [new("s", 1, 0.5f, 0f, false)]);
        Assert.Null(await host.HandleAsync(new SetChannelMessage(1, strip), Token));
        Assert.Null(await host.HandleAsync(new SetInsertParameterMessage(1, "g", "gain", 0.5), Token));
        Assert.Null(await host.HandleAsync(new SetSendLevelMessage(1, "s", 0.25f, 0f), Token));
        Assert.Null(await host.HandleAsync(new SetMixerMessage("tracks", true, 3), Token));
        Assert.IsType<ErrorMessage>(await host.HandleAsync(new SetMixerMessage("sometimes", false, 0), Token));
        Assert.IsType<ErrorMessage>(await host.HandleAsync(new SetChannelMessage(9, strip), Token));
        Assert.IsType<ErrorMessage>(await host.HandleAsync(new OpenPluginEditorMessage(1, "g", true), Token));

        var path = Path.Combine(dir, "host.wav");
        Assert.Null(await host.HandleAsync(new RenderMessage(7, 0, Rate, path), Token));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        RenderFinishedMessage? finished = null;
        while (finished is null && DateTime.UtcNow < deadline) {
            await Task.Delay(10, Token);
            lock (pushed) {
                finished = pushed.OfType<RenderFinishedMessage>().FirstOrDefault();
            }
        }

        Assert.NotNull(finished);
        Assert.Null(finished.Error);
        Assert.Equal([path], finished.Files);
        Assert.True(new FileInfo(path).Length > Rate * 8);
        var states = Assert.IsType<InstrumentStatesMessage>(await host.HandleAsync(new RequestInstrumentStatesMessage(), Token));
        Assert.Contains(states.States, static s => s is { TrackId: 1, InsertId: "g" });
    }
}
