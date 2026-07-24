using System.Net;
using System.Net.Sockets;

namespace Cruncharr.Core.Utils;

public static class WebhookUrlValidator
{
    public static SocketsHttpHandler CreateHttpMessageHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectToValidatedHostAsync
        };
    }

    public static bool IsValidWebhookUrl(string url, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Webhook URL is required";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "Invalid URL format";
            return false;
        }

        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            error = "Only HTTP and HTTPS URLs are allowed";
            return false;
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host == "127.0.0.1" ||
            host == "::1" ||
            host == "0.0.0.0")
        {
            error = "Localhost URLs are not allowed";
            return false;
        }

        if (host.StartsWith("127."))
        {
            error = "Loopback addresses are not allowed";
            return false;
        }

        // If the host is a literal IP, validate it directly.
        if (IPAddress.TryParse(host, out var literalIp))
        {
            if (IsBlockedIp(literalIp))
            {
                error = "Private, loopback, or link-local IP addresses are not allowed";
                return false;
            }
            return true;
        }

        // Otherwise resolve DNS and reject if ANY resolved address is internal
        // (also catches DNS-rebinding to a private range at validation time).
        try
        {
            var hostEntry = Dns.GetHostEntry(host);
            foreach (var resolvedIp in hostEntry.AddressList)
            {
                if (IsBlockedIp(resolvedIp))
                {
                    error = "Private, loopback, or link-local IP addresses are not allowed";
                    return false;
                }
            }
        }
        catch
        {
            error = "Unable to resolve hostname";
            return false;
        }

        return true;
    }

    private static async ValueTask<Stream> ConnectToValidatedHostAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken);

        if (addresses.Length == 0 || addresses.Any(IsBlockedIp))
        {
            throw new HttpRequestException(
                "Webhook hostname resolved to a private, loopback, link-local, or otherwise blocked address.");
        }

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                lastError = ex;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            "Unable to connect to the validated webhook host.",
            lastError);
    }

    /// <summary>
    /// True if the address is loopback, private, link-local, or otherwise not a
    /// routable public address (covers both IPv4 and IPv6, including IPv4-mapped IPv6).
    /// </summary>
    private static bool IsBlockedIp(IPAddress ip)
    {
        // Normalize IPv4-mapped IPv6 (::ffff:10.0.0.1) down to IPv4.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip)) return true; // 127.0.0.0/8 and ::1

        var bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork && bytes.Length == 4)
        {
            return bytes[0] == 10 ||                                   // 10.0.0.0/8
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16.0.0/12
                   (bytes[0] == 192 && bytes[1] == 168) ||            // 192.168.0.0/16
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) || // 192.0.0.0/24 IETF protocol assignments
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) || // 192.0.2.0/24 documentation
                   (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) || // 192.88.99.0/24 deprecated relay
                   (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19) || // 198.18.0.0/15 benchmarking
                   (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) || // 198.51.100.0/24 documentation
                   (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) || // 203.0.113.0/24 documentation
                   (bytes[0] == 169 && bytes[1] == 254) ||            // 169.254.0.0/16 link-local
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) || // 100.64.0.0/10 CGNAT
                   bytes[0] == 0 ||                                   // 0.0.0.0/8
                   bytes[0] >= 224;                                   // multicast, reserved, broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            if (ip.Equals(IPAddress.IPv6None)) return true;            // ::
            if (ip.IsIPv6LinkLocal) return true;                      // fe80::/10
            if (ip.IsIPv6SiteLocal) return true;                      // fec0::/10 deprecated site-local
            if ((bytes[0] & 0xFE) == 0xFC) return true;               // fc00::/7 unique local
            if (bytes[0] == 0xFF) return true;                        // ff00::/8 multicast
            if (bytes[0] == 0x20 && bytes[1] == 0x01 &&
                bytes[2] == 0x0D && bytes[3] == 0xB8) return true;     // 2001:db8::/32 documentation
        }

        return false;
    }
}
