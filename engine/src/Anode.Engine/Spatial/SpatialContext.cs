using Anode.Dsp.Spatial;

namespace Anode.Engine.Spatial;

/// <summary>
///     What a mixer plan's build needs of the spatial DSP (control thread): the factory, the rate, and the master's
///     object renderer and the listen-in tap's stereo down-mix, kept while the master's format stays.
///     <para>
///         A piece of spatial DSP that cannot be made (workstream D's stubs throw until D merges) is logged and left out:
///         a panned edge then carries its channels by label, objects are panned as beds, the tap takes the front pair.
///     </para>
/// </summary>
public sealed class SpatialContext(ISpatialFactory factory, float sampleRate) {
    IObjectRenderer? objects;
    SpeakerFormat? objectsFormat;
    IDownmix? tap;
    SpeakerFormat? tapFormat;

    public static SpatialContext Default { get; } = new(DspSpatial.Instance, 48_000);

    public ISpatialFactory Factory { get; } = factory;

    public float SampleRate { get; } = sampleRate;

    /// <summary>Where it reports a piece of spatial DSP it could not make.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    ///     The object renderer for a master of <paramref name="format" />, prepared for every object the ADM profile
    ///     allows (null: none can be made).
    /// </summary>
    public IObjectRenderer? ObjectRenderer(SpeakerFormat format) {
        if (objectsFormat != format) {
            objectsFormat = format;
            objects = Try(
                () => {
                    var next = Factory.CreateObjectRenderer();
                    next.Prepare(
                        SpeakerLayout.Of(format),
                        Dsp.Spatial.ObjectRenderer.MaxObjects,
                        SampleRate,
                        AudioEngine.MaxFrames
                    );
                    return next;
                },
                "the object renderer"
            );
        }

        return objects;
    }

    /// <summary>The down-mix of a surround master to stereo, for the listen-in tap (null: none can be made).</summary>
    public IDownmix? TapDownmix(SpeakerFormat format) {
        if (tapFormat != format) {
            tapFormat = format;
            tap = Try(
                () => Factory.CreateDownmix(SpeakerLayout.Of(format), SpeakerLayout.Of(SpeakerFormat.Stereo)),
                "the listen-in down-mix"
            );
        }

        return tap;
    }

    /// <summary>Makes a piece of spatial DSP, or logs why it cannot and returns null.</summary>
    public T? Try<T>(Func<T> create, string what) where T : class {
        try {
            return create();
        } catch (Exception e) when (e is NotImplementedException
                                        or NotSupportedException
                                        or InvalidOperationException
                                        or ArgumentException
                                        or IOException
                                        or InvalidDataException) {
            Log?.Invoke($"surround: {what} is not available ({e.Message})");
            return null;
        }
    }
}
