using System.Security.Cryptography;

namespace Anode.Model;

/// <summary>
///     Makes domain ids: a kind prefix (<c>trk</c>, <c>rgn</c>, <c>mid</c>, <c>aud</c>, <c>prj</c>, <c>tkf</c>,
///     <c>tak</c>,
///     <c>mrk</c>, <c>sec</c>, <c>alt</c>, and Phase 4's <c>ins</c>, <c>snd</c>, <c>aut</c>, <c>pat</c>, <c>job</c>, and
///     Phase 6's <c>ptn</c>, <c>row</c>, <c>scn</c>,
///     <c>cel</c>, and Phase 7's <c>grv</c>, <c>ars</c>, <c>lns</c>, and Phase 9's <c>sts</c>, <c>sym</c>, <c>lyr</c>,
///     <c>chd</c>, <c>scs</c>, and Phase 11's <c>crd</c>, <c>spr</c>, <c>env</c>, <c>eob</c>, <c>ecb</c>), an
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
    public const string Template = "tpl";
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
    public const string PatternRegion = "ptn";
    public const string PatternRow = "row";
    public const string Scene = "scn";
    public const string Cell = "cel";
    public const string Groove = "grv";
    public const string ArticulationSet = "ars";
    public const string LaneSet = "lns";
    public const string StaffStyle = "sts";
    public const string ScoreSymbol = "sym";
    public const string Lyric = "lyr";
    public const string ChordSymbol = "chd";
    public const string ScoreSet = "scs";
    public const string Chord = "crd";
    public const string SessionPlayerRegion = "spr";
    public const string EnvironmentLayer = "env";
    public const string EnvironmentObject = "eob";
    public const string EnvironmentCable = "ecb";

    /// <summary>
    ///     A Session Player seed drawn from a fresh id (<see cref="IIdSource.Next" />): random with the server's ids,
    ///     deterministic with a test's; never negative.
    /// </summary>
    public static long Seed(IIdSource ids) {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ids.Next("sed")));
        return BitConverter.ToInt64(hash) & long.MaxValue;
    }
}
