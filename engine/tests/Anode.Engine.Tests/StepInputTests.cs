using Anode.Engine.Midi;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 7's MIDI step input on the host: while forwarding is on, live note input (a device's, and the server's
///     live MIDI) is made into steps with the held-key state (keys down before any is released make a chord), and still
///     sounds on the thru track.
/// </summary>
public sealed class StepInputTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly FakeMidi midi = new();
    readonly List<StepInputMidiMessage> steps = [];
    readonly EngineController controller;
    readonly OfflineDriver driver;

    public StepInputTests() {
        var (registry, _) = MidiFixtures.Registry();
        controller = new(new(Rate, Block), registry, midi);
        controller.Midi.StepInput = steps.Add;
        driver = new(controller, Block);
        using var segment = TestAudio.Segment([new(1, [], [])], 1);
        controller.LoadModel(TestAudio.Load(segment, 1, true, [MidiFixtures.InstrumentTrack(1)], [], dir));
        controller.SetMidiThru(1);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    TestInstrument Instrument => (TestInstrument)controller.Engine.Root.Tracks[0].Instrument!.Instrument;

    void Keys(params (int Status, int Note, int Velocity)[] messages) {
        foreach (var (status, note, velocity) in messages) {
            midi.Play(0, (byte)status, (byte)note, (byte)velocity);
        }

        controller.Midi.Pump();
    }

    static (int, int, int) On(int note, int velocity = 100) => (0x90, note, velocity);

    static (int, int, int) Off(int note) => (0x80, note, 0);

    static string Step(StepInputMidiMessage m) => $"{string.Join(' ', m.Pitches)} @{m.Velocity}";

    [Fact]
    public void Held_keys_make_chords_and_single_keys_notes_only_while_forwarding_is_on() {
        Keys(On(60), Off(60));
        Assert.Empty(steps);

        controller.SetStepInputForward(true);
        Assert.True(controller.Midi.StepInputForward);

        // Three keys down before any goes up: one chord, in the order played, at the highest velocity, when the first
        // is released; releasing the others makes no step.
        Keys(On(60, 90), On(64, 110), On(67), Off(64));
        Assert.Equal("60 64 67 @110", Step(Assert.Single(steps)));
        Keys(Off(60), Off(67));
        Assert.Single(steps);

        // A key on its own; a note-on at velocity 0 releases it.
        Keys(On(62, 80), Off(62), On(65, 70), (0x90, 65, 0));
        Assert.Equal(["62 @80", "65 @70"], steps.Skip(1).Select(Step));

        // A key still held from a chord does not join the next step: 72 is down, 74 goes down and up.
        Keys(On(72), On(71), Off(71), On(74), Off(74), Off(72));
        Assert.Equal(["72 71 @100", "74 @100"], steps.Skip(3).Select(Step));

        // The server's live MIDI (the editor's keyboard) is forwarded too, and other messages are not steps.
        Assert.True(
            controller.LiveMidi(
                1,
                [new(0xB0, 64, 127), new(0x90, 48, 60), new(0x91, 52, 90), new(0x80, 48, 0), new(0x81, 52, 0)]
            )
        );
        Assert.Equal("48 52 @90", Step(steps[^1]));

        // The notes sound as well (Logic plays step-entered notes): the thru track heard them all, the first one played
        // before forwarding was on included.
        driver.Process();
        driver.Process();
        Assert.Equal(
            [60, 60, 64, 67, 62, 65, 72, 71, 74, 48, 52],
            Instrument.Events.Where(static e => (e.Status & 0xF0) == 0x90 && e.Data2 > 0)
                .Select(static e => (int)e.Data1)
        );

        // Off: no more steps; on again forgets keys held before.
        var count = steps.Count;
        controller.SetStepInputForward(false);
        Keys(On(70), Off(70), On(76));
        controller.SetStepInputForward(true);
        Keys(Off(76));
        Assert.Equal(count, steps.Count);
    }

    [Fact]
    public void A_chord_tracks_keys_whatever_their_channel() {
        var chord = new StepChord();
        Assert.Null(chord.Take(0x90, 60, 100));
        Assert.Null(chord.Take(0x95, 60, 120)); // the same key on another channel: already down
        Assert.Null(chord.Take(0xA0, 60, 30)); // poly pressure is not a step
        Assert.Null(chord.Take(0x80, 61, 0)); // a key that is not down
        var step = chord.Take(0x85, 60, 0);
        Assert.NotNull(step);
        Assert.Equal("60 @100", Step(step));
    }
}
