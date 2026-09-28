using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MTCVision.Backend.Camera;

/// <summary>
/// The machine's IPv4 interfaces, with the subnet each one covers.
///
/// This matters more than it looks: a GigE camera answers a discovery broadcast from any interface
/// on the same physical wire, but streaming only works from an interface on the camera's own
/// subnet — the camera is told "send the image data to this address" and needs a route back. So a
/// camera can be perfectly discoverable through an interface it can never stream to.
/// </summary>
public static class LocalNetworkInterfaces
{
    public static IReadOnlyList<LocalNetworkInterface> List() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(ua => new LocalNetworkInterface(
                    Name: ni.Name,
                    Description: ni.Description,
                    Ip: ua.Address.ToString(),
                    PrefixLength: ua.PrefixLength,
                    IsLoopback: ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)))
            .ToList();

    /// <summary>Interfaces a camera at <paramref name="cameraIp"/> could actually stream to.</summary>
    public static IReadOnlyList<LocalNetworkInterface> ReachingSubnetOf(string cameraIp) =>
        List().Where(ni => !ni.IsLoopback && ni.Covers(cameraIp)).ToList();
}

public sealed record LocalNetworkInterface(
    string Name,
    string Description,
    string Ip,
    int PrefixLength,
    bool IsLoopback)
{
    /// <summary>True when <paramref name="otherIp"/> falls inside this interface's subnet.</summary>
    public bool Covers(string otherIp)
    {
        if (!IPAddress.TryParse(otherIp, out var other) || other.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (PrefixLength is < 0 or > 32)
        {
            return false;
        }

        var mask = PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
        return (ToNumber(IPAddress.Parse(Ip)) & mask) == (ToNumber(other) & mask);
    }

    private static uint ToNumber(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
