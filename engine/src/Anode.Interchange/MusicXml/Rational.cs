using System.Numerics;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     An exact fraction of a quarter note, as MusicXML durations are (a duration over the divisions): kept reduced with
///     a positive denominator. Arithmetic is checked, so an absurd file overflows into an exception the reader turns into
///     a <see cref="FormatException" />.
/// </summary>
readonly record struct Rational : IComparable<Rational> {
    public Rational(long numerator, long denominator) {
        if (denominator == 0) {
            throw new FormatException("A duration over zero divisions.");
        }

        if (denominator < 0) {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        var gcd = (long)BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = gcd > 1 ? numerator / gcd : numerator;
        Denominator = gcd > 1 ? denominator / gcd : denominator;
    }

    public long Numerator { get; }

    public long Denominator { get; }

    public static Rational Zero { get; } = new(0, 1);

    public bool IsPositive => Numerator > 0;

    /// <summary>A length in ticks (960 a quarter).</summary>
    public static Rational FromTicks(long ticks) => new(ticks, Ppq);

    /// <summary>The nearest tick (960 PPQ), halves away from zero.</summary>
    public long ToTicks() {
        var scaled = (BigInteger)Numerator * Ppq;
        var quotient = BigInteger.DivRem(scaled, Denominator, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= Denominator) {
            quotient += scaled.Sign;
        }

        return (long)quotient;
    }

    /// <summary>This duration in <paramref name="divisions" /> per quarter, which must express it exactly.</summary>
    public long InDivisions(long divisions) => checked(Numerator * (divisions / Denominator));

    public static Rational operator +(Rational a, Rational b) =>
        new(checked((a.Numerator * b.Denominator) + (b.Numerator * a.Denominator)), checked(a.Denominator * b.Denominator));

    public static Rational operator -(Rational a, Rational b) =>
        new(checked((a.Numerator * b.Denominator) - (b.Numerator * a.Denominator)), checked(a.Denominator * b.Denominator));

    public static Rational operator *(Rational a, Rational b) =>
        new(checked(a.Numerator * b.Numerator), checked(a.Denominator * b.Denominator));

    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;

    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;

    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;

    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;

    public static Rational Max(Rational a, Rational b) => a >= b ? a : b;

    public int CompareTo(Rational other) =>
        ((BigInteger)Numerator * other.Denominator).CompareTo((BigInteger)other.Numerator * Denominator);

    /// <summary>The least common multiple of two positive denominators.</summary>
    public static long Lcm(long a, long b) => checked(a / (long)BigInteger.GreatestCommonDivisor(a, b) * b);

    const long Ppq = 960;
}
