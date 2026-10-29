using Anode.Dsp.Instruments;

namespace Anode.ContentBuilder;

/// <summary>A note to render: from frame <c>Start</c> for <c>Length</c> frames.</summary>
public readonly record struct RenderNote(long Start, long Length, int Pitch, int Velocity);

/// <summary>A sound: an instrument of Anode's own and its parameter values by id.</summary>
public sealed record Sound(string PluginId, IReadOnlyDictionary<string, float> Parameters);

/// <summary>
///     Renders notes through Anode's built-in instruments offline, block by block, as the engine plays them (no MIDI FX,
///     no inserts): what the content builder makes audio loops and one-shots of.
/// </summary>
public static class Renderer {
    const int Block = 256;

    /// <summary>
    ///     <paramref name="notes" /> played by <paramref name="sound" /> over <paramref name="frames" /> frames, stereo,
    ///     at <paramref name="gain" />.
    /// </summary>
    public static (float[] Left, float[] Right) Render(
        Sound sound,
        IReadOnlyList<RenderNote> notes,
        long frames,
        float gain = 1
    ) {
        using var instrument = BuiltinInstruments.Create(sound.PluginId)
            ?? throw new InvalidOperationException($"Anode has no instrument '{sound.PluginId}'.");
        instrument.Prepare(PackBuilder.SampleRate, Block);
        foreach (var parameter in instrument.Descriptor.Parameters) {
            if (sound.Parameters.TryGetValue(parameter.Id, out var value)) {
                instrument.SetParameter(parameter.Index, value);
            }
        }

        var events = new List<(long Frame, int Rank, MidiEvent Event)>();
        foreach (var n in notes) {
            events.Add((n.Start, 1, new(0, 0x90, (byte)n.Pitch, (byte)Math.Clamp(n.Velocity, 1, 127))));
            events.Add((n.Start + Math.Max(1, n.Length), 0, new(0, 0x80, (byte)n.Pitch, 0)));
        }

        events.Sort(static (a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Rank.CompareTo(b.Rank));
        var left = new float[frames];
        var right = new float[frames];
        var blockLeft = new float[Block];
        var blockRight = new float[Block];
        var pending = new List<MidiEvent>();
        var next = 0;
        for (long at = 0; at < frames; at += Block) {
            var length = (int)Math.Min(Block, frames - at);
            pending.Clear();
            while (next < events.Count && events[next].Frame < at + length) {
                pending.Add(events[next].Event with { Frame = (int)Math.Max(0, events[next].Frame - at) });
                next++;
            }

            instrument.Process(pending.ToArray(), blockLeft.AsSpan(0, length), blockRight.AsSpan(0, length));
            for (var i = 0; i < length; i++) {
                left[at + i] = blockLeft[i] * gain;
                right[at + i] = blockRight[i] * gain;
            }
        }

        return (left, right);
    }

    /// <summary>Mixes several renders into one (summed), soft-limited to ±1.</summary>
    public static (float[] Left, float[] Right) Mix(params (float[] Left, float[] Right)[] parts) {
        var frames = parts.Max(static p => p.Left.Length);
        var left = new float[frames];
        var right = new float[frames];
        foreach (var (l, r) in parts) {
            for (var i = 0; i < l.Length; i++) {
                left[i] += l[i];
                right[i] += r[i];
            }
        }

        for (var i = 0; i < frames; i++) {
            left[i] = MathF.Tanh(left[i]);
            right[i] = MathF.Tanh(right[i]);
        }

        return (left, right);
    }
}
