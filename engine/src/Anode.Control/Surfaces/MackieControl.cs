using Anode.Contracts;
using System.Globalization;

namespace Anode.Control.Surfaces;

/// <summary>What a surface's VPots control.</summary>
public enum VPotMode {
    /// <summary>Each strip's pan (a press centres it).</summary>
    Pan,

    /// <summary>Each strip's send <c>SendIndex</c> (the Send button again steps to the next send).</summary>
    Send,

    /// <summary>
    ///     The selected track's inserts: first the inserts by name (a VPot press chooses one), then the chosen insert's
    ///     parameters, paged by the bank and channel buttons. Mackie Control only.
    /// </summary>
    PlugIn
}

/// <summary>
///     The state the units of a <see cref="SurfaceGroup" /> share: the VPot mode, send, chosen insert and page, the
///     name/value display, the timecode display's mode (SMPTE or beats) and zoom.
/// </summary>
sealed class SurfaceState {
    public VPotMode Mode { get; set; }

    public int SendIndex { get; set; }

    public string? InsertId { get; set; }

    public string? InsertTrackId { get; set; }

    public int Page { get; set; }

    public bool NameValue { get; set; }

    public bool Beats { get; set; }

    public bool Zoom { get; set; }
}

/// <summary>
///     Mackie Control and Mackie Control XT (docs/09-phase-8.md, "Control surfaces"; docs/control/README.md): 14-bit
///     motor faders with touch, VPots with LED rings, the strip buttons with LEDs, the two-line scribble strip, meters,
///     the timecode and assignment displays, banking by 8 and 1, transport, jog, cursor and zoom, automation modes,
///     name/value; VPot modes pan, sends and plug-in (the selected track's insert, parameters paged by 8). An extender
///     has the strips alone. Checked against <c>engine/tests/Anode.Control.Tests/McuVectors.json</c>.
/// </summary>
public sealed class MackieControl : IControlSurface {
    const int StripCount = 8;
    const long QueryInterval = 2_000_000_000;

    readonly List<byte[]> _replies = [];
    readonly bool[] _touched = new bool[StripCount];
    readonly int?[] _fader = new int?[StripCount];
    readonly int?[] _ring = new int?[StripCount];
    readonly int?[] _meter = new int?[StripCount];
    readonly int?[] _display = new int?[12];
    readonly Dictionary<int, bool> _leds = [];
    char[]? _scribble;
    bool _meterModeSent;
    long? _lastQuery;
    ProjectView? _view;


    public MackieControl(bool extender) {
        Extender = extender;
    }

    /// <summary>Whether this is a Mackie Control XT (strips only; SysEx device id 0x15 rather than 0x14).</summary>
    public bool Extender { get; }

    public ControlSurfaceKind Kind => Extender ? ControlSurfaceKind.MackieControlXt : ControlSurfaceKind.MackieControl;

    public int Strips => StripCount;

    public int BankOffset {
        get => field;
        set => field = Math.Max(0, value);
    }

    public bool Connected { get; private set; }

    /// <summary>What the VPots control (shared by the units of a <see cref="SurfaceGroup" />).</summary>
    public VPotMode Mode => State.Mode;

    internal SurfaceState State { get; set; } = new();

    /// <summary>The plug-in mode's first parameter on strip 0, relative to the page (the strips left of this unit).</summary>
    internal int PlugInBase { get; set; }

    internal int TrackCount => _view?.Tracks.Count ?? 0;

    /// <summary>The bank and channel buttons' steps since a <see cref="SurfaceGroup" /> last took them.</summary>
    internal int BankDelta { get; set; }

    byte DeviceId => Extender ? (byte)0x15 : (byte)0x14;

    public IReadOnlyList<SurfaceAction> Decode(ReadOnlySpan<byte> message, long timeNs) {
        var actions = new List<SurfaceAction>();
        foreach (var m in MidiStream.Split(message)) {
            if (m[0] == 0xF0) {
                DecodeSysEx(m);
                continue;
            }

            MarkConnected();
            switch (m[0] & 0xF0) {
                case 0xE0:
                    Fader(m[0] & 0x0F, (m[2] << 7) | m[1], actions);
                    break;
                case 0x90 or 0x80:
                    Button(m[1], (m[0] & 0xF0) == 0x90 && m[2] > 0, actions);
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
        if (!_meterModeSent) {
            // Signal LEDs on, peak hold and the LCD meter off, for each strip.
            for (var s = 0; s < StripCount; s++) {
                output.Add(SysEx(0x20, (byte)s, 0x01));
            }

            _meterModeSent = true;
        }

        EncodeScribble(output);
        for (var s = 0; s < StripCount; s++) {
            EncodeStrip(s, output);
        }

        if (!Extender) {
            EncodeGlobalLeds(view, output);
            EncodeDisplays(view, output);
        }

        return output;
    }

    /// <summary>
    ///     Channel pressure 0xD0 per strip, (strip &lt;&lt; 4) | level 0…12 (the meter × 12, rounded); the level falls
    ///     at most one step per call (called 10 times a second: 1.2 s from full to off). A strip is sent while its level
    ///     is above 0 (the unit's own decay would drop it otherwise) and once when it reaches 0.
    /// </summary>
    public IReadOnlyList<byte[]> EncodeMeters(ProjectView view) {
        ArgumentNullException.ThrowIfNull(view);
        var output = new List<byte[]>();
        for (var s = 0; s < StripCount; s++) {
            var track = TrackAt(view, s);
            var level = track is null ? 0 : SurfaceText.Round(Math.Clamp(track.Meter, 0, 1) * 12);
            var shown = Math.Max(level, (_meter[s] ?? 0) - 1);
            if (shown > 0 || _meter[s] != shown) {
                output.Add([0xD0, (byte)((s << 4) | shown)]);
            }

            _meter[s] = shown;
        }

        return output;
    }

    /// <summary>
    ///     Replies queued by <see cref="Decode" /> (the handshake's), then, while not connected, the device query every
    ///     2 s.
    /// </summary>
    public IReadOnlyList<byte[]> Poll(long timeNs) {
        var output = new List<byte[]>(_replies);
        _replies.Clear();
        if (!Connected && (_lastQuery is not { } last || timeNs - last >= QueryInterval)) {
            output.Add(SysEx(0x00));
            _lastQuery = timeNs;
        }

        return output;
    }

    public void Invalidate() {
        Array.Clear(_fader);
        Array.Clear(_ring);
        Array.Clear(_meter);
        Array.Clear(_display);
        _leds.Clear();
        _scribble = null;
        _meterModeSent = false;
    }

    /// <summary>
    ///     The Host Connection Reply's response to a Host Connection Query's challenge (the protocol's published
    ///     formula).
    /// </summary>
    internal static byte[] Response(ReadOnlySpan<byte> challenge) {
        int l1 = challenge[0], l2 = challenge[1], l3 = challenge[2], l4 = challenge[3];
        return [
            (byte)(0x7F & (l1 + (l2 ^ 0xA) - l4)),
            (byte)(0x7F & ((l3 >> 4) ^ (l1 + l4))),
            (byte)(0x7F & ((l4 - (l3 << 2)) ^ (l1 | l2))),
            (byte)(0x7F & (l2 - l3 + (0xF0 ^ (l4 << 4))))
        ];
    }

    void MarkConnected() {
        if (!Connected) {
            Connected = true;
            Invalidate();
        }
    }

    void DecodeSysEx(byte[] m) {
        if (m.Length < 7 || m[1] != 0x00 || m[2] != 0x00 || m[3] != 0x66) {
            return;
        }

        switch (m[5]) {
            case 0x01 when m.Length >= 18:
                // Host Connection Query: serial (7), challenge (4). Reply with the serial and the response.
                MarkConnected();
                var reply = new List<byte> {
                    0xF0,
                    0x00,
                    0x00,
                    0x66,
                    DeviceId,
                    0x02
                };
                reply.AddRange(m.AsSpan(6, 7).ToArray());
                reply.AddRange(Response(m.AsSpan(13, 4)));
                reply.Add(0xF7);
                _replies.Add([..reply]);
                break;
            case 0x04 or 0x0F:
                // Connection error, or the unit went offline.
                Connected = false;
                break;
            default:
                MarkConnected();
                break;
        }
    }

    void Fader(int channel, int value, List<SurfaceAction> actions) {
        if (channel >= StripCount) {
            return; // The master fader: Anode's view has no master strip.
        }

        _fader[channel] = value;
        if (TrackAt(channel) is { } track) {
            actions.Add(new(SurfaceActionKind.SetParameter, channel, track.Id, "volume", value / 16383.0));
        }
    }

    void Button(int note, bool pressed, List<SurfaceAction> actions) {
        if (note is >= 0x68 and <= 0x6F) {
            Touch(note - 0x68, pressed, actions);
        } else if (!pressed) {
            Release(note, actions);
        } else if (note < 0x20) {
            StripButton(note >> 3, note & 7, actions);
        } else if (note < 0x28) {
            VPotPress(note - 0x20, actions);
        } else if (!Extender) {
            MainButton(note, actions);
        }
    }

    void Touch(int s, bool pressed, List<SurfaceAction> actions) {
        _touched[s] = pressed;
        if (!pressed) {
            _fader[s] = null; // Sends the project's value after the release.
        }

        if (TrackAt(s) is { } track) {
            actions.Add(new(SurfaceActionKind.Touch, s, track.Id, "volume", pressed ? 1 : 0));
        }
    }

    void Release(int note, List<SurfaceAction> actions) {
        if (!Extender && note is 0x5B or 0x5C) {
            actions.Add(new(note == 0x5B ? SurfaceActionKind.Rewind : SurfaceActionKind.Forward));
        }
    }

    void StripButton(int row, int s, List<SurfaceAction> actions) {
        if (TrackAt(s) is not { } track) {
            return;
        }

        actions.Add(
            row switch {
                0 => new(SurfaceActionKind.RecordArm, s, track.Id, Value: track.RecordArm ? 0 : 1),
                1 => new(SurfaceActionKind.Solo, s, track.Id, Value: track.Solo ? 0 : 1),
                2 => new(SurfaceActionKind.Mute, s, track.Id, Value: track.Mute ? 0 : 1),
                _ => new(SurfaceActionKind.Select, s, track.Id, Value: 1)
            }
        );
    }

    void MainButton(int note, List<SurfaceAction> actions) {
        switch (note) {
            case 0x29:
                if (State.Mode == VPotMode.Send) {
                    var sends = _view?.Tracks.Select(static t => t.Sends.Count).DefaultIfEmpty(0).Max() ?? 0;
                    State.SendIndex = (State.SendIndex + 1) % Math.Max(1, sends);
                } else {
                    State.Mode = VPotMode.Send;
                    State.SendIndex = 0;
                }

                break;
            case 0x2A:
                State.Mode = VPotMode.Pan;
                break;
            case 0x2B:
                State.Mode = VPotMode.PlugIn;
                State.InsertId = null;
                State.Page = 0;
                break;
            case 0x2E or 0x2F or 0x30 or 0x31:
                Bank(
                    note switch {
                        0x2E => -8,
                        0x2F => 8,
                        0x30 => -1,
                        _ => 1
                    },
                    actions
                );
                break;
            case 0x34:
                State.NameValue = !State.NameValue;
                break;
            case 0x35:
                State.Beats = !State.Beats;
                break;
            case 0x4A:
                Automation(
                    Selected?.Automation == AutomationMode.Read ? AutomationMode.Off : AutomationMode.Read,
                    actions
                );
                break;
            case 0x4B:
                Automation(AutomationMode.Write, actions);
                break;
            case 0x4D:
                Automation(AutomationMode.Touch, actions);
                break;
            case 0x4E:
                Automation(AutomationMode.Latch, actions);
                break;
            case 0x64:
                State.Zoom = !State.Zoom;
                actions.Add(new(SurfaceActionKind.Zoom, Value: State.Zoom ? 1 : 0));
                break;
            default:
                if (Function(note) is { } kind) {
                    actions.Add(new(kind, Value: 1));
                }

                break;
        }
    }

    static SurfaceActionKind? Function(int note) =>
        note switch {
            0x56 => SurfaceActionKind.Cycle,
            0x5B => SurfaceActionKind.Rewind,
            0x5C => SurfaceActionKind.Forward,
            0x5D => SurfaceActionKind.Stop,
            0x5E => SurfaceActionKind.Play,
            0x5F => SurfaceActionKind.Record,
            0x60 => SurfaceActionKind.CursorUp,
            0x61 => SurfaceActionKind.CursorDown,
            0x62 => SurfaceActionKind.CursorLeft,
            0x63 => SurfaceActionKind.CursorRight,
            _ => null
        };

    void Automation(AutomationMode mode, List<SurfaceAction> actions) {
        if (Selected is { } track) {
            actions.Add(new(SurfaceActionKind.SetAutomationMode, TrackId: track.Id, Automation: mode));
        }
    }

    void Bank(int delta, List<SurfaceAction> actions) {
        if (State.Mode == VPotMode.PlugIn) {
            var count = ChosenInsert() is { } insert ? insert.Parameters.Count : Selected?.Inserts.Count ?? 0;
            State.Page = Math.Clamp(State.Page + delta, 0, Math.Max(0, count - 1));
            return;
        }

        BankDelta += delta;
        var offset = Math.Clamp(BankOffset + delta, 0, Math.Max(0, TrackCount - 1));
        if (offset != BankOffset) {
            BankOffset = offset;
            actions.Add(new(SurfaceActionKind.Banked, Value: offset));
        }
    }

    void Controller(int controller, int value, List<SurfaceAction> actions) {
        var steps = (value & 0x40) != 0 ? -(value & 0x3F) : value & 0x3F;
        if (steps == 0) {
            return;
        }

        if (controller is >= 0x10 and <= 0x17) {
            VPot(controller - 0x10, steps, actions);
        } else if (controller == 0x3C && !Extender) {
            actions.Add(new(SurfaceActionKind.Jog, Value: steps));
        }
    }

    void VPot(int s, int steps, List<SurfaceAction> actions) {
        switch (State.Mode) {
            case VPotMode.Pan when TrackAt(s) is { } track:
                actions.Add(new(SurfaceActionKind.StepParameter, s, track.Id, "pan", steps));
                break;
            case VPotMode.Send when TrackAt(s) is { } track && SendOf(track) is { } send:
                actions.Add(new(SurfaceActionKind.StepParameter, s, track.Id, send.Address, steps));
                break;
            case VPotMode.PlugIn when PlugInParameter(s) is { } parameter:
                actions.Add(new(SurfaceActionKind.StepParameter, s, State.InsertTrackId, parameter.Address, steps));
                break;
        }
    }

    void VPotPress(int s, List<SurfaceAction> actions) {
        if (State.Mode == VPotMode.Pan && TrackAt(s) is { } track) {
            actions.Add(new(SurfaceActionKind.SetParameter, s, track.Id, "pan", 0.5));
        } else if (State.Mode == VPotMode.PlugIn && ChosenInsert() is null && Selected is { } selected) {
            var index = State.Page + PlugInBase + s;
            if (index < selected.Inserts.Count) {
                State.InsertId = selected.Inserts[index].Id;
                State.InsertTrackId = selected.Id;
                State.Page = 0;
            }
        }
    }

    TrackView? Selected => _view?.Tracks.FirstOrDefault(static t => t.Selected);

    TrackView? TrackAt(int s) => _view is null ? null : TrackAt(_view, s);

    TrackView? TrackAt(ProjectView view, int s) {
        var index = BankOffset + s;
        return index < view.Tracks.Count ? view.Tracks[index] : null;
    }

    ParameterView? SendOf(TrackView track) => State.SendIndex < track.Sends.Count ? track.Sends[State.SendIndex] : null;

    InsertView? ChosenInsert() {
        if (State.InsertId is null) {
            return null;
        }

        var track = Selected;
        var insert = track?.Id == State.InsertTrackId
            ? track?.Inserts.FirstOrDefault(i => i.Id == State.InsertId)
            : null;
        if (insert is null) {
            State.InsertId = null; // The selection or the inserts changed: back to choosing.
            State.Page = 0;
        }

        return insert;
    }

    ParameterView? PlugInParameter(int s) {
        var insert = ChosenInsert();
        var index = State.Page + PlugInBase + s;
        return insert is not null && index >= 0 && index < insert.Parameters.Count ? insert.Parameters[index] : null;
    }

    void EncodeStrip(int s, List<byte[]> output) {
        var track = TrackAt(s);
        var fader = track is null ? 0 : SurfaceText.Round(Math.Clamp(track.Fader, 0, 1) * 16383);
        if (!_touched[s] && _fader[s] != fader) {
            output.Add([(byte)(0xE0 | s), (byte)(fader & 0x7F), (byte)(fader >> 7)]);
            _fader[s] = fader;
        }

        var ring = Ring(track, s);
        if (_ring[s] != ring) {
            output.Add([0xB0, (byte)(0x30 + s), (byte)ring]);
            _ring[s] = ring;
        }

        Led(s, track?.RecordArm ?? false, output);
        Led(0x08 + s, track?.Solo ?? false, output);
        Led(0x10 + s, track?.Mute ?? false, output);
        Led(0x18 + s, track?.Selected ?? false, output);
    }

    /// <summary>A VPot's LED ring: (mode &lt;&lt; 4) | position 0…11; pan as a dot (1…11, 6 centre), levels as a fill.</summary>
    int Ring(TrackView? track, int s) {
        switch (State.Mode) {
            case VPotMode.Pan when track is not null:
                return 1 + SurfaceText.Round((Math.Clamp(track.Pan, -1, 1) + 1) / 2 * 10);
            case VPotMode.Send when track is not null && SendOf(track) is { } send:
                return 0x20 | SurfaceText.Round(Math.Clamp(send.Value, 0, 1) * 11);
            case VPotMode.PlugIn when PlugInParameter(s) is { } parameter:
                return 0x20 | SurfaceText.Round(Math.Clamp(parameter.Value, 0, 1) * 11);
            default:
                return 0;
        }
    }

    void Led(int note, bool on, List<byte[]> output) {
        if (_leds.TryGetValue(note, out var was) && was == on) {
            return;
        }

        output.Add([0x90, (byte)note, on ? (byte)0x7F : (byte)0x00]);
        _leds[note] = on;
    }

    void EncodeGlobalLeds(ProjectView view, List<byte[]> output) {
        var automation = Selected?.Automation;
        Led(0x28, false, output);
        Led(0x29, State.Mode == VPotMode.Send, output);
        Led(0x2A, State.Mode == VPotMode.Pan, output);
        Led(0x2B, State.Mode == VPotMode.PlugIn, output);
        Led(0x4A, automation == AutomationMode.Read, output);
        Led(0x4B, automation == AutomationMode.Write, output);
        Led(0x4C, false, output);
        Led(0x4D, automation == AutomationMode.Touch, output);
        Led(0x4E, automation == AutomationMode.Latch, output);
        Led(0x56, view.Cycle, output);
        Led(0x5D, view.Transport == TransportState.Stopped, output);
        Led(0x5E, view.Transport != TransportState.Stopped, output);
        Led(0x5F, view.Transport == TransportState.Recording, output);
        Led(0x64, State.Zoom, output);
        Led(0x71, !State.Beats, output);
        Led(0x72, State.Beats, output);
    }

    /// <summary>
    ///     The timecode display (CC 0x49 the leftmost digit … 0x40 the rightmost) and the assignment display (CC 0x4B
    ///     left, 0x4A right), in the unit's 7-segment character set (+0x40 lights a digit's dot).
    /// </summary>
    void EncodeDisplays(ProjectView view, List<byte[]> output) {
        var codes = new int[12];
        var text = State.Beats
            ? Pad(view.Beats.Bar, 3) + Pad(view.Beats.Beat, 2) + Pad(view.Beats.Division, 2) + Pad(view.Beats.Ticks, 3)
            : Pad(view.Timecode.Hours, 3)
            + Two(view.Timecode.Minutes)
            + Two(view.Timecode.Seconds)
            + Two(view.Timecode.Frames).PadLeft(3);
        for (var i = 0; i < 10; i++) {
            codes[9 - i] = Seven(text[i]) | (i is 2 or 4 or 6 ? 0x40 : 0);
        }

        var assignment = State.Mode switch {
            VPotMode.Send => "S" + ((State.SendIndex + 1) % 10).ToString(CultureInfo.InvariantCulture),
            VPotMode.PlugIn => "PI",
            _ => "PN"
        };
        codes[11] = Seven(assignment[0]);
        codes[10] = Seven(assignment[1]);
        for (var i = 11; i >= 0; i--) {
            if (_display[i] != codes[i]) {
                output.Add([0xB0, (byte)(0x40 + i), (byte)codes[i]]);
                _display[i] = codes[i];
            }
        }
    }

    static string Pad(int value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width);

    static string Two(int value) => value.ToString("D2", CultureInfo.InvariantCulture);

    static int Seven(char c) =>
        c switch {
            >= '@' and <= '_' => c - 0x40,
            >= '`' and <= '\u007f' => c - 0x60,
            >= ' ' and <= '?' => c,
            _ => 0x20
        };

    /// <summary>
    ///     The scribble strip: two lines of 56 characters (7 a strip: 6 and a space), offset 0 the top line and 0x38 the
    ///     bottom; everything after an invalidate, else each changed line from its first change to its last.
    /// </summary>
    void EncodeScribble(List<byte[]> output) {
        var desired = new char[112];
        for (var s = 0; s < StripCount; s++) {
            var (top, bottom) = StripText(s);
            (top + " ").CopyTo(0, desired, s * 7, 7);
            (bottom + " ").CopyTo(0, desired, 56 + s * 7, 7);
        }

        if (_scribble is null) {
            output.Add(Scribble(0, desired));
        } else {
            for (var line = 0; line < 2; line++) {
                if (Changed(_scribble, desired, line * 56, 56) is var (first, length)) {
                    output.Add(Scribble(first, desired.AsSpan(first, length)));
                }
            }
        }

        _scribble = desired;
    }

    /// <summary>
    ///     The span from the first to the last character that differs within <paramref name="count" /> from
    ///     <paramref name="start" />.
    /// </summary>
    static (int First, int Length)? Changed(char[] sent, char[] desired, int start, int count) {
        int first = -1, last = -1;
        for (var i = start; i < start + count; i++) {
            if (desired[i] == sent[i]) {
                continue;
            }

            first = first < 0 ? i : first;
            last = i;
        }

        return first < 0 ? null : (first, last - first + 1);
    }

    const string Blank = "      ";

    (string Top, string Bottom) StripText(int s) {
        if (State.Mode == VPotMode.PlugIn) {
            return PlugInText(s);
        }

        if (TrackAt(s) is not { } track) {
            return (Blank, Blank);
        }

        if (State.Mode == VPotMode.Send) {
            var send = SendOf(track);
            var top = State.NameValue && send is not null ? send.Name : track.Name;
            return (SurfaceText.Name(top, 6), send is null ? Blank : SurfaceText.Value(send.Display, 6));
        }

        return (SurfaceText.Name(State.NameValue ? "Pan" : track.Name, 6),
            SurfaceText.Value(SurfaceText.Pan(track.Pan), 6));
    }

    (string Top, string Bottom) PlugInText(int s) {
        if (ChosenInsert() is not null) {
            return PlugInParameter(s) is { } p
                ? (SurfaceText.Name(p.Name, 6), SurfaceText.Value(p.Display, 6))
                : (Blank, Blank);
        }

        var inserts = Selected?.Inserts ?? [];
        var index = State.Page + PlugInBase + s;
        return index >= 0 && index < inserts.Count ? (SurfaceText.Name(inserts[index].Name, 6), Blank) : (Blank, Blank);
    }

    byte[] Scribble(int offset, ReadOnlySpan<char> text) {
        var message = new byte[text.Length + 8];
        message[0] = 0xF0;
        message[3] = 0x66;
        message[4] = DeviceId;
        message[5] = 0x12;
        message[6] = (byte)offset;
        for (var i = 0; i < text.Length; i++) {
            message[7 + i] = (byte)text[i];
        }

        message[^1] = 0xF7;
        return message;
    }

    byte[] SysEx(byte command, params byte[] data) {
        var message = new byte[data.Length + 7];
        message[0] = 0xF0;
        message[3] = 0x66;
        message[4] = DeviceId;
        message[5] = command;
        data.CopyTo(message, 6);
        message[^1] = 0xF7;
        return message;
    }
}
