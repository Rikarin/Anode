using System.Net;
using System.Runtime.InteropServices;

namespace Anode.Server.Control;

/// <summary>
///     Bonjour service registration on macOS, through the system's DNS-SD API (<c>DNSServiceRegister</c> in libSystem,
///     talking to mDNSResponder): no child process to keep alive, and the registration goes when its reference is
///     deallocated or the process ends. No callback is passed (the API allows it): a failed registration only means the
///     service is not advertised, which clients survive (the user types the address). Elsewhere it does nothing.
/// </summary>
public static partial class Bonjour {
    const string LibSystem = "/usr/lib/libSystem.dylib";

    /// <summary>
    ///     Advertises <paramref name="name" /> as a service of <paramref name="type" /> (<c>_osc._udp</c>) on
    ///     <paramref name="port" /> in the local domain; null where Bonjour is unavailable or the registration fails
    ///     (logged). Disposing the result withdraws it.
    /// </summary>
    public static IDisposable? Advertise(string name, string type, int port, ILogger logger) {
        if (!OperatingSystem.IsMacOS()) {
            return null;
        }

        try {
            var error = DNSServiceRegister(
                out var reference,
                0,
                0,
                name,
                type,
                null,
                null,
                (ushort)IPAddress.HostToNetworkOrder((short)port),
                0,
                0,
                0,
                0
            );
            if (error != 0 || reference == 0) {
                ControlLog.BonjourRefused(logger, error);
                return null;
            }

            return new Registration(reference);
        } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
            ControlLog.BonjourFailed(logger, e.Message);
            return null;
        }
    }

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DNSServiceRegister(
        out nint reference,
        uint flags,
        uint interfaceIndex,
        string? name,
        string type,
        string? domain,
        string? host,
        ushort port,
        ushort txtLength,
        nint txtRecord,
        nint callback,
        nint context
    );

    [LibraryImport(LibSystem)]
    private static partial void DNSServiceRefDeallocate(nint reference);

    sealed class Registration(nint reference) : IDisposable {
        nint handle = reference;

        public void Dispose() {
            var h = Interlocked.Exchange(ref handle, 0);
            if (h != 0) {
                DNSServiceRefDeallocate(h);
            }
        }
    }
}
