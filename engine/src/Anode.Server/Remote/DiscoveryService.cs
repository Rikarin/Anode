using Anode.Contracts;
using Anode.Server.Control;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using System.Net;

namespace Anode.Server.Remote;

/// <summary>
///     How remotes find this Server (docs/11-phase-10.md, "Discovery"): <see cref="Info" /> for
///     <c>GET /system/discovery</c>, and a DNS-SD advertisement of <c>_anode._tcp</c> (<see cref="Discovery" />) through
///     Phase 8's <see cref="Bonjour" /> registration (macOS), with the TXT keys <c>version</c>, <c>project</c>,
///     <c>pairing</c> and <c>path</c>. It is advertised once the Server listens (on its first address's port), and again
///     when the open project changes name; not at all when <c>Anode:Discovery</c> is false or the Server has no address
///     (a test server).
/// </summary>
public sealed class DiscoveryService(
    IServer server,
    IHostApplicationLifetime lifetime,
    ProjectSession session,
    IOptions<ServerOptions> options,
    IConfiguration configuration,
    ILogger<DiscoveryService> logger
) : IHostedService, IDisposable {
    readonly Lock gate = new();
    IDisposable? advertised;
    string? advertisedProject;
    bool started;

    /// <summary>The Server's name: the machine's.</summary>
    public static string Name => Environment.MachineName;

    public DiscoveryInfo Info() {
        var (host, port) = Address();
        return new(
            Name,
            host,
            port,
            session.View?.State.Name,
            options.Value.Token is not null,
            EngineConnection.Version
        );
    }

    /// <summary>The TXT record's entries for <paramref name="info" />.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Txt(DiscoveryInfo info) {
        List<KeyValuePair<string, string>> txt = [new(Discovery.TxtVersion, info.Version)];
        if (info.ProjectName is { } project) {
            txt.Add(new(Discovery.TxtProject, project));
        }

        txt.Add(new(Discovery.TxtPairing, info.PairingRequired ? "1" : "0"));
        txt.Add(new(Discovery.TxtPath, "/api/v1"));
        return txt;
    }

    /// <summary>
    ///     The host and port remotes reach this Server on: the first listening address a remote can reach (not loopback:
    ///     the desktop app's Server listens on a loopback port for its own editor and on the network port for remotes),
    ///     else the first; its host name (with <c>.local</c>) for an address on every interface; port 0 when it does not
    ///     listen on a port.
    /// </summary>
    (string Host, int Port) Address() {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        (string Host, int Port)? first = null;
        foreach (var address in addresses) {
            if (Reach(address) is not var (host, port, loopback)) {
                continue;
            }

            if (!loopback) {
                return (host, port);
            }

            first ??= (host, port);
        }

        return first ?? ("localhost", 0);
    }

    /// <summary>
    ///     A listening address as remotes would use it: its host (this machine's name with <c>.local</c> for every
    ///     interface), port, and whether it is loopback; null when it is not a URL.
    /// </summary>
    static (string Host, int Port, bool Loopback)? Reach(string address) {
        if (!Uri.TryCreate(
                address.Replace("*", "0.0.0.0", StringComparison.Ordinal)
                    .Replace("+", "0.0.0.0", StringComparison.Ordinal),
                UriKind.Absolute,
                out var uri
            )) {
            return null;
        }

        var host = uri.Host;
        var ip = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : null;
        if (ip is not null && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))) {
            host = Dns.GetHostName();
            if (!host.Contains('.', StringComparison.Ordinal)) {
                host += ".local";
            }
        }

        return (host, uri.Port, host == "localhost" || (ip is not null && IPAddress.IsLoopback(ip)));
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!bool.TryParse(configuration["Anode:Discovery"], out var on) || on) {
            lifetime.ApplicationStarted.Register(() => {
                    lock (gate) {
                        started = true;
                    }

                    Advertise();
                }
            );
            session.Changed += Advertise;
        }

        return Task.CompletedTask;
    }

    /// <summary>Advertises (again, when the project's name changed since).</summary>
    void Advertise() {
        var info = Info();
        lock (gate) {
            if (!started || info.Port == 0 || (advertised is not null && advertisedProject == info.ProjectName)) {
                return;
            }

            advertised?.Dispose();
            advertisedProject = info.ProjectName;
            advertised = Bonjour.Advertise(info.Name, Discovery.ServiceType, info.Port, logger, Txt(info));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() {
        session.Changed -= Advertise;
        lock (gate) {
            advertised?.Dispose();
            advertised = null;
            started = false;
        }
    }
}
