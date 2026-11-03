using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     The style library (workstream D): versioned data in the repository, authored for Anode (JSON phrase banks, the
///     variation grammar, feels and presets per style; ADR-0014), embedded in this assembly from <c>Styles/</c>. Loaded
///     once and immutable; its <see cref="Version" /> is part of every generation input, so a changed library
///     regenerates the regions.
/// </summary>
public sealed class StyleLibrary {
    /// <summary>The loader's (<see cref="Builtin" />, <see cref="Load" />), from parsed style data.</summary>
    internal StyleLibrary() { }

    /// <summary>The library's version (its own field, bumped with any change to its data).</summary>
    public string Version => throw Pending.WorkstreamD();

    /// <summary>The catalogue the API serves (<c>GET /library/session-players</c>).</summary>
    public SessionPlayerCatalog Catalog => throw Pending.WorkstreamD();

    /// <summary>The library embedded in this assembly (<c>Styles/</c>).</summary>
    public static StyleLibrary Builtin() => throw Pending.WorkstreamD();

    /// <summary>A library from a folder of style files (tests, authoring); invalid data throws with the file named.</summary>
    public static StyleLibrary Load(string folder) => throw Pending.WorkstreamD();
}
