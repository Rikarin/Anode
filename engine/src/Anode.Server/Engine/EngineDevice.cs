namespace Anode.Server.Engine;

/// <summary>
///     The audio device the engine host runs, from its hello (the in-process engine reports its fake device). A new
///     project takes the device's sample rate unless it asks for another; a project at another rate than the device
///     plays off-speed and cannot record, which the engine status says.
/// </summary>
public sealed class EngineDevice {
    int sampleRate;

    /// <summary>The device's sample rate, or null before any host has said hello.</summary>
    public int? SampleRate => Volatile.Read(ref sampleRate) is > 0 and var rate ? rate : null;

    internal void Set(int rate) => Volatile.Write(ref sampleRate, rate);
}
