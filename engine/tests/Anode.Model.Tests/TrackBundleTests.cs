using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Storage;

namespace Anode.Model.Tests;

/// <summary>Tracks leaving one project for another (<see cref="TrackBundle" />, <see cref="Edits.InsertTracks" />).</summary>
public sealed class TrackBundleTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-bundle-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    sealed class CountingIds : IIdSource {
        long next;

        public string Next(string prefix) => $"{prefix}_x{++next}";
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Every_track_of_a_random_project_goes_into_another_and_back_out_with_undo(int seed) {
        var (source, _, _) = RandomProjects.Build(seed, 80);
        var (target, _, _) = RandomProjects.Build(seed + 100, 40);
        var bundle = TrackBundle.Of(source, source.Tracks.Select(static t => t.Id), new());
        var ids = new CountingIds();
        // The files arrive first, under new ids in real use; here the ids of two seeds do not meet.
        var forward = new List<DeltaOp>();
        var inverse = new List<DeltaOp>();
        var state = Edits.Step(target, bundle.Files.Select(static f => new AudioFileUpserted(f)), forward, inverse);

        TrackInsertion inserted;
        try {
            inserted = Edits.InsertTracks(state, bundle, ids);
        } catch (EditException) {
            return; // more buses or tracks than the target has room for: refused whole
        }

        state = Edits.Step(state, inserted.Ops, forward, inverse);

        Assert.Equal(target.Tracks.Count + source.Tracks.Count, state.Tracks.Count);
        Assert.Equal(state.Tracks.Count, state.Tracks.Select(static t => t.Id).Distinct().Count());
        Assert.Equal(target.Regions.Count + source.Regions.Count, state.Regions.Count);
        Assert.Equal(target.MidiRegions.Count + source.MidiRegions.Count, state.MidiRegions.Count);
        Assert.Null(Routing.Loop(state));
        foreach (var region in state.MidiRegions.Values) {
            Assert.Equal(region.Notes.Count, region.Notes.Select(static n => n.Id).Distinct().Count());
            Assert.NotNull(state.FindTrack(region.TrackId));
        }

        var added = state.Tracks.Skip(target.Tracks.Count).ToArray();
        var ownBuses = target.Tracks.Where(static t => t.Kind == TrackKind.Aux)
            .Select(static t => t.InputBus)
            .ToHashSet();
        Assert.All(
            added.SelectMany(Routing.Feeds),
            bus => Assert.DoesNotContain(bus, ownBuses)
        );

        // It stores and reads back as it is.
        ProjectPackage.Save(folder, state);
        Assert.Equal(state.Tracks.Count, ProjectPackage.Load(folder).Tracks.Count);

        // And it comes back out whole.
        inverse.Reverse();
        Assert.Equal(RandomProjects.Json(target), RandomProjects.Json(state.Apply(inverse)));
    }

    [Fact]
    public void An_aux_comes_along_through_a_chain_of_buses_and_a_stack_with_its_members() {
        var state = ProjectDefaults.NewProject("prj_a", "A", 48_000);
        var ids = new CountingIds();
        var plugins = RandomProjects.Plugins;
        state = Edits.Apply(state, new AddTrackCommand("Lead"), ids, plugins).State;
        state = Edits.Apply(state, new AddAuxCommand(1, "One"), ids, plugins).State;
        state = Edits.Apply(state, new AddAuxCommand(2, "Two"), ids, plugins).State;
        state = Edits.Apply(state, new AddAuxCommand(3, "Lonely"), ids, plugins).State;
        var (lead, one) = (state.Tracks[0].Id, state.Tracks[1].Id);
        state = Edits.Apply(state, new AddSendCommand(lead, 1), ids, plugins).State;
        state = Edits.Apply(state, new SetOutputCommand(one, "bus:2"), ids, plugins).State;

        var bundle = TrackBundle.Of(state, [lead], new());

        Assert.Equal(["Lead", "One", "Two"], bundle.Tracks.Select(static t => t.Name));
        Assert.Equal(["Lead"], TrackBundle.Of(state, [lead], new(Sends: false)).Tracks.Select(static t => t.Name));
    }
}
