using System.Security.Cryptography;

namespace Anode.Model;

/// <summary>
///     Makes domain ids: a kind prefix (<c>trk</c>, <c>rgn</c>, <c>mid</c>, <c>aud</c>, <c>prj</c>, <c>tkf</c>,
///     <c>tak</c>,
///     <c>mrk</c>, <c>sec</c>, <c>alt</c>, and Phase 4's <c>ins</c>, <c>snd</c>, <c>aut</c>, <c>pat</c>, <c>job</c>), an
///     underscore, a
///     suffix.
/// </summary>
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
    public const string MidiRegion = "mid";
    public const string TakeFolder = "tkf";
    public const string Take = "tak";
    public const string Marker = "mrk";
    public const string Section = "sec";
    public const string Alternative = "alt";
    public const string Insert = "ins";
    public const string Send = "snd";
    public const string Automation = "aut";
    public const string Patch = "pat";
    public const string Job = "job";
}
