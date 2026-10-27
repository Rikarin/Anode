namespace Anode.Engine.Plugins;

/// <summary>An instrument the engine activates once it is configured (a hosted plugin).</summary>
public interface IHostedInstrument {
    /// <summary>Control thread, after the state and parameters are set and before the engine swaps it in.</summary>
    void Activate();
}
