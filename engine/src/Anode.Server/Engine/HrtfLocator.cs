using Anode.Dsp.Spatial;

namespace Anode.Server.Engine;

/// <summary>
///     The HRTF set binaural listening uses: binaural monitoring and the Binaural Post-Processing insert (Phase 11).
/// </summary>
/// <remarks>
///     Where the set lives (Anode's <c>.anhrtf</c>, converted from SADIE II): <c>Anode:HrtfPath</c> when set; else the
///     first <c>*.anhrtf</c> in the <c>hrtf</c> folder beside the Server (a release bundles it there, as the content
///     folder; the Server's build copies the committed set, its <c>LICENSE</c> and <c>NOTICE</c> there too); else, in
///     development, the committed set found from the Server's folder up (<see cref="HrtfSet.Find" />:
///     <c>engine/src/Anode.Dsp/Spatial/Hrtf</c>). None found: binaural is sent without a set, and the host falls back as
///     it can.
/// </remarks>
public sealed class HrtfLocator(IConfiguration configuration) {
    public const string Extension = ".anhrtf";

    readonly Lazy<string?> path = new(() => Locate(configuration));

    /// <summary>The set's file, or null when there is none.</summary>
    public string? Path => path.Value;

    static string? Locate(IConfiguration configuration) {
        if (configuration["Anode:HrtfPath"] is { Length: > 0 } configured) {
            return System.IO.Path.GetFullPath(configured);
        }

        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "hrtf");
        if (Directory.Exists(bundled)
            && Directory.EnumerateFiles(bundled, "*" + Extension).Order(StringComparer.Ordinal).FirstOrDefault() is
                { } first) {
            return first;
        }

        // Development without a build's copy: the committed set in the checkout (engine/src/Anode.Dsp/Spatial/Hrtf).
        return HrtfSet.Find(AppContext.BaseDirectory);
    }
}
