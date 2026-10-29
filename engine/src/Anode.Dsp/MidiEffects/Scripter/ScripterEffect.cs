using Anode.Dsp.Instruments;
using Anode.Dsp.Transport;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Anode.Dsp.MidiEffects.Scripter;

/// <summary>
///     <c>anode.scripter</c>: Logic's Scripter, a MIDI effect programmed in JavaScript (docs/adr/0009-scripter.md,
///     docs/scripter/README.md). Each instance runs its script in its own QuickJS-ng runtime (<c>an_script</c>) whose
///     memory is a fixed arena, under a time budget per block.
/// </summary>
/// <remarks>
///     <para>
///         <b>Threading.</b> As <see cref="IMidiEffect" />: <see cref="Compile" />, <see cref="LoadState" />,
///         <see cref="SaveState" />, <see cref="Prepare" /> and disposal on the control thread while the instance is not
///         processing; <see cref="Process" />, <see cref="Reset" />, <see cref="SetParameter" /> and
///         <see cref="ReleaseNotes" /> on the audio thread, allocating nothing outside the script's arena. The engine
///         takes a new script (<c>setScript</c>) with <see cref="CreateCompiled" /> on the control thread and swaps the
///         new instance in. While the instance processes, the control thread may call <see cref="TakeParametersChanged" />,
///         <see cref="TakeProblem" />, <see cref="TryReadTrace" />, <see cref="TakeTraceDropped" />,
///         <see cref="TryCollect" /> and the read-only properties.
///     </para>
///     <para>
///         <b>Per block</b> the script sees the block's timing (<c>GetTimingInfo</c>, from the
///         <see cref="TransportContext" />), the parameter values the host set, <c>HandleMIDI</c> for each event (in
///         order; without one, events pass through) and then <c>ProcessMIDI</c>; <c>Idle</c> about four times a second.
///         Events it sends for later (<c>sendAfterMilliseconds</c>, <c>sendAtBeat</c>, <c>sendAfterBeats</c>) wait in
///         the instance, by sample or by beat, and play on the first sample at or after their time.
///     </para>
///     <para>
///         <b>Problems.</b> A syntax error, an uncaught exception, a block past its budget (25 % of the block period
///         unless <see cref="BudgetMicroseconds" /> says otherwise) or a full arena stops the script: the instance passes
///         its input through, ends the notes it started, and reports a <see cref="ScriptProblem" /> until a new script
///         compiles.
///     </para>
/// </remarks>
public sealed unsafe class ScripterEffect : IMidiEffect, IScriptHost {
    public const string Id = "anode.scripter";

    /// <summary>The most <c>PluginParameters</c> a script has; later entries are ignored.</summary>
    public const int MaxParameters = 128;

    /// <summary>The most events waiting for a later block; further ones are dropped (<see cref="DroppedEvents" />).</summary>
    public const int PendingCapacity = 2_048;

    /// <summary>Trace lines the ring holds between reads; further ones are counted (<see cref="TakeTraceDropped" />).</summary>
    public const int TraceCapacity = 256;

    /// <summary>The share of the block period a block's calls may take before the script is stopped (ADR-0009).</summary>
    public const double DefaultBudgetFraction = 0.25;

    const int EmitCapacity = 4_096;
    const int ReadChunk = 64;
    const int MaxTargetEvents = 256;
    const int DescriptionBytes = 64 * 1024;
    const int ProblemBytes = 512;
    const long ControlBudgetMicroseconds = 2_000_000;
    const int StateVersion = 1;
    const int BudgetSpent = int.MinValue;
    const int NoProblem = 0;

    static readonly ScriptProblem missingLibrary = new(
        ScriptProblemKind.Unavailable,
        "Scripter is unavailable: the an_script library was not found (build engine/native)",
        0
    );

    // Native state.
    nint script;
    readonly ulong seed;

    // Control-thread state.
    string source = string.Empty;
    IReadOnlyList<ScriptParameter> scriptParameters = [];
    IReadOnlyList<InstrumentParameter> runtimeParameters = [];
    IReadOnlyList<string> targetNames = [];
    MidiEffectDescriptor descriptor = Info;
    bool parametersChanged;
    readonly byte[] controlBuffer = new byte[DescriptionBytes];
    int traceDropped;

    // Parameters: values (written by both threads, a float at a time), what the host may set, and the host's requests.
    readonly float[] values = new float[MaxParameters];
    readonly float[] minimums = new float[MaxParameters];
    readonly float[] maximums = new float[MaxParameters];
    readonly int[] steps = new int[MaxParameters];
    readonly bool[] dirty = new bool[MaxParameters];
    int count;
    bool anyDirty;
    int valuesVersion;

    // The run-time parameter list the audio thread hands the control thread after UpdatePluginParameters.
    readonly byte[] description = new byte[DescriptionBytes];
    int descriptionLength;
    int descriptionReady;
    bool refreshWanted;

    // Problems: written by the thread that found them, then published by the sequence.
    readonly byte[] problemMessage = new byte[ProblemBytes];
    int problemLength;
    int problemLine;
    int problemKind = NoProblem;
    int problemSequence;
    int problemBuilt = -1;
    int problemTaken;
    int runtimeSequence;
    int runtimeTaken;
    long collectedAt;
    int idleCalls;
    ScriptProblem? problem;

    // Processing.
    double sampleRate = 48_000;
    int maxFrames = 1_024;
    long clock;
    long deadline;
    bool running;
    int busy;
    bool resetRequested;
    bool wasPlaying;
    double expectedBeat = 1;
    double idleFrames;
    int droppedEvents;
    readonly double[] args = new double[8];
    readonly double[] records = new double[ReadChunk * ScriptNative.EmitWidth];
    readonly Pending[] pending = new Pending[PendingCapacity];
    int pendingCount;
    readonly ScriptTargetEvent[] targetEvents = new ScriptTargetEvent[MaxTargetEvents];
    int targetCount;
    readonly short[] sounding = new short[16 * 128];
    readonly short[] soundingBefore = new short[16 * 128];
    readonly MidiEvent[] deferred = new MidiEvent[MidiEventBuffer.Capacity];
    int deferredCount;
    readonly MidiEvent[] combined = new MidiEvent[2 * MidiEventBuffer.Capacity];

    // The block being processed.
    int frames;
    bool playing;
    double startBeat;
    double qps;

    /// <summary>
    ///     A new instance with no script (it passes MIDI through), its <c>Math.random</c> seeded by
    ///     <paramref name="seed" /> (a random seed when null), in an arena of <paramref name="arenaBytes" /> (4 MiB when
    ///     0). Control thread. Without the native library the instance passes MIDI through and reports
    ///     <see cref="ScriptProblemKind.Unavailable" />.
    /// </summary>
    public ScripterEffect(ulong? seed = null, nuint arenaBytes = 0) : this(seed, arenaBytes, ScriptNative.Available) { }

    /// <summary>As the public constructor; <paramref name="native" /> false behaves as if the library were missing (tests).</summary>
    internal ScripterEffect(ulong? seed, nuint arenaBytes, bool native) {
        this.seed = seed ?? (ulong)Random.Shared.NextInt64();
        if (!native) {
            Publish(ScriptProblemKind.Unavailable, missingLibrary.Message, 0);
            return;
        }

        var bytes = arenaBytes == 0 ? ScriptNative.DefaultArenaBytes : arenaBytes;
        if (ScriptNative.Create(bytes, this.seed, EmitCapacity, TraceCapacity, out script) != ScriptNative.Ok) {
            script = 0;
            Publish(ScriptProblemKind.OutOfMemory, "Scripter could not create its arena", 0);
            return;
        }

        Compile(string.Empty);
        problemTaken = problemSequence;
    }

    ~ScripterEffect() {
        Free();
    }

    /// <summary>The catalogue entry: no parameters until a script defines them (<see cref="RuntimeParameters" />).</summary>
    public static MidiEffectDescriptor Info { get; } = new(Id, "Scripter", MidiEffect.Vendor, MidiEffect.Version, []);

    /// <summary>Whether the native library loads; without it every instance passes MIDI through.</summary>
    public static bool Available => ScriptNative.Available;

    /// <summary>The embedded engine, e.g. "quickjs-ng 0.17.0+6d46d07" (null without the native library).</summary>
    public static string? EngineVersion => ScriptNative.VersionText;

    /// <summary>The script's source.</summary>
    public string Source => source;

    /// <summary>The seed of the script's <c>Math.random</c>.</summary>
    public ulong Seed => seed;

    /// <summary>Whether the script runs: compiled, with no problem standing.</summary>
    public bool Running => Volatile.Read(ref running);

    /// <summary>
    ///     A block's budget in microseconds of wall time for all its calls into the script; 0 (the default) is
    ///     <see cref="DefaultBudgetFraction" /> of the block's period.
    /// </summary>
    public long BudgetMicroseconds { get; set; }

    /// <summary>The script's parameters, as it defined them (control thread).</summary>
    public IReadOnlyList<ScriptParameter> ScriptParameters => scriptParameters;

    /// <summary>
    ///     The script's parameters as the engine addresses them (control thread): index as in
    ///     <c>PluginParameters</c>, id the parameter's name. <see cref="Descriptor" /> carries the same list.
    /// </summary>
    public IReadOnlyList<InstrumentParameter> RuntimeParameters => runtimeParameters;

    /// <summary>The <c>TargetEvent</c> targets the script has named, in first-use order (control thread).</summary>
    public IReadOnlyList<string> TargetNames => targetNames;

    /// <summary>
    ///     The <c>TargetEvent</c>s the script sent in the last block (audio thread, after <see cref="Process" />): the
    ///     engine routes them to the targets it maps by name.
    /// </summary>
    public ReadOnlySpan<ScriptTargetEvent> TargetEvents => targetEvents.AsSpan(0, targetCount);

    /// <summary>Bumped whenever the script itself sets a parameter (<c>SetParameter</c>): the engine re-reads values.</summary>
    public int ParameterValuesVersion => Volatile.Read(ref valuesVersion);

    /// <summary>Events dropped since the instance was created: a full output, pending list or emit buffer.</summary>
    public int DroppedEvents => Volatile.Read(ref droppedEvents);

    /// <summary>Events waiting for a later block.</summary>
    public int PendingEvents => pendingCount;

    /// <summary>Bytes of the script's arena in use, and its size.</summary>
    public long MemoryUsed => script == 0 ? 0 : (long)ScriptNative.MemoryUsed(script);

    public long ArenaBytes => script == 0 ? 0 : (long)ScriptNative.ArenaBytes(script);

    /// <summary>The problem standing (control thread), or null while the script runs.</summary>
    public ScriptProblem? Problem {
        get {
            var sequence = Volatile.Read(ref problemSequence);
            if (sequence != problemBuilt) {
                problem = problemKind == NoProblem
                    ? null
                    : new(
                        (ScriptProblemKind)(problemKind - 1),
                        Encoding.UTF8.GetString(problemMessage, 0, problemLength),
                        problemLine
                    );
                problemBuilt = sequence;
            }

            return problem;
        }
    }

    public MidiEffectDescriptor Descriptor => descriptor;

    public void Prepare(float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        this.sampleRate = sampleRate;
        this.maxFrames = maxFrames;
        clock = 0;
        ForgetEvents();
    }

    public void SetParameter(int index, float value) {
        if ((uint)index >= (uint)Math.Min(Volatile.Read(ref count), MaxParameters)) {
            return;
        }

        values[index] = Fit(index, value);
        dirty[index] = true;
        anyDirty = true;
    }

    public float GetParameter(int index) => (uint)index < MaxParameters ? values[index] : 0;

    public void Process(in TransportContext transport, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output) {
        targetCount = 0;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) {
            // TryCollect holds the script: take these events in the next block.
            Defer(input);
            clock += transport.Frames;
            return;
        }

        try {
            var events = WithDeferred(input);
            if (resetRequested) {
                ResetNow();
            }

            if (!Volatile.Read(ref running)) {
                Through(events, output);
                return;
            }

            Array.Copy(sounding, soundingBefore, sounding.Length);
            var status = RunBlock(transport, events, output);
            if (status != ScriptNative.Ok) {
                FailRunning(status);
                output.Clear();
                targetCount = 0;
                Array.Copy(soundingBefore, sounding, sounding.Length);
                ReleaseNotes(output);
                Through(events, output);
            }
        } finally {
            clock += transport.Frames;
            Volatile.Write(ref busy, 0);
        }
    }

    public void Reset() {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) {
            resetRequested = true;
            return;
        }

        try {
            ResetNow();
        } finally {
            Volatile.Write(ref busy, 0);
        }
    }

    /// <summary>
    ///     Adds a note-off at frame 0 to <paramref name="output" /> for every note this instance started and has not
    ///     ended, and forgets the events it holds (audio thread). The engine calls it on the old instance when it swaps
    ///     a new script in, so no note hangs.
    /// </summary>
    public void ReleaseNotes(MidiEventBuffer output) {
        for (var key = 0; key < sounding.Length; key++) {
            for (var n = sounding[key]; n > 0; n--) {
                if (!output.Add(new(0, (byte)(0x80 | (key >> 7)), (byte)(key & 0x7F), 0))) {
                    Interlocked.Increment(ref droppedEvents);
                }
            }

            sounding[key] = 0;
        }

        pendingCount = 0;
    }

    /// <summary>
    ///     Compiles <paramref name="newSource" /> into this instance, replacing its script (control thread, while the
    ///     instance is not processing): the prelude and the script run, <c>PluginParameters</c> become
    ///     <see cref="RuntimeParameters" />, values in <paramref name="parameterValues" /> (by name; else the current
    ///     ones, unless the script sets <c>ResetParameterDefaults = true</c>) are restored, and <c>ParameterChanged</c>
    ///     runs for each parameter. Returns the problem, or null when the script runs.
    /// </summary>
    public ScriptProblem? Compile(string newSource, IReadOnlyDictionary<string, float>? parameterValues = null) {
        ArgumentNullException.ThrowIfNull(newSource);
        var carried = parameterValues ?? ParameterValues();
        source = newSource;
        Volatile.Write(ref running, false);
        ForgetEvents();
        if (script == 0) {
            return Problem;
        }

        var status = ScriptNative.Reset(script);
        if (status == ScriptNative.Ok) {
            ScriptNative.SetBudget(script, ControlBudgetMicroseconds);
            status = CompileText(ScriptPrelude.Source, ScriptPrelude.Name);
            if (status != ScriptNative.Ok) {
                throw new InvalidOperationException($"The Scripter prelude failed: {LastError()}");
            }

            status = CompileText(newSource, "Script");
        }

        if (status == ScriptNative.Ok) {
            status = ControlCall("__init"u8, [], out _);
        }

        if (status == ScriptNative.Ok) {
            status = Describe(controlBuffer, out var length);
            if (status == ScriptNative.Ok) {
                Apply(controlBuffer.AsSpan(0, length), true);
                status = Restore(carried);
            }
        }

        if (status == ScriptNative.Ok) {
            status = ControlCall("__changedAll"u8, [], out _);
        }

        if (status == ScriptNative.Ok) {
            status = Sync();
        }

        if (status != ScriptNative.Ok) {
            Fail(status);
            return Problem;
        }

        Publish(null, string.Empty, 0);
        Volatile.Write(ref running, true);
        return null;
    }

    /// <summary>
    ///     A new prepared instance running <paramref name="newSource" />, with <paramref name="previous" />'s parameter
    ///     values carried over by name (control thread): how the engine takes <c>setScript</c> without stopping the
    ///     chain. Check <see cref="Problem" />; then swap it in on the audio thread, call
    ///     <see cref="ReleaseNotes" /> on the old instance there, and dispose the old one on the control thread.
    /// </summary>
    public static ScripterEffect CreateCompiled(
        string newSource,
        float sampleRate,
        int maxFrames,
        ScripterEffect? previous = null,
        ulong? seed = null
    ) {
        var effect = new ScripterEffect(seed);
        effect.Prepare(sampleRate, maxFrames);
        effect.BudgetMicroseconds = previous?.BudgetMicroseconds ?? 0;
        effect.Compile(newSource, previous?.ParameterValues());
        return effect;
    }

    /// <summary>The parameters' values by name (control thread).</summary>
    public Dictionary<string, float> ParameterValues() {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var p in scriptParameters) {
            if (p.Kind != ScriptParameterKind.Text) {
                result[p.Name] = values[p.Index];
            }
        }

        return result;
    }

    /// <summary>
    ///     True once after the run-time parameter list changed (control thread): after a compile, a
    ///     <see cref="LoadState" />, or the script's <c>UpdatePluginParameters</c>/a new <c>TargetEvent</c> target,
    ///     which this reads. The engine then pushes <see cref="RuntimeParameters" /> (<c>slotParameters</c>).
    /// </summary>
    public bool TakeParametersChanged() {
        if (Volatile.Read(ref descriptionReady) == 1) {
            try {
                Apply(description.AsSpan(0, descriptionLength), false);
            } catch (JsonException) {
                // A list too long for the hand-off buffer: keep the previous one.
            }

            Volatile.Write(ref descriptionReady, 0);
        }

        var changed = parametersChanged;
        parametersChanged = false;
        return changed;
    }

    /// <summary>
    ///     True once after the problem changed (control thread), with the new one: a problem to report
    ///     (<c>pluginProblem</c>), or null when a new script cleared it.
    /// </summary>
    public bool TakeProblem(out ScriptProblem? current) {
        var sequence = Volatile.Read(ref problemSequence);
        current = Problem;
        if (sequence == problemTaken) {
            return false;
        }

        problemTaken = sequence;
        return true;
    }

    /// <summary>The oldest <c>Trace</c> line not yet read (control thread; one reader).</summary>
    public bool TryReadTrace([NotNullWhen(true)] out string? line) {
        line = null;
        if (script == 0) {
            return false;
        }

        int length;
        int dropped;
        fixed (byte* buffer = controlBuffer) {
            length = ScriptNative.ReadTrace(script, buffer, ScriptNative.TraceLineMax, &dropped);
        }

        traceDropped += dropped;
        if (length <= 0) {
            return false;
        }

        line = Encoding.UTF8.GetString(controlBuffer, 0, length);
        return true;
    }

    /// <summary>Trace lines dropped because the ring was full, since the previous call (control thread).</summary>
    public int TakeTraceDropped() {
        if (script != 0) {
            int dropped;
            byte unused;
            if (ScriptNative.ReadTrace(script, &unused, 0, &dropped) == 0) { // capacity 0: only the dropped count
                traceDropped += dropped;
            }
        }

        var result = traceDropped;
        traceDropped = 0;
        return result;
    }

    /// <summary>
    ///     Runs the script's garbage collector between blocks (control thread): false when a block was processing,
    ///     to be tried again. A block that starts during the collection takes its events one block late. Call it now
    ///     and then (once a second); reference counting frees most garbage at once, the collector only cycles.
    /// </summary>
    public bool TryCollect() {
        if (script == 0) {
            return true;
        }

        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) {
            return false;
        }

        try {
            ScriptNative.Collect(script);
        } finally {
            Volatile.Write(ref busy, 0);
        }

        return true;
    }

    /// <summary>
    ///     <see cref="IScriptHost" />: true while a problem stands (a failed compile, an error, an overrun or a full
    ///     arena; the missing library too). Read on the audio thread; an instance is compiled once, so it stays true.
    /// </summary>
    public bool Faulted => Volatile.Read(ref problemKind) != NoProblem;

    /// <summary>
    ///     <see cref="IScriptHost.Compile" />: <see cref="Compile(string, IReadOnlyDictionary{string, float})" />,
    ///     with the values the engine sets afterwards.
    /// </summary>
    ScriptError? IScriptHost.Compile(string source) =>
        Compile(source) is { } p ? new ScriptError(p.Message, p.Line) : null;

    /// <summary>
    ///     <see cref="IScriptHost" />: the problem that stopped the running script, once (control thread); not a
    ///     compile's, which <see cref="Compile(string, IReadOnlyDictionary{string, float})" /> returns.
    /// </summary>
    public ScriptError? TakeError() {
        var sequence = Volatile.Read(ref runtimeSequence);
        if (sequence == runtimeTaken || Problem is not { } p) {
            return null;
        }

        runtimeTaken = sequence;
        return new(p.Message, p.Line);
    }

    /// <summary>
    ///     <see cref="IScriptHost" />: appends the trace lines waiting (control thread) and returns how many the ring
    ///     dropped since the previous call.
    /// </summary>
    public int DrainTrace(List<string> lines) {
        ArgumentNullException.ThrowIfNull(lines);
        while (TryReadTrace(out var line)) {
            lines.Add(line);
        }

        return TakeTraceDropped();
    }

    /// <summary>
    ///     <see cref="IScriptHost" /> (control thread, about every 50 ms, possibly during <see cref="Process" />): the
    ///     garbage collector's turn, when the arena grew by 256 KiB since the last collection or every second or so.
    ///     It never waits for the audio thread: when a block is processing it tries again on a later call. The
    ///     script's own <c>Idle</c> runs on the audio thread (about every 250 ms, under the block's budget), so it never
    ///     contends with a block.
    /// </summary>
    public void Idle() {
        if (script == 0 || !Volatile.Read(ref running)) {
            return;
        }

        idleCalls++;
        if (MemoryUsed - collectedAt < 256 * 1024 && idleCalls < 20) {
            return;
        }

        if (TryCollect()) {
            idleCalls = 0;
            collectedAt = MemoryUsed;
        }
    }

    /// <summary>Whether the arena's bookkeeping is consistent (tests).</summary>
    internal bool CheckArena() => script != 0 && ScriptNative.Check(script) == ScriptNative.Ok;

    /// <summary>The state: the source and the parameter values by name.</summary>
    public byte[]? SaveState() {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            writer.Write("ANSC"u8);
            writer.Write(StateVersion);
            writer.Write(source);
            var saved = ParameterValues();
            writer.Write(saved.Count);
            foreach (var p in scriptParameters) {
                if (saved.TryGetValue(p.Name, out var value)) {
                    writer.Write(p.Name);
                    writer.Write(value);
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>Loads a state (control thread) and compiles its script; false when it is not a Scripter state.</summary>
    public bool LoadState(ReadOnlySpan<byte> state) {
        if (state.Length < 8 || !state[..4].SequenceEqual("ANSC"u8)) {
            return false;
        }

        string text;
        Dictionary<string, float> saved;
        try {
            using var stream = new MemoryStream([..state]);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            reader.ReadBytes(4);
            if (reader.ReadInt32() != StateVersion) {
                return false;
            }

            text = reader.ReadString();
            var n = reader.ReadInt32();
            if (n is < 0 or > MaxParameters) {
                return false;
            }

            saved = new(StringComparer.Ordinal);
            for (var i = 0; i < n; i++) {
                var name = reader.ReadString();
                saved[name] = reader.ReadSingle();
            }
        } catch (EndOfStreamException) {
            return false;
        } catch (IOException) {
            return false;
        }

        Compile(text, saved);
        return true;
    }

    public void Dispose() {
        Free();
        GC.SuppressFinalize(this);
    }

    void Free() {
        if (script != 0) {
            ScriptNative.Destroy(script);
            script = 0;
        }
    }

    // Audio thread.

    int RunBlock(in TransportContext transport, ReadOnlySpan<MidiEvent> events, MidiEventBuffer output) {
        frames = transport.Frames;
        playing = transport.Playing;
        qps = Math.Max(transport.QuartersPerSample, 1e-12);
        startBeat = transport.Quarter + 1;
        var endBeat = startBeat + frames * qps;
        if (playing != wasPlaying || (playing && Math.Abs(startBeat - expectedBeat) > 1e-6)) {
            // A start, stop or jump: what waits for a beat keeps its distance from here, in time.
            for (var i = 0; i < pendingCount; i++) {
                ref var p = ref pending[i];
                if (p.ByBeat) {
                    p.At = clock + Math.Max(0, p.At - expectedBeat) / qps;
                    p.ByBeat = false;
                }
            }
        }

        wasPlaying = playing;
        expectedBeat = playing ? endBeat : startBeat;
        var budget = BudgetMicroseconds > 0
            ? BudgetMicroseconds
            : (long)Math.Max(1, DefaultBudgetFraction * frames / sampleRate * 1e6);
        deadline = Stopwatch.GetTimestamp() + budget * Stopwatch.Frequency / 1_000_000;

        DeliverDue(output);

        args[0] = playing ? 1 : 0;
        args[1] = startBeat;
        args[2] = endBeat;
        args[3] = transport.Bpm;
        args[4] = transport.Numerator;
        args[5] = transport.Denominator;
        args[6] = frames;
        args[7] = sampleRate;
        var status = Call("__block"u8, 8, out _);
        if (status != ScriptNative.Ok) {
            return status;
        }

        if (anyDirty) {
            anyDirty = false;
            for (var i = 0; i < MaxParameters; i++) {
                if (!dirty[i]) {
                    continue;
                }

                dirty[i] = false;
                args[0] = i;
                args[1] = values[i];
                status = Call("__param"u8, 2, out var fitted);
                if (status != ScriptNative.Ok) {
                    return status;
                }

                if (!double.IsNaN(fitted)) {
                    values[i] = (float)fitted;
                }
            }
        }

        foreach (var e in events) {
            args[0] = e.Status;
            args[1] = e.Data1;
            args[2] = e.Data2;
            args[3] = e.Frame;
            args[4] = startBeat + e.Frame * qps;
            status = Call("__midi"u8, 5, out _);
            if (status != ScriptNative.Ok) {
                return status;
            }
        }

        idleFrames += frames;
        var idle = idleFrames >= sampleRate / 4;
        if (idle) {
            idleFrames = 0;
        }

        args[0] = idle ? 1 : 0;
        status = Call("__process"u8, 1, out _);
        if (status != ScriptNative.Ok) {
            return status;
        }

        Drain(output);
        if (refreshWanted) {
            status = Refresh(output);
        }

        return status;
    }

    /// <summary>
    ///     After <c>UpdatePluginParameters</c>: the values again (by index in the new list) and, when the control thread
    ///     has taken the previous one, the new list for it.
    /// </summary>
    int Refresh(MidiEventBuffer output) {
        var status = Call("__sync"u8, 0, out var n);
        if (status != ScriptNative.Ok) {
            return status;
        }

        Volatile.Write(ref count, (int)Math.Clamp(n, 0, MaxParameters));
        Drain(output);
        if (Volatile.Read(ref descriptionReady) != 0) {
            return ScriptNative.Ok; // try again next block
        }

        status = Describe(description, out descriptionLength);
        if (status == ScriptNative.Ok) {
            refreshWanted = false;
            Volatile.Write(ref descriptionReady, 1);
        }

        return status;
    }

    int Describe(byte[] buffer, out int length) {
        length = 0;
        if (Stopwatch.GetTimestamp() >= deadline && Volatile.Read(ref running)) {
            return BudgetSpent;
        }

        var onAudioThread = Volatile.Read(ref running);
        if (onAudioThread) {
            // The block's budget, not the control thread's.
            ScriptNative.SetBudget(
                script,
                Math.Max(1, (deadline - Stopwatch.GetTimestamp()) * 1_000_000 / Stopwatch.Frequency)
            );
        }

        int written;
        fixed (byte* b = &buffer[0]) {
            fixed (byte* expression = "__describe()"u8) {
                written = ScriptNative.EvalJson(script, expression, b, buffer.Length);
            }
        }

        if (onAudioThread) {
            ScriptNative.SetBudget(script, ControlBudgetMicroseconds);
        }

        if (written < 0) {
            return written;
        }

        length = Math.Min(written, buffer.Length - 1);
        return ScriptNative.Ok;
    }

    int Call(ReadOnlySpan<byte> name, int argc, out double result) {
        result = double.NaN;
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) {
            return BudgetSpent;
        }

        var budget = Math.Max(1, remaining * 1_000_000 / Stopwatch.Frequency);
        double r;
        int status;
        fixed (byte* n = &MemoryMarshal.GetReference(name)) {
            fixed (double* a = args) {
                status = ScriptNative.Call(script, n, a, argc, budget, &r);
            }
        }

        result = r;
        return status == ScriptNative.NoFunction ? ScriptNative.Ok : status;
    }

    /// <summary>Routes what the script emitted.</summary>
    void Drain(MidiEventBuffer output) {
        while (true) {
            int n;
            int dropped;
            fixed (double* r = records) {
                n = ScriptNative.ReadEmitted(script, r, ReadChunk, &dropped);
            }

            if (dropped > 0) {
                Interlocked.Add(ref droppedEvents, dropped);
            }

            if (n <= 0) {
                return;
            }

            for (var i = 0; i < n; i++) {
                Route(records.AsSpan(i * ScriptNative.EmitWidth, ScriptNative.EmitWidth), output);
            }
        }
    }

    void Route(ReadOnlySpan<double> r, MidiEventBuffer output) {
        var kind = r[0];
        if (kind >= 0) {
            var status = (byte)Math.Clamp((int)kind, 0x80, 0xFF);
            Schedule(new() { Status = status, Data1 = (byte)r[1], Data2 = (byte)r[2] }, (int)r[3], r[4], r[5], output);
            return;
        }

        switch ((int)kind) {
            case -1:
                Schedule(new() { Target = (int)r[1], Value = (float)r[2] }, (int)r[3], r[4], r[5], output);
                break;
            case -2:
                var index = (int)r[1];
                if ((uint)index < MaxParameters) {
                    values[index] = (float)r[2];
                    Interlocked.Increment(ref valuesVersion);
                }

                break;
            case -3:
                refreshWanted = true;
                break;
            case -4:
                ReleaseSounding(output);
                break;
        }
    }

    void ReleaseSounding(MidiEventBuffer output) {
        for (var key = 0; key < sounding.Length; key++) {
            while (sounding[key] > 0) {
                Deliver(new() { Status = (byte)(0x80 | (key >> 7)), Data1 = (byte)(key & 0x7F) }, 0, output);
            }
        }
    }

    /// <summary>Plays an event now, at a time in this block, or holds it for a later one.</summary>
    void Schedule(Pending p, int mode, double when, double offset, MidiEventBuffer output) {
        var from = Math.Clamp(double.IsNaN(offset) ? 0 : offset, 0, frames - 1);
        switch (mode) {
            case 1 when playing:
                var beatFrame = FrameOf((when - startBeat) / qps);
                if (beatFrame < frames) {
                    Deliver(p, (int)Math.Max(0, beatFrame), output);
                } else {
                    p.ByBeat = true;
                    p.At = when;
                    Hold(p);
                }

                return;
            case 1:
                // Stopped: the beat at the transport's tempo from where it stands.
                At(p, clock + Math.Max(0, (when - startBeat) / qps), output);
                return;
            case 2:
                At(p, clock + from + Math.Max(0, when) * sampleRate / 1_000, output);
                return;
            default:
                Deliver(p, (int)from, output);
                return;
        }
    }

    void At(Pending p, double sample, MidiEventBuffer output) {
        var frame = FrameOf(sample - clock);
        if (frame < frames) {
            Deliver(p, (int)Math.Max(0, frame), output);
        } else {
            p.ByBeat = false;
            p.At = sample;
            Hold(p);
        }
    }

    /// <summary>The first frame at or after <paramref name="offset" /> (a sample offset into the block).</summary>
    static double FrameOf(double offset) => double.IsNaN(offset) ? 0 : Math.Ceiling(offset - 1e-6);

    void Hold(Pending p) {
        if (pendingCount == PendingCapacity) {
            Interlocked.Increment(ref droppedEvents);
            return;
        }

        pending[pendingCount++] = p;
    }

    /// <summary>Plays the held events due in this block, in the order they were sent.</summary>
    void DeliverDue(MidiEventBuffer output) {
        var kept = 0;
        for (var i = 0; i < pendingCount; i++) {
            var p = pending[i];
            double frame;
            if (p.ByBeat) {
                frame = playing ? FrameOf((p.At - startBeat) / qps) : double.MaxValue;
            } else {
                frame = FrameOf(p.At - clock);
            }

            if (frame < frames) {
                Deliver(p, (int)Math.Max(0, frame), output);
            } else {
                pending[kept++] = p;
            }
        }

        pendingCount = kept;
    }

    void Deliver(in Pending p, int frame, MidiEventBuffer output) {
        if (p.Status == 0) {
            if (targetCount < MaxTargetEvents) {
                targetEvents[targetCount++] = new(frame, p.Target, Math.Clamp(p.Value, 0, 1));
            } else {
                Interlocked.Increment(ref droppedEvents);
            }

            return;
        }

        var e = new MidiEvent(frame, p.Status, (byte)(p.Data1 & 0x7F), (byte)(p.Data2 & 0x7F));
        if (!output.Add(e)) {
            Interlocked.Increment(ref droppedEvents);
            return;
        }

        var type = p.Status & 0xF0;
        if (type is not (0x80 or 0x90)) {
            return;
        }

        var key = ((p.Status & 0x0F) << 7) | e.Data1;
        if (type == 0x90 && e.Data2 > 0) {
            if (sounding[key] < short.MaxValue) {
                sounding[key]++;
            }
        } else if (sounding[key] > 0) {
            sounding[key]--;
        }
    }

    static void Through(ReadOnlySpan<MidiEvent> events, MidiEventBuffer output) {
        foreach (var e in events) {
            if (!output.Add(e)) {
                return;
            }
        }
    }

    void Defer(ReadOnlySpan<MidiEvent> input) {
        foreach (var e in input) {
            if (deferredCount == deferred.Length) {
                Interlocked.Increment(ref droppedEvents);
                continue;
            }

            deferred[deferredCount++] = e with { Frame = 0 };
        }
    }

    ReadOnlySpan<MidiEvent> WithDeferred(ReadOnlySpan<MidiEvent> input) {
        if (deferredCount == 0) {
            return input;
        }

        deferred.AsSpan(0, deferredCount).CopyTo(combined);
        var n = Math.Min(input.Length, combined.Length - deferredCount);
        input[..n].CopyTo(combined.AsSpan(deferredCount));
        var total = deferredCount + n;
        deferredCount = 0;
        return combined.AsSpan(0, total);
    }

    void ResetNow() {
        resetRequested = false;
        ForgetEvents();
        if (!Volatile.Read(ref running)) {
            return;
        }

        var budget = BudgetMicroseconds > 0
            ? BudgetMicroseconds
            : (long)Math.Max(1, DefaultBudgetFraction * maxFrames / sampleRate * 1e6);
        deadline = Stopwatch.GetTimestamp() + budget * Stopwatch.Frequency / 1_000_000;
        var status = Call("__reset"u8, 0, out _);
        if (status != ScriptNative.Ok) {
            FailRunning(status);
        }

        // What Reset sent: it starts the next block (nothing sounds after a reset; events wait in the emit buffer).
    }

    void ForgetEvents() {
        pendingCount = 0;
        deferredCount = 0;
        targetCount = 0;
        wasPlaying = false;
        expectedBeat = 1;
        Array.Clear(sounding);
    }

    /// <summary>Stops the running script (audio thread): a problem for <see cref="TakeError" />.</summary>
    void FailRunning(int status) {
        Fail(status);
        Volatile.Write(ref runtimeSequence, Volatile.Read(ref problemSequence));
    }

    /// <summary>Stops the script with the problem behind <paramref name="status" />.</summary>
    void Fail(int status) {
        Volatile.Write(ref running, false);
        if (status == BudgetSpent) {
            Publish(ScriptProblemKind.Interrupted, "interrupted: the script ran past its time budget", 0);
            return;
        }

        var kind = status switch {
            ScriptNative.Interrupted => ScriptProblemKind.Interrupted,
            ScriptNative.OutOfMemory => ScriptProblemKind.OutOfMemory,
            _ => ScriptProblemKind.Error
        };
        int length;
        int line;
        fixed (byte* b = problemMessage) {
            length = ScriptNative.LastError(script, b, ProblemBytes, &line);
        }

        if (length <= 0) {
            Publish(kind, "the script failed", line);
            return;
        }

        problemLength = Math.Min(length, ProblemBytes - 1);
        problemLine = line;
        problemKind = (int)kind + 1;
        Interlocked.Increment(ref problemSequence);
    }

    /// <summary>Publishes a problem (or none, when <paramref name="kind" /> is null). Allocation-free.</summary>
    void Publish(ScriptProblemKind? kind, string message, int line) {
        var n = 0;
        foreach (var c in message) {
            if (n == ProblemBytes - 1) {
                break;
            }

            problemMessage[n++] = c < 0x80 ? (byte)c : (byte)'?';
        }

        problemLength = n;
        problemLine = line;
        problemKind = kind is null ? NoProblem : (int)kind + 1;
        Interlocked.Increment(ref problemSequence);
    }

    float Fit(int index, float value) {
        if (float.IsNaN(value)) {
            return values[index];
        }

        var min = minimums[index];
        var max = maximums[index];
        value = Math.Clamp(value, min, Math.Max(min, max));
        var n = steps[index];
        if (n > 0 && max > min) {
            var step = (max - min) / n;
            value = min + MathF.Round((value - min) / step) * step;
        }

        return value;
    }

    // Control thread.

    int CompileText(string text, string name) {
        var bytes = Encoding.UTF8.GetBytes(text);
        var label = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* s = bytes) {
            fixed (byte* l = label) {
                byte empty = 0;
                return ScriptNative.Compile(script, bytes.Length == 0 ? &empty : s, bytes.Length, l);
            }
        }
    }

    int ControlCall(ReadOnlySpan<byte> name, ReadOnlySpan<double> arguments, out double result) {
        arguments.CopyTo(args);
        deadline = Stopwatch.GetTimestamp() + ControlBudgetMicroseconds * Stopwatch.Frequency / 1_000_000;
        return Call(name, arguments.Length, out result);
    }

    int Restore(IReadOnlyDictionary<string, float> carried) {
        var reset = false;
        fixed (byte* name = "ResetParameterDefaults"u8) {
            double flag;
            if (ScriptNative.GetGlobal(script, name, &flag) == ScriptNative.Ok) {
                reset = flag == 1;
            }
        }

        if (reset) {
            return ScriptNative.Ok;
        }

        foreach (var p in scriptParameters) {
            if (p.Kind == ScriptParameterKind.Text || !carried.TryGetValue(p.Name, out var value)) {
                continue;
            }

            var status = ControlCall("__restore"u8, [p.Index, value], out var fitted);
            if (status != ScriptNative.Ok) {
                return status;
            }

            if (!double.IsNaN(fitted)) {
                values[p.Index] = (float)fitted;
            }
        }

        return ScriptNative.Ok;
    }

    /// <summary>Takes what the script emitted while compiling: parameter values; events are dropped.</summary>
    int Sync() {
        var status = ControlCall("__sync"u8, [], out _);
        while (status == ScriptNative.Ok) {
            int n;
            fixed (double* r = records) {
                n = ScriptNative.ReadEmitted(script, r, ReadChunk, null);
            }

            if (n <= 0) {
                break;
            }

            for (var i = 0; i < n; i++) {
                var record = records.AsSpan(i * ScriptNative.EmitWidth, ScriptNative.EmitWidth);
                if ((int)record[0] == -2 && (uint)record[1] < MaxParameters) {
                    values[(int)record[1]] = (float)record[2];
                }
            }
        }

        return status;
    }

    /// <summary>Takes the parameter list from <c>__describe()</c>'s JSON (and the values with it, after a compile).</summary>
    void Apply(ReadOnlySpan<byte> json, bool withValues) {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var list = new List<ScriptParameter>();
        foreach (var p in root.GetProperty("parameters").EnumerateArray()) {
            var index = list.Count;
            if (index == MaxParameters) {
                break;
            }

            var kind = p.GetProperty("type").GetString() switch {
                "log" => ScriptParameterKind.Logarithmic,
                "menu" => ScriptParameterKind.Menu,
                "checkbox" => ScriptParameterKind.Checkbox,
                "momentary" => ScriptParameterKind.Momentary,
                "text" => ScriptParameterKind.Text,
                _ => ScriptParameterKind.Linear
            };
            IReadOnlyList<string>? names = null;
            if (p.GetProperty("valueStrings") is { ValueKind: JsonValueKind.Array } strings) {
                names = [..strings.EnumerateArray().Select(static s => s.GetString() ?? string.Empty)];
            }

            var parameter = new ScriptParameter(
                index,
                p.GetProperty("name").GetString() ?? string.Empty,
                kind,
                (float)p.GetProperty("min").GetDouble(),
                (float)p.GetProperty("max").GetDouble(),
                (float)p.GetProperty("default").GetDouble(),
                p.GetProperty("steps").GetInt32(),
                names,
                p.GetProperty("unit").GetString() ?? string.Empty,
                p.GetProperty("automatable").GetBoolean()
            );
            list.Add(parameter);
            minimums[index] = parameter.Min;
            maximums[index] = parameter.Max;
            steps[index] = parameter.Steps;
            if (withValues) {
                values[index] = (float)p.GetProperty("value").GetDouble();
            }
        }

        targetNames =
            [..root.GetProperty("targets").EnumerateArray().Select(static t => t.GetString() ?? string.Empty)];
        scriptParameters = list;
        runtimeParameters = [
            ..list.Select(static p => new InstrumentParameter(
                    p.Index,
                    p.Name,
                    p.Name,
                    p.Min,
                    p.Max,
                    p.Default,
                    p.Unit,
                    p.Steps,
                    p.ValueNames,
                    Kind: p.Kind switch {
                        ScriptParameterKind.Menu => InstrumentParameterKind.Menu,
                        ScriptParameterKind.Checkbox => InstrumentParameterKind.Checkbox,
                        ScriptParameterKind.Momentary => InstrumentParameterKind.Momentary,
                        ScriptParameterKind.Text => InstrumentParameterKind.Text,
                        _ => InstrumentParameterKind.Slider
                    }
                )
            )
        ];
        descriptor = list.Count == 0 ? Info : Info with { Parameters = runtimeParameters };
        Volatile.Write(ref count, list.Count);
        parametersChanged = true;
    }

    string LastError() {
        var buffer = new byte[ProblemBytes];
        int line;
        int length;
        fixed (byte* b = buffer) {
            length = ScriptNative.LastError(script, b, buffer.Length, &line);
        }

        return $"{Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length))} (line {line})";
    }

    /// <summary>An event waiting for a later block: MIDI (<see cref="Status" /> ≥ 0x80) or a target event (0).</summary>
    struct Pending {
        public double At; // a sample of the instance's clock, or a beat when ByBeat
        public bool ByBeat;
        public byte Status;
        public byte Data1;
        public byte Data2;
        public int Target;
        public float Value;
    }
}
