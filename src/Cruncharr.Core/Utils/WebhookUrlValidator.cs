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

        // Resolve DNS to check for rebinding attacks
        try
        {
            var hostEntry = Dns.GetHostEntry(host);
            foreach (var resolvedIp in hostEntry.AddressList)
            {
                var bytes = resolvedIp.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    if (bytes[0] == 10 ||
                        (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                        (bytes[0] == 192 && bytes[1] == 168) ||
                        (bytes[0] == 169 && bytes[1] == 254) ||
                        bytes[0] == 127 ||
                        bytes[0] == 0)
                    {
                        error = "Private IP addresses are not allowed";
                        return false;
                    }
                }
                if (resolvedIp.AddressFamily == AddressFamily.InterNetworkV6 &&
                    resolvedIp.Equals(IPAddress.IPv6Loopback))
                {
                    error = "Loopback addresses are not allowed";
                    return false;
                }
            }
        }
        catch
        {
            error = "Unable to resolve hostname";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254))
            {
                error = "Private IP addresses are not allowed";
                return false;
            }
        }

        return true;
    }
}
