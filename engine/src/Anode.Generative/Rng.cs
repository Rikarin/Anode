namespace Anode.Generative;

/// <summary>
///     The generator's randomness: a value hashed from the seed and where it is used (SplitMix64's finaliser over the
///     keys), so a draw never depends on how many came before it. A change in one bar leaves the others' draws alone,
///     and every machine draws the same.
/// </summary>
static class Rng {
    /// <summary>
    ///     64 random bits for <paramref name="seed" /> at <paramref name="a" />, <paramref name="b" /> and
    ///     <paramref name="c" />.
    /// </summary>
    public static ulong Bits(long seed, long a, long b = 0, long c = 0) {
        var x = Mix((ulong)seed ^ 0x9E3779B97F4A7C15UL);
        x = Mix(x ^ (ulong)a);
        x = Mix(x ^ ((ulong)b * 0xBF58476D1CE4E5B9UL));
        return Mix(x ^ ((ulong)c * 0x94D049BB133111EBUL));
    }

    /// <summary>A number in [0, 1), from 53 random bits.</summary>
    public static double Unit(long seed, long a, long b = 0, long c = 0) =>
        (Bits(seed, a, b, c) >> 11) * (1.0 / (1UL << 53));

    /// <summary>
    ///     An integer in −<paramref name="spread" /> … <paramref name="spread" />, triangular (the mean of two uniform
    ///     draws), so small moves are likelier than large ones.
    /// </summary>
    public static int Spread(long seed, long a, long b, long c, int spread) {
        if (spread <= 0) {
            return 0;
        }

        var bits = Bits(seed, a, b, c);
        var width = (ulong)(2 * spread + 1);
        var first = (long)((bits & 0xFFFFFFFF) % width);
        var second = (long)((bits >> 32) % width);
        return (int)((first + second - 2L * spread) / 2);
    }

    static ulong Mix(ulong z) {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
