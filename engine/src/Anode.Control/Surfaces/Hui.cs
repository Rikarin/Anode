using Anode.Contracts;
using System.Globalization;

namespace Anode.Control.Surfaces;

/// <summary>
///     HUI (docs/09-phase-8.md, "Control surfaces"; docs/control/README.md): the ping, zones and ports, faders with
///     touch, VPots, the 4-character strips, meters, transport, banking. Checked against
///     <c>engine/tests/Anode.Control.Tests/HuiVectors.json</c>.
/// </summary>
/// <remarks>
///     The host pings (Note On 0 0 0) every second and the unit answers 0x90 0x00 0x7F; without input for 3 s the unit
///     is offline. A unit that pings itself is answered the same way. Switches arrive as a zone (CC 0x0F) then a port
///     (CC 0x2F: 0x40 | port pressed, port released); LEDs go out as a zone (CC 0x0C) then a port (CC 0x2C: 0x40 |
///     port on, port off).
/// </remarks>
public sealed class Hui : IControlSurface {
    const int StripCount = 8;
    const long PingInterval = 1_000_000_000;
    const long OfflineAfter = 3_000_000_000;

    readonly List<byte[]> _replies = [];
    readonly bool[] _touched = new bool[StripCount];
    readonly int[] _faderMsb = [-1, -1, -1, -1, -1, -1, -1, -1];
    readonly int?[] _fader = new int?[StripCount];
    readonly int?[] _ring = new int?[StripCount];
    readonly int?[] _meter = new int?[StripCount];
    readonly string?[] _names = new string?[StripCount];
    readonly Dictionary<(int Zone, int Port), bool> _leds = [];
    string? _timecode;
    int _zone = -1;
    long? _lastPing;
    long? _lastInput;
    ProjectView? _view;


    public ControlSurfaceKind Kind => ControlSurfaceKind.Hui;

    public int Strips => StripCount;

    public int BankOffset {
        get => field;
        set => field = Math.Max(0, value);
    }

    public bool Connected { get; private set; }

    /// <summary>What the VPots control: pan or a send (<see cref="VPotMode.PlugIn" /> is Mackie Control's alone).</summary>
    public VPotMode Mode { get; private set; }

    /// <summary>The send the VPots control in <see cref="VPotMode.Send" /> (0 is send A).</summary>
    public int SendIndex { get; private set; }

    internal int TrackCount => _view?.Tracks.Count ?? 0;

    /// <summary>The bank and channel buttons' steps since a <see cref="SurfaceGroup" /> last took them.</summary>
    internal int BankDelta { get; set; }

    public IReadOnlyList<SurfaceAction> Decode(ReadOnlySpan<byte> message, long timeNs) {
        var actions = new List<SurfaceAction>();
        foreach (var m in MidiStream.Split(message)) {
            if (m[0] == 0xF0) {
                MarkInput(timeNs);
                continue;
            }

            MarkInput(timeNs);
            switch (m[0] & 0xF0) {
                case 0x90 when m[1] == 0x00:
                    if (m[2] == 0x00) {
                        _replies.Add([0x90, 0x00, 0x7F]); // The unit pinged: answer.
                    }

                    break;
                case 0xB0:
                    Controller(m[1], m[2], actions);
                    break;
            }
        }

        return actions;
    }

    public IReadOnlyList<byte[]> Encode(ProjectView view) {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        var output = new List<byte[]>();
        for (var s = 0; s < StripCount; s++) {
            var name = SurfaceText.Name(TrackAt(s)?.Name, 4);
            if (_names[s] != name) {
                output.Add(
                    [0xF0, 0x00, 0x00, 0x66, 0x05, 0x00, 0x10, (byte)s, ..name.Select(static c => (byte)c), 0xF7]
                );
                _names[s] = name;
            }
        }

        for (var s = 0; s < StripCount; s++) {
            EncodeStrip(s, output);
        }

        var selected = Selected;
        Led(0x0B, 2, Mode == VPotMode.Pan, output);
        for (var send = 0; send < 5; send++) {
            Led(0x0B, 7 - send, Mode == VPotMode.Send && SendIndex == send, output);
        }

        Led(0x0E, 3, view.Transport == TransportState.Stopped, output);
        Led(0x0E, 4, view.Transport != TransportState.Stopped, output);
        Led(0x0E, 5, view.Transport == TransportState.Recording, output);
        Led(0x0F, 3, view.Cycle, output);
        Led(0x18, 1, selected?.Automation == AutomationMode.Latch, output);
        Led(0x18, 2, selected?.Automation == AutomationMode.Read, output);
        Led(0x18, 3, selected?.Automation == AutomationMode.Off, output);
        Led(0x18, 4, selected?.Automation == AutomationMode.Write, output);
        Led(0x18, 5, selected?.Automation == AutomationMode.Touch, output);

        // The timecode display: eight digits, the rightmost first.
        var tc = view.Timecode;
        var digits = string.Create(
            CultureInfo.InvariantCulture,
            $"{tc.Hours % 100:D2}{tc.Minutes:D2}{tc.Seconds:D2}{tc.Frames:D2}"
        );
        if (_timecode != digits) {
            var message = new byte[] { 0xF0, 0x00, 0x00, 0x66, 0x05, 0x00, 0x11, 0, 0, 0, 0, 0, 0, 0, 0, 0xF7 };
            for (var i = 0; i < 8; i++) {
                message[7 + i] = (byte)(digits[7 - i] - '0');
            }

            output.Add(message);
            _timecode = digits;
        }

        return output;
    }

    /// <summary>
    ///     Poly pressure 0xA0 per strip and side: strip, (side &lt;&lt; 4) | level 0…12, the left side then the right
    ///     (both the track's meter), as one buffer per strip; the same scale and decay as Mackie Control's.
    /// </summary>
    public IReadOnlyList<byte[]> EncodeMeters(ProjectView view) {
        ArgumentNullException.ThrowIfNull(view);
        var output = new List<byte[]>();
        for (var s = 0; s < StripCount; s++) {
            var index = BankOffset + s;
            var level = index < view.Tracks.Count
                ? SurfaceText.Round(Math.Clamp(view.Tracks[index].Meter, 0, 1) * 12)
                : 0;
            var shown = Math.Max(level, (_meter[s] ?? 0) - 1);
            if (shown > 0 || _meter[s] != shown) {
                output.Add([0xA0, (byte)s, (byte)shown, 0xA0, (byte)s, (byte)(0x10 | shown)]);
            }

            _meter[s] = shown;
        }

        return output;
    }

    /// <summary>Replies queued by <see cref="Decode" />, the ping every second, and the offline check.</summary>
    public IReadOnlyList<byte[]> Poll(long timeNs) {
        var output = new List<byte[]>(_replies);
        _replies.Clear();
        if (_lastPing is not { } last || timeNs - last >= PingInterval) {
            output.Add([0x90, 0x00, 0x00]);
            _lastPing = timeNs;
        }

        if (Connected && _lastInput is { } input && timeNs - input > OfflineAfter) {
            Connected = false;
        }

        return output;
    }

    public void Invalidate() {
        Array.Clear(_fader);
        Array.Clear(_ring);
        Array.Clear(_meter);
        Array.Clear(_names);
        _leds.Clear();
        _timecode = null;
    }

    void MarkInput(long timeNs) {
        _lastInput = timeNs;
        if (!Connected) {
            Connected = true;
            Invalidate();
        }
    }

    void Controller(int controller, int value, List<SurfaceAction> actions) {
        switch (controller) {
            case 0x0F:
                _zone = value;
                break;
            case 0x2F when _zone >= 0:
                Switch(_zone, value & 0x0F, (value & 0x40) != 0, actions);
                break;
            case <= 0x07:
                _faderMsb[controller] = value;
                break;
            case >= 0x20 and <= 0x27 when _faderMsb[controller - 0x20] >= 0:
                Fader(controller - 0x20, (_faderMsb[controller - 0x20] << 7) | value, actions);
                break;
            case >= 0x40 and <= 0x47:
                VPot(controller - 0x40, Steps(value), actions);
                break;
            case 0x0D when Steps(value) != 0:
                actions.Add(new(SurfaceActionKind.Jog, Value: Steps(value)));
                break;
        }
    }

    /// <summary>A VPot or the jog wheel: 0x40 | n is n steps clockwise, n alone n counter-clockwise.</summary>
    static int Steps(int value) => (value & 0x40) != 0 ? value & 0x3F : -(value & 0x3F);

    void Fader(int s, int value, List<SurfaceAction> actions) {
        _fader[s] = value;
        if (TrackAt(s) is { } track) {
            actions.Add(new(SurfaceActionKind.SetParameter, s, track.Id, "volume", value / 16383.0));
        }
    }

    void VPot(int s, int steps, List<SurfaceAction> actions) {
        if (steps == 0 || TrackAt(s) is not { } track) {
            return;
        }

        if (Mode == VPotMode.Pan) {
            actions.Add(new(SurfaceActionKind.StepParameter, s, track.Id, "pan", steps));
        } else if (SendIndex < track.Sends.Count) {
            actions.Add(new(SurfaceActionKind.StepParameter, s, track.Id, track.Sends[SendIndex].Address, steps));
        }
    }

    void Switch(int zone, int port, bool pressed, List<SurfaceAction> actions) {
        if (zone < StripCount) {
            StripSwitch(zone, port, pressed, actions);
        } else if (zone == 0x0E && port is 1 or 2) {
            actions.Add(new(port == 1 ? SurfaceActionKind.Rewind : SurfaceActionKind.Forward, Value: pressed ? 1 : 0));
        } else if (pressed && Function(zone, port) is { } kind) {
            actions.Add(new(kind, Value: 1));
        } else if (pressed) {
            Press(zone, port, actions);
        }
    }

    static SurfaceActionKind? Function(int zone, int port) =>
        (zone, port) switch {
            (0x0D, 0) => SurfaceActionKind.CursorDown,
            (0x0D, 1) => SurfaceActionKind.CursorLeft,
            (0x0D, 3) => SurfaceActionKind.CursorRight,
            (0x0D, 4) => SurfaceActionKind.CursorUp,
            (0x0E, 3) => SurfaceActionKind.Stop,
            (0x0E, 4) => SurfaceActionKind.Play,
            (0x0E, 5) => SurfaceActionKind.Record,
            (0x0F, 3) => SurfaceActionKind.Cycle,
            _ => null
        };

    static AutomationMode? AutomationFor(int port) =>
        port switch {
            1 => AutomationMode.Latch,
            2 => AutomationMode.Read,
            3 => AutomationMode.Off,
            4 => AutomationMode.Write,
            5 => AutomationMode.Touch,
            _ => null
        };

    void Press(int zone, int port, List<SurfaceAction> actions) {
        switch (zone, port) {
            case (0x0A, _):
                Bank(
                    port switch {
                        0 => -1,
                        1 => -8,
                        2 => 1,
                        _ => 8
                    },
                    actions
                );
                break;
            case (0x0B, 2):
                Mode = VPotMode.Pan;
                break;
            case (0x0B, >= 3 and <= 7):
                Mode = VPotMode.Send;
                SendIndex = 7 - port;
                break;
            case (0x0D, 2):
                _zoom = !_zoom;
                actions.Add(new(SurfaceActionKind.Zoom, Value: _zoom ? 1 : 0));
                break;
            case (0x18, _) when AutomationFor(port) is { } mode && Selected is { } track:
                actions.Add(new(SurfaceActionKind.SetAutomationMode, TrackId: track.Id, Automation: mode));
                break;
        }
    }

    bool _zoom;

    void StripSwitch(int s, int port, bool pressed, List<SurfaceAction> actions) {
        var track = TrackAt(s);
        if (port == 0) {
            Touch(s, pressed, track, actions);
            return;
        }

        if (!pressed || track is null) {
            return;
        }

        SurfaceAction? action = port switch {
            1 => new(SurfaceActionKind.Select, s, track.Id, Value: 1),
            2 => new(SurfaceActionKind.Mute, s, track.Id, Value: track.Mute ? 0 : 1),
            3 => new(SurfaceActionKind.Solo, s, track.Id, Value: track.Solo ? 0 : 1),
            5 when Mode == VPotMode.Pan => new(SurfaceActionKind.SetParameter, s, track.Id, "pan", 0.5),
            7 => new(SurfaceActionKind.RecordArm, s, track.Id, Value: track.RecordArm ? 0 : 1),
            _ => null
        };
        if (action is { } a) {
            actions.Add(a);
        }
    }

    void Touch(int s, bool pressed, TrackView? track, List<SurfaceAction> actions) {
        _touched[s] = pressed;
        if (!pressed) {
            _fader[s] = null;
        }

        if (track is not null) {
            actions.Add(new(SurfaceActionKind.Touch, s, track.Id, "volume", pressed ? 1 : 0));
        }
    }

    void Bank(int delta, List<SurfaceAction> actions) {
        BankDelta += delta;
        var offset = Math.Clamp(BankOffset + delta, 0, Math.Max(0, TrackCount - 1));
        if (offset != BankOffset) {
            BankOffset = offset;
            actions.Add(new(SurfaceActionKind.Banked, Value: offset));
        }
    }

    TrackView? Selected => _view?.Tracks.FirstOrDefault(static t => t.Selected);

    TrackView? TrackAt(int s) {
        var index = BankOffset + s;
        return _view is not null && index < _view.Tracks.Count ? _view.Tracks[index] : null;
    }

    void EncodeStrip(int s, List<byte[]> output) {
        var track = TrackAt(s);
        var fader = track is null ? 0 : SurfaceText.Round(Math.Clamp(track.Fader, 0, 1) * 16383);
        if (!_touched[s] && _fader[s] != fader) {
            output.Add([0xB0, (byte)s, (byte)(fader >> 7), 0xB0, (byte)(0x20 + s), (byte)(fader & 0x7F)]);
            _fader[s] = fader;
        }

        var ring = track is null
            ? 0
            : Mode == VPotMode.Pan
                ? 1 + SurfaceText.Round((Math.Clamp(track.Pan, -1, 1) + 1) / 2 * 10)
                : SendIndex < track.Sends.Count
                    ? 0x20 | SurfaceText.Round(Math.Clamp(track.Sends[SendIndex].Value, 0, 1) * 11)
                    : 0;
        if (_ring[s] != ring) {
            output.Add([0xB0, (byte)(0x10 + s), (byte)ring]);
            _ring[s] = ring;
        }

        Led(s, 1, track?.Selected ?? false, output);
        Led(s, 2, track?.Mute ?? false, output);
        Led(s, 3, track?.Solo ?? false, output);
        Led(s, 7, track?.RecordArm ?? false, output);
    }

    void Led(int zone, int port, bool on, List<byte[]> output) {
        if (_leds.TryGetValue((zone, port), out var was) && was == on) {
            return;
        }

        output.Add([0xB0, 0x0C, (byte)zone, 0xB0, 0x2C, (byte)(on ? 0x40 | port : port)]);
        _leds[(zone, port)] = on;
    }
}
