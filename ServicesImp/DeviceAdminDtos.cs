namespace OnvifLib
{
  // Device administration: users, clock, network and discovery scopes. All of it lives on the
  // Device Management service and is reached through Camera, not through a service object.

  public enum OnvifUserLevel { Administrator, Operator, User, Anonymous, Extended }

  /// <summary>A camera account. ONVIF never returns passwords, so there is no field for one.</summary>
  public record OnvifUser(string Username, OnvifUserLevel Level);

  /// <summary>The camera's clock and how it is kept.</summary>
  /// <param name="DateTimeType">"Manual" or "NTP".</param>
  /// <param name="TimeZone">POSIX TZ string as the camera stores it, e.g. <c>CET-1CEST,M3.5.0,M10.5.0/3</c>.</param>
  /// <param name="Utc">The camera's UTC time, or null when it did not report one.</param>
  /// <param name="Local">The camera's local wall-clock time, unspecified kind — it is in the camera's zone, not ours.</param>
  /// <param name="DaylightSavings">The camera applies daylight saving time.</param>
  public record OnvifSystemDateTime(string DateTimeType, bool DaylightSavings, string? TimeZone, DateTime? Utc, DateTime? Local);

  /// <summary>A WS-Discovery scope. Fixed scopes cannot be removed or replaced.</summary>
  public record OnvifScope(string ScopeItem, bool Fixed);

  /// <summary>One network interface and its IPv4 configuration.</summary>
  /// <param name="ManualAddress">The first manually configured address, or null.</param>
  /// <param name="FromDhcpAddress">The address DHCP handed out, when DHCP is on.</param>
  /// <param name="Token">The interface token SetNetworkInterfaceAsync takes.</param>
  /// <param name="Enabled">The interface is up.</param>
  /// <param name="Name">The interface name, e.g. eth0.</param>
  /// <param name="HwAddress">MAC address.</param>
  /// <param name="Mtu">MTU, or null when not reported.</param>
  /// <param name="IPv4Enabled">IPv4 is enabled on the interface.</param>
  /// <param name="Dhcp">IPv4 comes from DHCP.</param>
  /// <param name="ManualPrefix">Prefix length of <paramref name="ManualAddress"/>.</param>
  /// <param name="LinkLocalAddress">The link-local address, when the camera reports one.</param>
  public record OnvifNetworkInterface(
    string Token,
    bool Enabled,
    string Name,
    string HwAddress,
    int? Mtu,
    bool IPv4Enabled,
    bool Dhcp,
    string? ManualAddress,
    int? ManualPrefix,
    string? LinkLocalAddress,
    string? FromDhcpAddress);

  public record OnvifDns(bool FromDhcp, IReadOnlyList<string> SearchDomains, IReadOnlyList<string> ManualServers, IReadOnlyList<string> DhcpServers);

  /// <summary>NTP servers. Entries are IP addresses or host names, whichever the camera stores.</summary>
  public record OnvifNtp(bool FromDhcp, IReadOnlyList<string> ManualServers, IReadOnlyList<string> DhcpServers);

  public record OnvifHostname(string Name, bool FromDhcp);

  /// <param name="Name">"HTTP", "HTTPS" or "RTSP".</param>
  /// <param name="Enabled">The protocol is switched on.</param>
  /// <param name="Ports">The ports it listens on.</param>
  public record OnvifNetworkProtocol(string Name, bool Enabled, IReadOnlyList<int> Ports);

  public record OnvifNetworkGateway(IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6);
}
