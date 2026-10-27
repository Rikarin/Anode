using Anode.Dsp.Instruments;

namespace Anode.Dsp.Tests.Support;

/// <summary>A MIDI message at an absolute frame of a render.</summary>
public readonly record struct TimedEvent(long Frame, byte Status, byte Data1, byte Data2) {
    public static TimedEvent On(long frame, int note, int velocity = 100) =>
        new(frame, 0x90, (byte)note, (byte)velocity);

    public static TimedEvent Off(long frame, int note) => new(frame, 0x80, (byte)note, 0);

    public static TimedEvent Cc(long frame, int controller, int value) =>
        new(frame, 0xB0, (byte)controller, (byte)value);

    /// <summary>Pitch bend, −8192 … 8191.</summary>
    public static TimedEvent Bend(long frame, int value) =>
        new(frame, 0xE0, (byte)((value + 8192) & 0x7F), (byte)(((value + 8192) >> 7) & 0x7F));
}

/// <summary>Renders instruments for the tests.</summary>
public static class Play {
    public const float Rate = 48_000;
    public const int Block = 64;

    /// <summary>A new, prepared instance of <paramref name="id" /> with <paramref name="parameters" /> set.</summary>
    public static IInstrument Create(string id, params (int Index, float Value)[] parameters) {
        var instrument = BuiltinInstruments.Create(id) ?? throw new InvalidOperationException(id);
        instrument.Prepare(Rate, 1_024);
        foreach (var (index, value) in parameters) {
            instrument.SetParameter(index, value);
        }

        return instrument;
    }

    /// <summary>
    ///     Renders <paramref name="frames" /> frames, buffers of <paramref name="block" /> frames (or the sizes
    ///     <paramref name="blocks" /> cycles through), each event in the buffer it falls in at its offset.
    /// </summary>
    public static (float[] Left, float[] Right) Render(
        IInstrument instrument,
        int frames,
        IReadOnlyList<TimedEvent> events,
        int block = Block,
        int[]? blocks = null
    ) {
        var left = new float[frames];
        var right = new float[frames];
        var buffer = new List<MidiEvent>();
        var sorted = events.OrderBy(static e => e.Frame).ToArray();
        var next = 0;
        var cycle = 0;
        for (var at = 0; at < frames;) {
            var count = Math.Min(blocks is null ? block : blocks[cycle++ % blocks.Length], frames - at);
            buffer.Clear();
            while (next < sorted.Length && sorted[next].Frame < at + count) {
                var e = sorted[next++];
                buffer.Add(new((int)(e.Frame - at), e.Status, e.Data1, e.Data2));
            }

            instrument.Process(buffer.ToArray(), left.AsSpan(at, count), right.AsSpan(at, count));
            at += count;
        }

        return (left, right);
    }

    /// <summary>
    ///     One note held for <paramref name="held" /> frames of a <paramref name="frames" />-frame render, then a reset, so
    ///     the next render starts from silence.
    /// </summary>
    public static (float[] Left, float[] Right) Note(
        IInstrument instrument,
        int note,
        int frames,
        int held,
        int velocity = 100
    ) {
        var audio = Render(instrument, frames, [TimedEvent.On(0, note, velocity), TimedEvent.Off(held, note)]);
        instrument.Reset();
        return audio;
    }

    /// <summary>The engine folder (where Anode.slnx is).</summary>
    public static string EngineRoot() {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "Anode.slnx"))) {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Cannot find the engine folder.");
    }

    /// <summary>A new empty folder under the system temp folder.</summary>
    public static string TempFolder() {
        var path = Path.Combine(Path.GetTempPath(), "anode-dsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
