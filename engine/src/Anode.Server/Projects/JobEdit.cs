using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;

namespace Anode.Server.Projects;

/// <summary>
///     The edits a finished job makes, as one undo step (<see cref="ProjectSession.ApplyJob" />): commands applied in
///     turn, each seeing the state the ones before left, their operations and inverses collected.
/// </summary>
public sealed class JobEdit(ProjectState state, IIdSource ids, PluginCatalog plugins) {
    public ProjectState State { get; private set; } = state;

    public IIdSource Ids { get; } = ids;

    public List<DeltaOp> Forward { get; } = [];

    public List<DeltaOp> Inverse { get; } = [];

    /// <summary>Applies a command; its operations.</summary>
    public IReadOnlyList<DeltaOp> Apply(EditCommand command) {
        var ops = Edits.Ops(State, command, Ids, plugins);
        Step(ops);
        return ops;
    }

    public void Step(IEnumerable<DeltaOp> ops) => State = Edits.Step(State, ops, Forward, Inverse);
}
