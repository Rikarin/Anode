using Anode.Dsp.Spatial;

namespace Anode.Engine.Spatial;

/// <summary>
///     What a mixer plan's build needs of the spatial DSP (control thread): the factory, the rate, and the master's
///     object renderer and the listen-in tap's stereo down-mix, kept while the master's format stays.
/// </summary>
public sealed class SpatialContext(ISpatialFactory factory, float sampleRate) {
    IObjectRenderer? objects;
    SpeakerFormat objectsFormat;
    IDownmix? tap;
    SpeakerFormat tapFormat;

    public static SpatialContext Default { get; } = new(DspSpatial.Instance, 48_000);

    public ISpatialFactory Factory { get; } = factory;

    public float SampleRate { get; } = sampleRate;

    /// <summary>The object renderer for a master of <paramref name="format" />, prepared for every object the ADM profile allows.</summary>
    public IObjectRenderer ObjectRenderer(SpeakerFormat format) {
        if (objects is null || objectsFormat != format) {
            var next = Factory.CreateObjectRenderer();
            next.Prepare(SpeakerLayout.Of(format), Dsp.Spatial.ObjectRenderer.MaxObjects, SampleRate, AudioEngine.MaxFrames);
            objects = next;
            objectsFormat = format;
        }

        return objects;
    }

    /// <summary>The down-mix of a surround master to stereo, for the listen-in tap.</summary>
    public IDownmix TapDownmix(SpeakerFormat format) {
        if (tap is null || tapFormat != format) {
            tap = Factory.CreateDownmix(SpeakerLayout.Of(format), SpeakerLayout.Of(SpeakerFormat.Stereo));
            tapFormat = format;
        }

        return tap;
    }
}
