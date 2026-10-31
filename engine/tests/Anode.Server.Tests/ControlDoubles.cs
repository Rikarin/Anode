using Anode.Contracts;
using Anode.Control.Assignments;
using Anode.Control.Osc;
using Anode.Control.Surfaces;
using Anode.Server.Control;
using Anode.Stems;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     Stand-ins for workstream D's control protocols, enough to drive the server's control service end to end: a
///     small assignment engine (7-bit CC, notes; absolute, relative two's complement, toggle, momentary; pickup;
///     zone modes), a made-up surface protocol (<see cref="TestSurface" />), and OSC 1.0 with a few of Anode's addresses
///     (<see cref="TestOsc" />). The protocols themselves are D's to test, against their byte vectors.
/// </summary>
sealed class TestProtocols : IControlProtocols {
    public ConcurrentQueue<TestSurface> Surfaces { get; } = new();

    public IAssignmentRuntime Assignments(ControllerAssignments assignments) => new TestAssignments(assignments);

    public IControlSurface Surface(ControlSurfaceKind kind) {
        var surface = new TestSurface(kind);
        Surfaces.Enqueue(surface);
        return surface;
    }

    public OscPacket DecodeOsc(ReadOnlySpan<byte> data) => TestOsc.Decode(data);

    public byte[] EncodeOsc(OscPacket packet) => TestOsc.Encode((OscMessage)packet);

    public OscRequest? ResolveOsc(OscMessage message, ProjectView view) => TestOsc.Resolve(message, view);

    public IReadOnlyList<OscMessage> OscFeedback(ProjectView view) => TestOsc.Feedback(view);
}

/// <summary>A small assignment engine: 7-bit control changes and notes, the modes the service's flows need.</summary>
sealed class TestAssignments(ControllerAssignments assignments) : IAssignmentRuntime {
    readonly Dictionary<string, (bool Picked, double? Last)> pickup = [];

    public ControllerAssignments Assignments { get; private set; } = assignments;

    public IReadOnlyList<ControlMatcher> Matchers => [
        ..Assignments.Zones.SelectMany(static z => z.Modes)
            .SelectMany(static m => m.Assignments)
            .Select(static a => new ControlMatcher(
                    a.Input.Port,
                    a.Input.Kind == ControlInputKind.Note ? 0x90 : 0xB0,
                    a.Input.Channel is { } c ? c - 1 : -1,
                    a.Input.Number,
                    MatcherFamily.Single
                )
            )
    ];

    public IReadOnlyList<TargetChange> Receive(string port, ReadOnlySpan<byte> message, ITargetValues values) {
        if (Read(port, message) is not var (input, raw)) {
            return [];
        }

        var active = Assignments.Zones.SelectMany(static z => z.Modes.Where(m => m.Id == z.ActiveModeId))
            .SelectMany(static m => m.Assignments);
        var assignment = active.FirstOrDefault(a => a.Input.Kind == input.Kind
            && a.Input.Number == input.Number
            && (a.Input.Port is null || a.Input.Port == port)
            && (a.Input.Channel is null || a.Input.Channel == input.Channel)
        );
        if (assignment is null) {
            return [];
        }

        var range = assignment.Max - assignment.Min;
        double value;
        switch (assignment.Mode) {
            case ControlValueMode.Absolute:
                value = assignment.Min + raw * range;
                if (assignment.Pickup && !PickedUp(assignment, value, values.Value(assignment.Target))) {
                    return [];
                }

                break;
            case ControlValueMode.RelativeTwosComplement:
                var step = (int)Math.Round(raw * 127);
                step = step < 64 ? step : step - 128;
                value = Math.Clamp((values.Value(assignment.Target) ?? 0) + step / 128.0 * range, 0, 1);
                break;
            case ControlValueMode.Toggle:
                if (raw < 0.5) {
                    return [];
                }

                value = (values.Value(assignment.Target) ?? 0) >= 0.5 ? assignment.Min : assignment.Max;
                break;
            default:
                value = raw >= 0.5 ? assignment.Max : assignment.Min;
                break;
        }

        if (assignment.Target is { Kind: ControlTargetKind.ZoneMode, ZoneId: { } zoneId, ModeId: { } modeId }
            && value >= 0.5) {
            Assignments = new(
                [
                    ..Assignments.Zones.Select(z => z.Id == zoneId ? z with { ActiveModeId = modeId } : z)
                ]
            );
        }

        return [new(assignment.Id, assignment.Target, value)];
    }

    /// <summary>Pickup: held until the control's value crosses (or meets) the target's.</summary>
    bool PickedUp(ControlAssignment assignment, double value, double? current) {
        if (current is not { } target) {
            return false;
        }

        var (picked, last) = pickup.GetValueOrDefault(assignment.Id);
        if (!picked) {
            picked = Math.Abs(value - target) <= 1.0 / 127
                || (last is { } before && Math.Sign(before - target) != Math.Sign(value - target));
        }

        pickup[assignment.Id] = (picked, value);
        return picked;
    }

    public ControlInput? Learn(string port, ReadOnlySpan<byte> message) =>
        Read(port, message) is var (input, _) ? input : null;

    public void Reset() => pickup.Clear();

    static (ControlInput Input, double Raw)? Read(string port, ReadOnlySpan<byte> message) {
        if (message.Length < 3) {
            return null;
        }

        var channel = (message[0] & 0x0F) + 1;
        return (message[0] & 0xF0) switch {
            0xB0 => (new(port, ControlInputKind.ControlChange, channel, message[1]), message[2] / 127.0),
            0x90 => (new(port, ControlInputKind.Note, channel, message[1]), message[2] / 127.0),
            0x80 => (new(port, ControlInputKind.Note, channel, message[1]), 0),
            _ => null
        };
    }
}

/// <summary>
///     A made-up surface protocol: pitch bend on channel n is strip n's fader (14-bit), note 0x68 + n its touch, note
///     0x10 + n its mute, notes 0x5E/0x5D/0x5F play, stop and record, note 0x2F banks right by its strips, note 0x00 with
///     velocity 0x7F answers its ping. Feedback: a strip's fader as pitch bend, its name as a SysEx, a meter as channel
///     pressure; every call is counted.
/// </summary>
sealed class TestSurface(ControlSurfaceKind kind) : IControlSurface {
    readonly Dictionary<int, (string? Id, double Fader, string Name)> sent = [];
    readonly HashSet<int> touched = [];
    IReadOnlyList<string> strips = [];
    int encodes;
    int meterCalls;
    int polls;
    int invalidations;
    bool connected;

    public ConcurrentQueue<long> EncodeTimes { get; } = new();
    public ConcurrentQueue<long> MeterTimes { get; } = new();

    public int Encodes => Volatile.Read(ref encodes);
    public int MeterCalls => Volatile.Read(ref meterCalls);
    public int Polls => Volatile.Read(ref polls);
    public int Invalidations => Volatile.Read(ref invalidations);

    public ControlSurfaceKind Kind { get; } = kind;

    public int Strips => 8;

    public int BankOffset { get; set; }

    public bool Connected => connected;

    public IReadOnlyList<SurfaceAction> Decode(ReadOnlySpan<byte> message, long timeNs) {
        connected = true;
        var status = message[0] & 0xF0;
        var channel = message[0] & 0x0F;
        if (status == 0xE0 && message.Length >= 3) {
            var value = (message[1] | message[2] << 7) / 16383.0;
            return [new(SurfaceActionKind.SetParameter, channel, Track(channel), "volume", value)];
        }

        if (status != 0x90 || message.Length < 3) {
            return [];
        }

        var (note, velocity) = (message[1], message[2]);
        switch (note) {
            case >= 0x68 and < 0x70:
                var strip = note - 0x68;
                if (velocity > 0) {
                    touched.Add(strip);
                } else {
                    touched.Remove(strip);
                }

                return [new(SurfaceActionKind.Touch, strip, Track(strip), "volume", velocity > 0 ? 1 : 0)];
            case >= 0x10 and < 0x18 when velocity > 0:
                return [new(SurfaceActionKind.Mute, note - 0x10, Track(note - 0x10))];
            case 0x5E when velocity > 0:
                return [new(SurfaceActionKind.Play)];
            case 0x5D when velocity > 0:
                return [new(SurfaceActionKind.Stop)];
            case 0x5F when velocity > 0:
                return [new(SurfaceActionKind.Record)];
            case 0x2F when velocity > 0:
                return [new(SurfaceActionKind.Banked, Value: BankOffset + Strips)];
            default:
                return [];
        }
    }

    string? Track(int strip) => strip < strips.Count ? strips[strip] : null;

    public IReadOnlyList<byte[]> Encode(ProjectView view) {
        Interlocked.Increment(ref encodes);
        EncodeTimes.Enqueue(Environment.TickCount64);
        var messages = new List<byte[]>();
        var shown = view.Tracks.Skip(BankOffset).Take(Strips).ToArray();
        strips = [..shown.Select(static t => t.Id)];
        for (var i = 0; i < Strips; i++) {
            var track = i < shown.Length ? shown[i] : null;
            var now = (track?.Id, track?.Fader ?? 0, track?.Name ?? string.Empty);
            if (sent.TryGetValue(i, out var was) && was == now) {
                continue;
            }

            if (!touched.Contains(i)) {
                var position = (int)Math.Round(now.Item2 * 16383);
                messages.Add([(byte)(0xE0 | i), (byte)(position & 0x7F), (byte)(position >> 7)]);
            }

            if (!sent.TryGetValue(i, out was) || was.Name != now.Item3) {
                messages.Add([0xF0, 0x7D, (byte)i, ..Encoding.ASCII.GetBytes(now.Item3), 0xF7]);
            }

            sent[i] = now;
        }

        return messages;
    }

    public IReadOnlyList<byte[]> EncodeMeters(ProjectView view) {
        Interlocked.Increment(ref meterCalls);
        MeterTimes.Enqueue(Environment.TickCount64);
        return [
            ..view.Tracks.Skip(BankOffset)
                .Take(Strips)
                .Select(static (t, i) => new byte[] { 0xD0, (byte)(i << 4 | (int)Math.Round(t.Meter * 12)) })
        ];
    }

    public IReadOnlyList<byte[]> Poll(long timeNs) {
        Interlocked.Increment(ref polls);
        return [[0x90, 0x00, 0x00]];
    }

    public void Invalidate() {
        Interlocked.Increment(ref invalidations);
        sent.Clear();
    }
}

/// <summary>
///     OSC 1.0 messages (strings, ints, floats) and a few of Anode's addresses: <c>/anode/transport/play</c>,
///     <c>/anode/transport/stop</c>, <c>/anode/track/{n}/volume</c>, <c>/anode/track/{n}/mute</c>,
///     <c>/anode/subscribe</c> and <c>/anode/unsubscribe</c>; feedback <c>/anode/track/{n}/volume</c>.
/// </summary>
static class TestOsc {
    public static byte[] Encode(OscMessage message) {
        var bytes = new List<byte>();
        Pad(bytes, Encoding.UTF8.GetBytes(message.Address));
        var tags = ","
            + string.Concat(
                message.Arguments.Select(static a => a switch {
                        OscInt => "i",
                        OscFloat => "f",
                        _ => "s"
                    }
                )
            );
        Pad(bytes, Encoding.ASCII.GetBytes(tags));
        foreach (var argument in message.Arguments) {
            var word = new byte[4];
            switch (argument) {
                case OscInt i:
                    BinaryPrimitives.WriteInt32BigEndian(word, i.Value);
                    bytes.AddRange(word);
                    break;
                case OscFloat f:
                    BinaryPrimitives.WriteSingleBigEndian(word, f.Value);
                    bytes.AddRange(word);
                    break;
                case OscString s:
                    Pad(bytes, Encoding.UTF8.GetBytes(s.Value));
                    break;
            }
        }

        return [..bytes];

        static void Pad(List<byte> bytes, byte[] text) {
            bytes.AddRange(text);
            bytes.Add(0);
            while (bytes.Count % 4 != 0) {
                bytes.Add(0);
            }
        }
    }

    public static OscMessage Decode(ReadOnlySpan<byte> data) {
        var offset = 0;
        var address = Text(data, ref offset);
        var tags = Text(data, ref offset);
        if (!address.StartsWith('/') || !tags.StartsWith(',')) {
            throw new FormatException("Not an OSC message.");
        }

        var arguments = new List<OscArgument>();
        foreach (var tag in tags[1..]) {
            switch (tag) {
                case 'i':
                    arguments.Add(new OscInt(BinaryPrimitives.ReadInt32BigEndian(data[offset..])));
                    offset += 4;
                    break;
                case 'f':
                    arguments.Add(new OscFloat(BinaryPrimitives.ReadSingleBigEndian(data[offset..])));
                    offset += 4;
                    break;
                case 's':
                    arguments.Add(new OscString(Text(data, ref offset)));
                    break;
                default:
                    throw new FormatException($"Type tag {tag}.");
            }
        }

        return new(address, arguments);

        static string Text(ReadOnlySpan<byte> data, ref int offset) {
            var end = data[offset..].IndexOf((byte)0);
            if (end < 0) {
                throw new FormatException("An unterminated string.");
            }

            var text = Encoding.UTF8.GetString(data.Slice(offset, end));
            offset += (end + 4) & ~3;
            return text;
        }
    }

    public static OscRequest? Resolve(OscMessage message, ProjectView view) {
        var value = (message.Arguments.Count > 0 ? message.Arguments[0] : null) switch {
            OscFloat f => f.Value,
            OscInt i => i.Value,
            _ => 1d
        };
        var parts = message.Address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts) {
            case ["anode", "transport", "play"]:
                return new(
                    OscRequestKind.Set,
                    new(ControlTargetKind.Transport, Transport: ControlTransportAction.Play),
                    1
                );
            case ["anode", "transport", "stop"]:
                return new(
                    OscRequestKind.Set,
                    new(ControlTargetKind.Transport, Transport: ControlTransportAction.Stop),
                    1
                );
            case ["anode", "subscribe"]:
                return new(OscRequestKind.Subscribe, Number: (int)value);
            case ["anode", "unsubscribe"]:
                return new(OscRequestKind.Unsubscribe, Number: (int)value);
            case ["anode", "track", var n, var control]
                when int.TryParse(n, out var index) && index >= 1 && index <= view.Tracks.Count:
                var trackId = view.Tracks[index - 1].Id;
                return control switch {
                    "volume" => new(OscRequestKind.Set, new(ControlTargetKind.Parameter, trackId, "volume"), value),
                    "mute" => new(
                        OscRequestKind.Set,
                        new(ControlTargetKind.TrackSwitch, trackId, Switch: TrackSwitchAction.Mute),
                        value
                    ),
                    _ => null
                };
            default:
                return null;
        }
    }

    public static IReadOnlyList<OscMessage> Feedback(ProjectView view) => [
        ..view.Tracks.Select(static (t, i) => new OscMessage(
                $"/anode/track/{i + 1}/volume",
                [new OscFloat((float)t.Fader)]
            )
        )
    ];
}

/// <summary>
///     A separator that "separates" by scaling: stem k of the model's order is the input times (k + 1) / 10, so each
///     stem file is recognisable. It can wait to be cancelled, and reports progress.
/// </summary>
sealed class TestSeparator(StemModel model, bool waitForCancel = false) : IStemSeparator {
    public StemModel Model { get; } = model;

    public IReadOnlyList<StemKind> Stems { get; } = model == StemModel.SixStems
        ? [StemKind.Vocals, StemKind.Drums, StemKind.Bass, StemKind.Other, StemKind.Guitar, StemKind.Piano]
        : [StemKind.Vocals, StemKind.Drums, StemKind.Bass, StemKind.Other];

    public int SampleRate => 44_100;

    public bool Disposed { get; private set; }

    public static float Scale(StemKind stem) => ((int)stem + 1) / 10f;

    public IReadOnlyDictionary<StemKind, float[][]> Separate(
        float[][] channels,
        int sampleRate,
        IReadOnlyList<StemKind> stems,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        progress?.Report(0.5);
        if (waitForCancel) {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            cancellationToken.ThrowIfCancellationRequested();
        }

        progress?.Report(1);
        return stems.ToDictionary(
            static s => s,
            s => channels.Select(c => c.Select(x => x * Scale(s)).ToArray()).ToArray()
        );
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Opens <see cref="TestSeparator" />s, reporting a "download" first.</summary>
sealed class TestStemModels(bool waitForCancel = false) : Projects.IStemModels {
    public ConcurrentQueue<StemModel> Opened { get; } = new();

    public Task<IStemSeparator> OpenAsync(
        StemModel model,
        IProgress<double> progress,
        CancellationToken cancellationToken
    ) {
        Opened.Enqueue(model);
        progress.Report(0.5);
        progress.Report(1);
        return Task.FromResult<IStemSeparator>(new TestSeparator(model, waitForCancel));
    }
}
