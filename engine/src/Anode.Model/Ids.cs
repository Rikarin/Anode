using System.Security.Cryptography;

namespace Anode.Model;

/// <summary>Makes domain ids: a kind prefix (<c>trk</c>, <c>rgn</c>, <c>aud</c>, <c>prj</c>), an underscore, a suffix.</summary>
public interface IIdSource {
    string Next(string prefix);
}

/// <summary>Random ids (about 62 bits each), safe as file names; what the server uses.</summary>
public sealed class RandomIds : IIdSource {
    const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static RandomIds Instance { get; } = new();

    public string Next(string prefix) => $"{prefix}_{RandomNumberGenerator.GetString(Alphabet, 12)}";
}

/// <summary>Ids from a counter: deterministic, for tests.</summary>
public sealed class SequentialIds : IIdSource {
    long next;

    public string Next(string prefix) => $"{prefix}_{Interlocked.Increment(ref next):D6}";
}

public static class IdPrefix {
    public const string Project = "prj";
    public const string Track = "trk";
    public const string Region = "rgn";
    public const string File = "aud";
}
