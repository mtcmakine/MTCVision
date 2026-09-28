using System.Net;

namespace MTCVision.Backend.Camera;

/// <summary>
/// Who the camera says it is and how it will address itself on the network at next boot.
/// </summary>
public sealed record CameraIdentity(
    /// <summary>Free-text name the operator can set, up to 15 characters (GenICam DeviceUserID).</summary>
    string? UserId,
    string? ManufacturerName,
    string? ModelName,
    string? SerialNumber,
    string? DeviceVersion,
    /// <summary>"PersistentIP", "DHCP" or "LLA" — how the camera obtains its address at boot.</summary>
    string? IpConfiguration,
    string? PersistentIp,
    string? PersistentSubnetMask,
    string? PersistentGateway,
    /// <summary>The address it is answering on right now, which is not necessarily the persistent one.</summary>
    string? CurrentIp,
    string? Mac);

public sealed record CameraIdentityUpdate(
    string? UserId = null,
    string? IpConfiguration = null,
    string? PersistentIp = null,
    string? PersistentSubnetMask = null,
    string? PersistentGateway = null);

public static class IpAddressNumber
{
    /// <summary>GigE Vision stores addresses as a 32-bit number, most significant octet first.</summary>
    public static string ToDotted(double value)
    {
        var number = (uint)value;
        return $"{(number >> 24) & 0xFF}.{(number >> 16) & 0xFF}.{(number >> 8) & 0xFF}.{number & 0xFF}";
    }

    public static uint FromDotted(string text)
    {
        if (!IPAddress.TryParse(text, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new CameraConfigurationException($"'{text}' geçerli bir IPv4 adresi değil.");
        }

        var octets = address.GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }
}
