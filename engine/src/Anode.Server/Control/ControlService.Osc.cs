using Anode.Contracts;
using Anode.Control.Osc;
using Anode.Control.Surfaces;
using Anode.Server.Projects;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Anode.Server.Control;

/// <summary>
///     The OSC server (docs/09-phase-8.md, "OSC"; the address space is workstream D's, <c>docs/control/osc.md</c>): each
///     datagram is decoded (bundles are carried out at once, their time tags ignored), resolved against the project view
///     and carried out as a control move would be. Feedback goes to the configured clients and this session's
///     subscribers: an address whose value changed since it was last sent, at most once a tick (30 a second).
/// </summary>
public sealed partial class ControlService {
    /// <summary>The name the OSC server is advertised under over Bonjour.</summary>
    public const string OscServiceName = "Anode";

    /// <summary>The most clients that subscribe in a session.</summary>
    public const int MaxSubscribers = 64;

    readonly HashSet<IPEndPoint> subscribed = [];
    readonly Dictionary<string, byte[]> oscSent = new(StringComparer.Ordinal);
    OscSettings osc = OscSettings.Default;
    OscServer? oscServer;
    IReadOnlyList<IPEndPoint> oscClients = [];

    bool OscWanted => oscServer is not null && oscClients.Count + subscribed.Count > 0;

    public OscStatus Osc() {
        lock (gate) {
            return OscStatusLocked();
        }
    }

    OscStatus OscStatusLocked() =>
        new(
            osc,
            oscServer is not null,
            Addresses(),
            [..subscribed.Select(static e => new OscClient(e.Address.ToString(), e.Port))]
        );

    /// <summary>Changes the OSC settings (checked: 422), saves them, and (re)starts or stops the server.</summary>
    public OscStatus SetOsc(OscSettings? replacement) {
        var next = ControlValidation.Check(replacement);
        var clients = Resolve(next.Clients);
        settings.SaveOscSettings(next);
        lock (gate) {
            var restart = next.Enabled != osc.Enabled || next.Port != osc.Port || (next.Enabled && oscServer is null);
            osc = next;
            oscClients = clients;
            if (restart) {
                StopOsc();
                StartOsc(next, clients);
            }

            return OscStatusLocked();
        }
    }

    void StartOsc(OscSettings next, IReadOnlyList<IPEndPoint>? clients = null) {
        osc = next;
        oscClients = clients ?? Resolve(next.Clients);
        if (!next.Enabled) {
            return;
        }

        try {
            oscServer = OscServer.Start(next.Port, OnOsc, OscServiceName, logger);
        } catch (SocketException e) {
            ControlLog.OscFailed(logger, next.Port, e.Message);
        }
    }

    void StopOsc() {
        oscServer?.Dispose();
        oscServer = null;
        subscribed.Clear();
        oscSent.Clear();
    }

    /// <summary>The clients' endpoints (a name that does not resolve is left out until the settings change).</summary>
    List<IPEndPoint> Resolve(IReadOnlyList<OscClient> clients) {
        var endpoints = new List<IPEndPoint>();
        foreach (var client in clients) {
            try {
                var address = IPAddress.TryParse(client.Host, out var parsed)
                    ? parsed
                    : Dns.GetHostAddresses(client.Host)
                        .OrderBy(static a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                        .First();
                endpoints.Add(new(address, client.Port));
            } catch (Exception e) when (e is SocketException or ArgumentException or InvalidOperationException) {
                ControlLog.OscClientUnresolved(logger, client.Host, e.Message);
            }
        }

        return endpoints;
    }

    /// <summary>This machine's addresses, for the user to type into a client: IPv4 first, without loopback.</summary>
    static IReadOnlyList<string> Addresses() {
        try {
            return [
                ..NetworkInterface.GetAllNetworkInterfaces()
                    .Where(static n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    )
                    .SelectMany(static n => n.GetIPProperties().UnicastAddresses)
                    .Select(static u => u.Address)
                    .Where(static a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
                    .OrderBy(static a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .Select(static a => a.ToString())
                    .Distinct()
            ];
        } catch (NetworkInformationException) {
            return [];
        }
    }

    void OnOsc(byte[] data, IPEndPoint from) {
        lock (gate) {
            if (oscServer is null) {
                return;
            }

            OscPacket packet;
            try {
                packet = protocols.DecodeOsc(data);
            } catch (Exception e) when (e is FormatException or NotImplementedException or ArgumentException) {
                if (logger.IsEnabled(LogLevel.Debug)) {
                    ControlLog.OscIgnored(logger, from, e.Message);
                }

                return;
            }

            try {
                foreach (var message in Messages(packet)) {
                    Carry(message, from);
                }
            } catch (Exception e) when (e is not OutOfMemoryException) {
                ControlLog.MessageFailed(logger, from.ToString(), e);
            }
        }
    }

    static IEnumerable<OscMessage> Messages(OscPacket packet) =>
        packet switch {
            OscMessage message => [message],
            OscBundle bundle => bundle.Elements.SelectMany(Messages),
            _ => []
        };

    /// <summary>Carries out one OSC message from <paramref name="from" />.</summary>
    void Carry(OscMessage message, IPEndPoint from) {
        var open = session.View;
        if (protocols.ResolveOsc(message, View(open)) is not { } request) {
            return;
        }

        if (request.Kind is OscRequestKind.Subscribe or OscRequestKind.Unsubscribe) {
            Subscribe(request, from, open);
        } else if (open is not null) {
            Carry(request, from, open);
        }
    }

    /// <summary>A subscription from the sender's host on the port the request names (a new subscriber gets everything).</summary>
    void Subscribe(OscRequest request, IPEndPoint from, OpenProjectView? open) {
        if (request.Number is < 1 or > 65_535) {
            return;
        }

        var endpoint = new IPEndPoint(from.Address, request.Number);
        if (request.Kind == OscRequestKind.Unsubscribe) {
            subscribed.Remove(endpoint);
        } else if (subscribed.Count < MaxSubscribers && subscribed.Add(endpoint)) {
            SendAll(endpoint, View(open));
        }
    }

    void Carry(OscRequest request, IPEndPoint from, OpenProjectView open) {
        var target = request.Target;
        var track = target is null ? null : TrackOf(open, target.TrackId);
        switch (request.Kind) {
            case OscRequestKind.Set when target is not null:
                Set("o:" + from, open, target, request.Value);
                break;
            case OscRequestKind.SetDb when track is not null && target?.Address is { } address:
                SetParameter("o:" + from, open, track, address, Taper.Position(request.Value));
                break;
            case OscRequestKind.SetName when track is not null && request.Text is { } name:
                Edit(new SetTrackCommand(track.Id, name));
                break;
            case OscRequestKind.GoToMarker:
                var markers = open.State.Markers.Values.OrderBy(static m => m.Tick)
                    .ThenBy(static m => m.Id, StringComparer.Ordinal)
                    .ToArray();
                if (request.Number >= 1 && request.Number <= markers.Length) {
                    Control(new(TransportCommand.Locate, markers[request.Number - 1].Tick));
                }

                break;
            default:
                // a request without what it needs (a track that is gone)
                break;
        }
    }

    /// <summary>An OSC value for a target: as an assignment's change, but switches and zone modes are set directly.</summary>
    void Set(string source, OpenProjectView open, ControlTarget target, double value) {
        switch (target.Kind) {
            case ControlTargetKind.Parameter or ControlTargetKind.SmartControl:
                if (TrackOf(open, target.TrackId) is { } track && target.Address is { } address) {
                    SetParameter(
                        source,
                        open,
                        track,
                        target.Kind == ControlTargetKind.SmartControl ? "smartControl:" + address : address,
                        value
                    );
                }

                break;
            case ControlTargetKind.TrackSwitch:
                Switch(open, target.TrackId, target.Switch!.Value, value);
                break;
            case ControlTargetKind.Transport:
                Transport(open, target.Transport!.Value, value);
                break;
            case ControlTargetKind.KeyCommand when value >= 0.5:
                RunKeyCommand(open, target.KeyCommandId!);
                break;
            case ControlTargetKind.ZoneMode when value >= 0.5:
                ActivateMode(target.ZoneId, target.ModeId);
                break;
        }
    }

    /// <summary>Makes a mode its zone's active one (OSC; assignments switch modes in their engine).</summary>
    void ActivateMode(string? zoneId, string? modeId) {
        var zones = assignments.Zones.ToList();
        var index = zones.FindIndex(z => z.Id == zoneId);
        if (index < 0 || zones[index].Modes.All(m => m.Id != modeId) || zones[index].ActiveModeId == modeId) {
            return;
        }

        zones[index] = zones[index] with { ActiveModeId = modeId! };
        var next = new ControllerAssignments(zones);
        var (made, error) = Engine(next);
        if (error is null) {
            Replace(next, made);
        }
    }

    /// <summary>Sends every address whose value changed to the clients and subscribers.</summary>
    void OscFeedback(ProjectView view) {
        if (!OscWanted) {
            return;
        }

        IReadOnlyList<OscMessage> messages;
        try {
            messages = protocols.OscFeedback(view);
        } catch (NotImplementedException) {
            return;
        }

        foreach (var message in messages) {
            var data = protocols.EncodeOsc(message);
            if (oscSent.TryGetValue(message.Address, out var last) && last.AsSpan().SequenceEqual(data)) {
                continue;
            }

            oscSent[message.Address] = data;
            foreach (var to in oscClients.Concat(subscribed)) {
                oscServer!.Send(data, to);
            }
        }
    }

    /// <summary>Every feedback message, to one new subscriber.</summary>
    void SendAll(IPEndPoint to, ProjectView view) {
        try {
            foreach (var message in protocols.OscFeedback(view)) {
                oscServer!.Send(protocols.EncodeOsc(message), to);
            }
        } catch (NotImplementedException) {
            // the stubs, until workstream D
        }
    }
}

static partial class ControlLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "The OSC server could not listen on port {Port}: {Message}")]
    public static partial void OscFailed(ILogger logger, int port, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "OSC client {Host} does not resolve: {Message}")]
    public static partial void OscClientUnresolved(ILogger logger, string host, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored an OSC datagram from {From}: {Message}")]
    public static partial void OscIgnored(ILogger logger, IPEndPoint from, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "The OSC server is not advertised over Bonjour: {Message}")]
    public static partial void BonjourFailed(ILogger logger, string message);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The OSC server is not advertised over Bonjour: DNSServiceRegister answered {Error}"
    )]
    public static partial void BonjourRefused(ILogger logger, int error);
}
