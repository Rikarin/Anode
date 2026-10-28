namespace Anode.Engine;

// Offline renders (docs/05-phase-4.md, "Rendering"): the render thread takes the engine over from the device
// (BeginOffline), and these run on it, as the audio thread, around its ProcessOffline calls.
public sealed partial class AudioEngine {
    long savedPosition;
    Cycle savedCycle;

    /// <summary>
    ///     Offline render thread: stops the transport, turns the cycle off, locates to <paramref name="start" />,
    ///     silences every tail and history (so the render does not depend on what played before) and publishes the
    ///     transport for the disk thread. <see cref="StartRender" /> then rolls it once the disk has primed.
    /// </summary>
    public void BeginRender(long start) {
        ApplyCommands();
        savedPosition = transport.Position;
        savedCycle = transport.Cycle;
        countInRemaining = 0;
        transport.Stop();
        transport.SetCycle(Cycle.Off);
        transport.Locate(Math.Max(0, start));
        hardFloor = transport.Anchor.Id;
        Panic();
        var model = Volatile.Read(ref root);
        ResetMix(model);
        PublishSnapshot(transport.Clock, transport.Position, Snapshot.HostTimeNs, model);
    }

    /// <summary>The transport's clock and anchor id, for the thread running the engine (an offline render).</summary>
    public (long Clock, long AnchorId) RenderTransport => (transport.Clock, transport.Anchor.Id);

    /// <summary>Offline render thread: rolls the transport from the render's start.</summary>
    public void StartRender() => transport.Play();

    /// <summary>
    ///     Offline render thread: stops, restores the cycle and the playhead, silences the render's tails and
    ///     publishes the transport.
    /// </summary>
    public void EndRender() {
        Rendering = null;
        transport.Stop();
        transport.SetCycle(savedCycle);
        transport.Locate(savedPosition);
        hardFloor = transport.Anchor.Id;
        Panic();
        var model = Volatile.Read(ref root);
        ResetMix(model);
        PublishSnapshot(transport.Clock, transport.Position, Snapshot.HostTimeNs, model);
    }
}
