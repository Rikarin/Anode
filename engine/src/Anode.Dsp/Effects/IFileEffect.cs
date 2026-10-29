namespace Anode.Dsp.Effects;

/// <summary>
///     An effect that loads a file (Phase 6: Space's impulse responses), as an instrument does through
///     <c>IInstrument.LoadFile</c>. The engine finds it with a type test on an <see cref="IAudioEffect" />.
///     <see cref="LoadFile" /> runs on the control thread while the effect is not processing; the effect's state carries
///     the file's path, so a saved state reloads it.
/// </summary>
public interface IFileEffect {
    /// <summary>The extensions it loads (<c>.wav</c>), lower case with the dot.</summary>
    IReadOnlyList<string> FileTypes { get; }

    /// <summary>The file loaded, or null.</summary>
    string? File { get; }

    /// <summary>Loads <paramref name="path" />; false when it cannot (the effect keeps what it had).</summary>
    bool LoadFile(string path);
}
