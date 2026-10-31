namespace Anode.Control.Surfaces;

/// <summary>
///     The control surfaces that act as one console, left to right in <c>ControlSurface.order</c>: one bank across all
///     of them (docs/control/README.md, "Banking"). The group owns the bank: <see cref="BankBase" /> is the index in
///     <see cref="ProjectView.Tracks" /> of the track on the leftmost strip, and each unit's
///     <see cref="IControlSurface.BankOffset" /> is the base plus the strips left of it. A bank or channel button on
///     any unit moves the whole group (by 8 or 1); a Mackie Control's VPot mode, send, plug-in page, name/value and
///     display modes are shared with the Mackie Control XTs of its group (the first Mackie Control is the main unit),
///     and in plug-in mode the parameters run on across the units. Used from one thread, like its units.
/// </summary>
public sealed class SurfaceGroup {
    readonly int[] _start;

    /// <summary>A group of <paramref name="units" />, left to right; it takes over their bank offsets.</summary>
    public SurfaceGroup(IReadOnlyList<IControlSurface> units) {
        ArgumentNullException.ThrowIfNull(units);
        Units = [..units];
        _start = new int[Units.Count];
        var strips = 0;
        var main = Units.OfType<MackieControl>().FirstOrDefault(static m => !m.Extender);
        for (var i = 0; i < Units.Count; i++) {
            _start[i] = strips;
            strips += Units[i].Strips;
            if (Units[i] is MackieControl mackie && main is not null) {
                mackie.State = main.State;
                mackie.PlugInBase = _start[i];
            }
        }

        Strips = strips;
        Bank(Units.Count > 0 ? Units[0].BankOffset : 0, int.MaxValue);
    }

    /// <summary>The units, left to right.</summary>
    public IReadOnlyList<IControlSurface> Units { get; }

    /// <summary>The strips of every unit.</summary>
    public int Strips { get; }

    /// <summary>The index of the track on the leftmost strip.</summary>
    public int BankBase { get; private set; }

    /// <summary>The strips left of unit <paramref name="unit" />.</summary>
    public int StripsBefore(int unit) => _start[unit];

    /// <summary>
    ///     Moves the bank to <paramref name="bankBase" />, within 0 and the last of <paramref name="trackCount" />
    ///     tracks, and sets every unit's offset from it. True when the base changed.
    /// </summary>
    public bool Bank(int bankBase, int trackCount) {
        var clamped = Math.Clamp(bankBase, 0, Math.Max(0, trackCount - 1));
        var changed = clamped != BankBase;
        BankBase = clamped;
        for (var i = 0; i < Units.Count; i++) {
            Units[i].BankOffset = clamped + _start[i];
        }

        return changed;
    }

    /// <summary>
    ///     A message from unit <paramref name="unit" /> (<see cref="IControlSurface.Decode" />): a bank or channel button
    ///     moves the group, and its <see cref="SurfaceActionKind.Banked" /> action carries the group's new
    ///     <see cref="BankBase" /> (dropped when the base did not move).
    /// </summary>
    public IReadOnlyList<SurfaceAction> Decode(int unit, ReadOnlySpan<byte> message, long timeNs) {
        var surface = Units[unit];
        var actions = surface.Decode(message, timeNs);
        var delta = TakeBankDelta(surface);
        var result = new List<SurfaceAction>(actions.Count);
        foreach (var action in actions) {
            if (action.Kind != SurfaceActionKind.Banked) {
                result.Add(action);
            }
        }

        if (delta != 0 && Bank(BankBase + delta, TrackCount(surface))) {
            result.Add(new(SurfaceActionKind.Banked, Value: BankBase));
        } else {
            Bank(BankBase, int.MaxValue); // Undoes a unit's own move.
        }

        return result;
    }

    /// <summary>
    ///     Every unit's <see cref="IControlSurface.Encode" />, in unit order, after bringing the bank within
    ///     <paramref name="view" />'s tracks (they may have gone).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<byte[]>> Encode(ProjectView view) {
        ArgumentNullException.ThrowIfNull(view);
        Bank(BankBase, view.Tracks.Count);
        var output = new List<IReadOnlyList<byte[]>>(Units.Count);
        foreach (var unit in Units) {
            output.Add(unit.Encode(view));
        }

        return output;
    }

    /// <summary>Every unit's <see cref="IControlSurface.EncodeMeters" />, in unit order.</summary>
    public IReadOnlyList<IReadOnlyList<byte[]>> EncodeMeters(ProjectView view) {
        var output = new List<IReadOnlyList<byte[]>>(Units.Count);
        foreach (var unit in Units) {
            output.Add(unit.EncodeMeters(view));
        }

        return output;
    }

    /// <summary>Every unit's <see cref="IControlSurface.Poll" />, in unit order.</summary>
    public IReadOnlyList<IReadOnlyList<byte[]>> Poll(long timeNs) {
        var output = new List<IReadOnlyList<byte[]>>(Units.Count);
        foreach (var unit in Units) {
            output.Add(unit.Poll(timeNs));
        }

        return output;
    }

    static int TakeBankDelta(IControlSurface surface) {
        var delta = 0;
        switch (surface) {
            case MackieControl m:
                delta = m.BankDelta;
                m.BankDelta = 0;
                break;
            case Hui h:
                delta = h.BankDelta;
                h.BankDelta = 0;
                break;
        }

        return delta;
    }

    static int TrackCount(IControlSurface surface) =>
        surface switch {
            MackieControl m => m.TrackCount,
            Hui h => h.TrackCount,
            _ => int.MaxValue
        };
}
