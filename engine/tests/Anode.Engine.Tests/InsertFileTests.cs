using Anode.Dsp.Effects;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     An insert's file (Phase 6, <c>loadInsertFile</c>): the control thread loads it into an <see cref="IFileEffect" />
///     (the convolution reverb's impulse response) when it builds the insert; another file is a new instance, as an
///     instrument's is.
/// </summary>
public sealed class InsertFileTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();
    readonly string first;
    readonly string second;
    readonly string mono;

    public InsertFileTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, 2 * Rate, 1);
        first = TestAudio.Write24(dir, "Hall.wav", 1, Rate / 4, 1);
        second = TestAudio.Write24(dir, "Room.wav", 1, Rate / 8, 1);
    }

    public void Dispose() => Directory.Delete(dir, true);

    EngineController Load(params EngineInsert[] inserts) {
        var controller = new EngineController(new(Rate, 64), TestEffects.Registry());
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

    static Mixing.InsertSlot Slot(EngineController controller) =>
        controller.Engine.Root.Tracks[0].Channel.Config.Inserts.Single().Slot;

    static EngineInsert Space(string? file) =>
        new("r", "anode.space", false, new Dictionary<string, double>(), File: file);

    [Fact]
    public void An_insert_loads_its_file_and_another_file_makes_a_new_instance() {
        using var controller = Load(Space(first));
        var slot = Slot(controller);
        Assert.Equal(first, ((IFileEffect)slot.Effect!).File);
        Assert.Equal(first, slot.File);

        Assert.True(controller.SetChannel(1, new(1f, 0f, false, false, [Space(first)])));
        Assert.Same(slot, Slot(controller));

        Assert.True(controller.SetChannel(1, new(1f, 0f, false, false, [Space(second)])));
        Assert.NotSame(slot, Slot(controller));
        Assert.Equal(second, ((IFileEffect)Slot(controller).Effect!).File);
    }
}
