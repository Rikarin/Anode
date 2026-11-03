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
    ///     Advertises <paramref name="name" /> as a service of <paramref name="type" /> (<c>_osc._udp</c>,
    ///     <c>_anode._tcp</c>) on <paramref name="port" /> in the local domain, with the TXT record
    ///     <paramref name="txt" /> (Phase 10; none when null); null where Bonjour is unavailable or the registration
    ///     fails (logged). Disposing the result withdraws it.
    /// </summary>
    public static IDisposable? Advertise(
        string name,
        string type,
        int port,
        ILogger logger,
        IReadOnlyList<KeyValuePair<string, string>>? txt = null
    ) {
        if (!OperatingSystem.IsMacOS()) {
            return null;
        }

        var record = Txt(txt ?? []);
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
                (ushort)record.Length,
                record,
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
        byte[] txtRecord,
        nint callback,
        nint context
    );

    [LibraryImport(LibSystem)]
    private static partial void DNSServiceRefDeallocate(nint reference);

    /// <summary>
    ///     A TXT record (RFC 6763 §6): each <c>key=value</c> as a length byte and its UTF-8, at most 255 bytes each (longer
    ///     values are cut), the record at most 8900 bytes; a lone zero byte when empty.
    /// </summary>
    public static byte[] Txt(IReadOnlyList<KeyValuePair<string, string>> entries) {
        var bytes = new List<byte>();
        foreach (var (key, value) in entries) {
            var entry = System.Text.Encoding.UTF8.GetBytes($"{key}={value}");
            var length = Math.Min(entry.Length, 255);
            if (bytes.Count + 1 + length > 8900) {
                break;
            }

            bytes.Add((byte)length);
            bytes.AddRange(entry.AsSpan(0, length));
        }

        return bytes.Count == 0 ? [0] : [..bytes];
    }

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
