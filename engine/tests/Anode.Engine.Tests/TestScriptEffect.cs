using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.Transport;
using System.Globalization;

namespace Anode.Engine.Tests;

/// <summary>
///     Once <see cref="Hold" /> is called, holds the script effect's compiles until <see cref="Release" />, and says
///     when one began (<see cref="Entered" />).
/// </summary>
sealed class ScriptGate : IDisposable {
    readonly ManualResetEventSlim open = new(true);

    public ManualResetEventSlim Entered { get; } = new();

    public void Wait() {
        Entered.Set();
        open.Wait(TimeSpan.FromSeconds(10));
    }

    public void Hold() {
        Entered.Reset();
        open.Reset();
    }

    public void Release() => open.Set();

    public void Dispose() {
        open.Dispose();
        Entered.Dispose();
    }
}

/// <summary>
///     A stand-in for workstream D's Scripter (<see cref="IScriptHost" />), with a language of one statement per line:
///     <c>param ID DEFAULT</c> declares a run-time parameter; <c>transpose N</c> moves notes by N, plus the value of
///     parameter <c>shift</c> when there is one; <c>hold</c> adds an octave above each note-on, which it never ends;
///     <c>trace</c> traces each note-on (<c>note N</c>); <c>crash N</c> stops the script when note N comes, with an
///     error on its line; <c>fail</c> fails to compile, on its line. Allocation-free while processing.
/// </summary>
sealed class ScriptEffect(ScriptGate? gate) : IMidiEffect, IScriptHost {
    public static readonly MidiEffectDescriptor Info = new(TestMidiEffects.Script, "Script", "Anode tests", "1.0", []);

    const int Ring = 256;
    readonly float[] values = new float[16];
    readonly int[] traced = new int[Ring];
    InstrumentParameter[] parameters = [];
    int transpose;
    int crashNote = -1;
    int crashLine;
    bool hold;
    bool trace;
    long written;
    long read;
    int ringDropped;
    volatile bool faulted;
    int changed;
    ScriptError? pendingError;

    public MidiEffectDescriptor Descriptor => Info;

    public IReadOnlyList<InstrumentParameter> RuntimeParameters => parameters;

    public string? Source { get; private set; }

    public bool Faulted => faulted;

    public int Idles { get; private set; }

    public int Processed { get; private set; }

    public bool Disposed { get; private set; }

    public ScriptError? Compile(string source) {
        gate?.Wait();
        Source = source;
        var lines = source.Split('\n');
        var list = new List<InstrumentParameter>();
        for (var n = 0; n < lines.Length; n++) {
            switch (lines[n].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                case ["param", var id, var value]:
                    var initial = float.Parse(value, CultureInfo.InvariantCulture);
                    values[list.Count] = initial;
                    list.Add(new(list.Count, id, id, -64, 64, initial, string.Empty));
                    break;
                case ["transpose", var by]:
                    transpose = int.Parse(by, CultureInfo.InvariantCulture);
                    break;
                case ["hold"]:
                    hold = true;
                    break;
                case ["trace"]:
                    trace = true;
                    break;
                case ["crash", var note]:
                    crashNote = int.Parse(note, CultureInfo.InvariantCulture);
                    crashLine = n + 1;
                    break;
                case ["fail"]:
                    faulted = true;
                    return new ScriptError("SyntaxError: unexpected token", n + 1);
            }
        }

        parameters = [..list];
        return null;
    }

    /// <summary>The script redefines its parameters (Logic's <c>UpdatePluginParameters</c>).</summary>
    public void UpdateParameters(params InstrumentParameter[] list) {
        parameters = list;
        Interlocked.Exchange(ref changed, 1);
    }

    public bool TakeParametersChanged() => Interlocked.Exchange(ref changed, 0) != 0;

    public ScriptError? TakeError() {
        var error = pendingError;
        pendingError = null;
        return error;
    }

    public int DrainTrace(List<string> lines) {
        var end = Volatile.Read(ref written);
        for (; Volatile.Read(ref read) < end; Volatile.Write(ref read, read + 1)) {
            lines.Add($"note {traced[read % Ring]}");
        }

        return Interlocked.Exchange(ref ringDropped, 0);
    }

    public void Idle() => Idles++;

    public void Prepare(float sampleRate, int maxFrames) { }

    public void SetParameter(int index, float value) => values[index] = value;

    public float GetParameter(int index) => values[index];

    int Shift() {
        foreach (var p in parameters) {
            if (p.Id == "shift") {
                return transpose + (int)values[p.Index];
            }
        }

        return transpose;
    }

    public void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        Processed++;
        var shift = Shift();
        foreach (var e in input) {
            var on = (e.Status & 0xF0) == 0x90 && e.Data2 > 0;
            if (on && e.Data1 == crashNote) {
                pendingError = new("InternalError: interrupted", crashLine);
                faulted = true;
                output.Add(e with { Data1 = 1 }); // written before it stopped: the engine discards it
                return;
            }

            if (!TestMidiEffects.IsNote(e)) {
                output.Add(e);
                continue;
            }

            var note = Math.Clamp(e.Data1 + shift, 0, 127);
            output.Add(e with { Data1 = (byte)note });
            if (on && hold && note + 12 <= 127) {
                output.Add(e with { Data1 = (byte)(note + 12) });
            }

            if (on && trace) {
                if (written - Volatile.Read(ref read) >= Ring) {
                    Interlocked.Increment(ref ringDropped);
                } else {
                    traced[written % Ring] = note;
                    Volatile.Write(ref written, written + 1);
                }
            }
        }
    }

    public void Reset() { }

    public byte[]? SaveState() => null;

    public bool LoadState(ReadOnlySpan<byte> state) => true;

    public void Dispose() => Disposed = true;
}
