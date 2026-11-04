using Anode.Contracts;

namespace Anode.Server.Projects;

/// <summary>Save As and Save a Copy as jobs: a package can be many gigabytes, and the job can be cancelled.</summary>
public sealed partial class Jobs {
    /// <summary>Queues Save As, its target checked first: 409 when something is there, 422 when it cannot be.</summary>
    public Job SaveAs(SaveProjectAsRequest? request) {
        var target = session.SaveTarget(request?.Path);
        return Enqueue(
            JobKind.SaveAs,
            async (entry, token) => {
                var states = await engine.InstrumentStatesAsync(token).ConfigureAwait(false);
                await Task.Run(() => session.SaveAs(target, states, p => Progress(entry, p), token), token)
                    .ConfigureAwait(false);
                return [target];
            }
        );
    }

    /// <summary>Queues Save a Copy, its target checked first.</summary>
    public Job SaveCopy(SaveProjectAsRequest? request) {
        var target = session.SaveTarget(request?.Path);
        return Enqueue(
            JobKind.SaveCopy,
            async (entry, token) => {
                var states = await engine.InstrumentStatesAsync(token).ConfigureAwait(false);
                await Task.Run(() => session.SaveCopy(target, states, p => Progress(entry, p), token), token)
                    .ConfigureAwait(false);
                return [target];
            }
        );
    }
}
