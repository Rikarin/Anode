using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Mixing;

/// <summary>What an automation target drives (<see cref="EngineAutomationTarget.Kind" />).</summary>
public enum AutomationKind : byte {
    Unknown,
    Volume,
    Pan,
    Mute,
    Send,
    Insert,
    Instrument
}

/// <summary>
///     One target's automation, from the model's <see cref="PlaybackEventKind.Automation" /> events: breakpoints sorted
///     by position. Between two breakpoints the value ramps linearly, unless the later one is a step (its
///     <see cref="PlaybackEvent.Data1" /> is 1), which holds the earlier value up to it; before the first it is the first
///     value, after the last the last. Immutable; built on the control thread.
/// </summary>
public sealed class AutomationLane {
    readonly long[] positions;
    readonly float[] values;
    readonly bool[] steps;

    public AutomationLane(long[] positions, float[] values, bool[] steps) {
        if (positions.Length == 0 || positions.Length != values.Length || positions.Length != steps.Length) {
            throw new ArgumentException("A lane needs at least one breakpoint, with a value and step flag each.");
        }

        this.positions = positions;
        this.values = values;
        this.steps = steps;
    }

    public int Count => positions.Length;

    /// <summary>The index of the last breakpoint at or before <paramref name="position" /> (−1: before the first).</summary>
    public int IndexAt(long position) {
        int lo = 0, hi = positions.Length;
        while (lo < hi) {
            var mid = (lo + hi) >>> 1;
            if (positions[mid] <= position) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }

        return lo - 1;
    }

    /// <summary>The value at <paramref name="position" />.</summary>
    public float ValueAt(long position) => ValueAt(position, IndexAt(position));

    /// <summary>The value at <paramref name="position" />, given its <see cref="IndexAt" />.</summary>
    public float ValueAt(long position, int index) {
        if (index < 0) {
            return values[0];
        }

        var next = index + 1;
        if (next >= positions.Length || steps[next]) {
            return values[index];
        }

        return Interpolate(positions[index], values[index], positions[next], values[next], position);
    }

    /// <summary>
    ///     Linear interpolation between (<paramref name="p0" />, <paramref name="v0" />) and (<paramref name="p1" />,
    ///     <paramref name="v1" />) at <paramref name="position" />, in double, rounded once to float.
    /// </summary>
    public static float Interpolate(long p0, float v0, long p1, float v1, long position) =>
        p1 <= p0 ? v1 : (float)(v0 + (v1 - (double)v0) * ((double)(position - p0) / (p1 - p0)));

    /// <summary>
    ///     Audio thread: the value at each of <paramref name="blockPositions" /> (the block's play-order positions) into
    ///     <paramref name="destination" />, walking a cursor (and searching again after a jump back).
    /// </summary>
    public void Fill(ReadOnlySpan<long> blockPositions, Span<float> destination) {
        var index = IndexAt(blockPositions[0]);
        var count = positions.Length;
        for (var i = 0; i < destination.Length; i++) {
            var p = blockPositions[i];
            if (index >= 0 && p < positions[index]) {
                index = IndexAt(p); // a cycle jump back
            }

            while (index + 1 < count && positions[index + 1] <= p) {
                index++;
            }

            destination[i] = ValueAt(p, index);
        }
    }

    /// <summary>The model's lanes: one per target index that has events (null where none).</summary>
    public static unsafe AutomationLane?[] Collect(IEnumerable<TrackView> views, int targets) {
        var lists = new List<(long Position, float Value, bool Step)>?[targets];
        foreach (var view in views) {
            for (long i = 0; i < view.EventCount; i++) {
                ref readonly var e = ref view.Events[i];
                if (e.Kind != PlaybackEventKind.Automation || (uint)e.Target >= (uint)targets) {
                    continue;
                }

                (lists[e.Target] ??= []).Add((e.SamplePos, float.IsFinite(e.Value) ? e.Value : 0f, e.Data1 == 1));
            }
        }

        var lanes = new AutomationLane?[targets];
        for (var t = 0; t < targets; t++) {
            if (lists[t] is not { Count: > 0 } list) {
                continue;
            }

            var sorted = list.Select((e, order) => (e, order)).OrderBy(static x => x.e.Position)
                .ThenBy(static x => x.order)
                .Select(static x => x.e)
                .ToArray();
            lanes[t] = new(
                [..sorted.Select(static e => e.Position)],
                [..sorted.Select(static e => e.Value)],
                [..sorted.Select(static e => e.Step)]
            );
        }

        return lanes;
    }

    public static AutomationKind KindOf(string kind) =>
        kind switch {
            "volume" => AutomationKind.Volume,
            "pan" => AutomationKind.Pan,
            "mute" => AutomationKind.Mute,
            "send" => AutomationKind.Send,
            "insert" => AutomationKind.Insert,
            "instrument" => AutomationKind.Instrument,
            _ => AutomationKind.Unknown
        };
}

/// <summary>The model's automation: the targets the server numbered and each one's lane. Immutable.</summary>
public sealed class AutomationSet {
    public static readonly AutomationSet Empty = new([], []);

    public AutomationSet(EngineAutomationTarget?[] targets, AutomationLane?[] lanes) {
        Targets = targets;
        Lanes = lanes;
    }

    /// <summary>The targets by index (null where the server numbered none).</summary>
    public EngineAutomationTarget?[] Targets { get; }

    public AutomationLane?[] Lanes { get; }

    /// <summary>The lane of target <paramref name="index" />, or null.</summary>
    public AutomationLane? Lane(int index) => (uint)index < (uint)Lanes.Length ? Lanes[index] : null;

    /// <summary>The index of the target matching, or −1.</summary>
    public int Find(int trackId, AutomationKind kind, string? id = null, string? parameterId = null) {
        for (var i = 0; i < Targets.Length; i++) {
            if (Targets[i] is { } t
                && t.TrackId == trackId
                && AutomationLane.KindOf(t.Kind) == kind
                && (id is null || t.Id == id)
                && (parameterId is null || t.ParameterId == parameterId)) {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
///     Which targets a control move overrides (set by the control thread, cleared by the audio thread). A move
///     (<c>setStrip</c>, <c>setSendLevel</c>, <c>setInsertParameter</c>, <c>setParameter</c>) of an automated target
///     holds the moved value instead of the lane until the next play or locate, so a touch, latch or write pass is heard
///     while the server records it; the server's recompile then plays the recorded lane back.
/// </summary>
public sealed class AutomationOverrides(int count) {
    int[] held = new int[Math.Max(count, 0)];

    /// <summary>Control thread: grows to <paramref name="count" /> targets (before a model that has them is published).</summary>
    public void EnsureCapacity(int count) {
        if (count > held.Length) {
            var next = new int[count];
            held.AsSpan().CopyTo(next);
            Volatile.Write(ref held, next);
        }
    }

    /// <summary>Control thread: target <paramref name="index" /> follows the control, not its lane.</summary>
    public void Hold(int index) {
        var h = Volatile.Read(ref held);
        if ((uint)index < (uint)h.Length) {
            Volatile.Write(ref h[index], 1);
        }
    }

    /// <summary>Audio thread: whether target <paramref name="index" /> is held.</summary>
    public bool IsHeld(int index) {
        var h = Volatile.Read(ref held);
        return (uint)index < (uint)h.Length && Volatile.Read(ref h[index]) != 0;
    }

    /// <summary>Audio thread (play, locate): every target follows its lane again.</summary>
    public void Release() {
        var h = Volatile.Read(ref held);
        for (var i = 0; i < h.Length; i++) {
            if (h[i] != 0) {
                Volatile.Write(ref h[i], 0);
            }
        }
    }
}
