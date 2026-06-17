using System.Net;
using System.Net.Sockets;

namespace Cruncharr.Core.Utils;

public static class WebhookUrlValidator
{
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
                   (bytes[0] == 169 && bytes[1] == 254) ||            // 169.254.0.0/16 link-local
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) || // 100.64.0.0/10 CGNAT
                   bytes[0] == 0;                                      // 0.0.0.0/8
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            if (ip.IsIPv6LinkLocal) return true;                      // fe80::/10
            if ((bytes[0] & 0xFE) == 0xFC) return true;               // fc00::/7 unique local
            if (bytes[0] == 0xFF) return true;                        // ff00::/8 multicast
        }

        return false;
    }
}
