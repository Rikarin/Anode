namespace Anode.Model;

/// <summary>Level conversions between the domain (dB) and the engine (linear gain).</summary>
public static class Units {
    /// <summary>The volume that stands for −∞ dB.</summary>
    public const double SilenceDb = -144;

    public static float DbToGain(double db) => db <= SilenceDb ? 0f : (float)Math.Pow(10, db / 20);
}
