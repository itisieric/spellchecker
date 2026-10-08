using System.Net;

namespace LLMAutocorrect.Configuration;

public static class ProviderEndpointPolicy
{
    public static bool TryValidate(string value, out Uri endpoint, out string error)
    {
        endpoint = null!;
        error = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter a complete HTTP or HTTPS address, including the port when needed.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Do not put a user name or password in the provider address.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "The provider address cannot contain a query string or fragment.";
            return false;
        }
        endpoint = parsed;
        return true;
    }

    public static bool IsSameComputer(Uri endpoint)
    {
        if (endpoint.IsLoopback || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);
    }

    public static bool IsPrivateNetwork(Uri endpoint)
    {
        if (IsSameComputer(endpoint)) return true;
        if (!IPAddress.TryParse(endpoint.Host, out var address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        return address.IsIPv6LinkLocal || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    public static string? SecurityWarning(Uri endpoint, ProviderAuthentication authentication)
    {
        if (endpoint.Scheme == Uri.UriSchemeHttps) return null;
        if (IsSameComputer(endpoint)) return null;
        if (authentication == ProviderAuthentication.None && IsPrivateNetwork(endpoint))
            return "This LAN connection is not encrypted. Text sent for correction can be read on the network.";
        return "This connection is not encrypted. Text and API tokens could be read on the network. Use HTTPS or a VPN.";
    }

    public static bool IsAllowedByPrivateMode(AppSettings settings)
    {
        if (!settings.PrivateMode) return true;
        return TryValidate(settings.ActiveProvider.Endpoint, out var endpoint, out _) && IsSameComputer(endpoint);
    }
}
