using System.Net.Sockets;
using OnvifLib;
using Dsr = DeviceServiceReference;

// Device administration: users, clock, network, discovery scopes. Everything here changes how the
// camera is reached or who may reach it, so callers with a UI should confirm before any Set.
public partial class Camera
{
  // ── Users ─────────────────────────────────────────────────────────────────────

  public async Task<List<OnvifUser>> GetUsersAsync()
  {
    using var device = await GetDevice();
    var resp = await device.GetUsersAsync();
    return (resp?.User ?? [])
      .Select(u => new OnvifUser(u.Username ?? string.Empty, (OnvifUserLevel)u.UserLevel))
      .ToList();
  }

  /// <remarks>
  /// Cameras commonly refuse weak passwords (<c>ter:PasswordTooWeak</c>) and the Extended level.
  /// </remarks>
  public async Task CreateUserAsync(string username, string password, OnvifUserLevel level)
  {
    using var device = await GetDevice();
    await device.CreateUsersAsync([new Dsr.User { Username = username, Password = password, UserLevel = (Dsr.UserLevel)level }]);
  }

  /// <summary>Changes a user's password, level, or both.</summary>
  /// <param name="password">Null leaves the password as it is.</param>
  /// <param name="level">
  /// Null keeps the current level. SetUser requires one, so it is read back first rather than
  /// sent as the enum default — which would silently make the user an Administrator.
  /// </param>
  /// <remarks>
  /// Changing the password of the account this Camera logs in with locks this instance out; the
  /// caller has to create a new Camera with the new password afterwards.
  /// </remarks>
  /// <param name="username">The existing user to change.</param>
  public async Task SetUserAsync(string username, string? password, OnvifUserLevel? level)
  {
    using var device = await GetDevice();
    var userLevel = level is { } l
      ? (Dsr.UserLevel)l
      : ((await device.GetUsersAsync())?.User ?? []).FirstOrDefault(u => u.Username == username)?.UserLevel
        ?? throw new InvalidOperationException($"The camera has no user '{username}'");
    await device.SetUserAsync([new Dsr.User { Username = username, Password = password, UserLevel = userLevel }]);
  }

  /// <remarks>Cameras refuse to delete fixed users (<c>ter:FixedUser</c>), and usually the last administrator.</remarks>
  public async Task DeleteUserAsync(string username)
  {
    using var device = await GetDevice();
    await device.DeleteUsersAsync([username]);
  }

  // ── Date and time ─────────────────────────────────────────────────────────────

  public async Task<OnvifSystemDateTime> GetSystemDateAndTimeAsync()
  {
    using var device = await GetDevice();
    var t = await device.GetSystemDateAndTimeAsync();
    return new OnvifSystemDateTime(
      t.DateTimeType.ToString(),
      t.DaylightSavings,
      string.IsNullOrEmpty(t.TimeZone?.TZ) ? null : t.TimeZone.TZ,
      ToDateTime(t.UTCDateTime, DateTimeKind.Utc),
      ToDateTime(t.LocalDateTime, DateTimeKind.Unspecified));
  }

  /// <summary>Sets how the camera keeps time, its zone, and — in manual mode — the time itself.</summary>
  /// <param name="dateTimeType">"Manual" or "NTP". NTP needs servers configured (<see cref="SetNtpAsync"/>).</param>
  /// <param name="timeZone">
  /// POSIX TZ string. Null keeps the camera's current zone: some firmware rejects a request with
  /// no zone at all, and sending an empty one would wipe it.
  /// </param>
  /// <param name="utc">Required for Manual, ignored for NTP (the schema forbids it there).</param>
  /// <param name="daylightSavings">Whether the camera applies daylight saving time.</param>
  public async Task SetSystemDateAndTimeAsync(string dateTimeType, bool daylightSavings, string? timeZone, System.DateTime? utc)
  {
    var type = Enum.TryParse<Dsr.SetDateTimeType>(dateTimeType, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
      ? parsed
      : throw new ArgumentException($"'{dateTimeType}' is not a date/time type; expected Manual or NTP", nameof(dateTimeType));
    if (type == Dsr.SetDateTimeType.Manual && utc is null)
      throw new ArgumentException("Manual mode needs the time to set", nameof(utc));

    using var device = await GetDevice();
    var current = await device.GetSystemDateAndTimeAsync();
    var zone = timeZone is null ? current.TimeZone : new Dsr.TimeZone { TZ = timeZone };

    Dsr.DateTime? when = null;
    if (type == Dsr.SetDateTimeType.Manual && utc is { } u)
    {
      var v = u.Kind == DateTimeKind.Local ? u.ToUniversalTime() : u;
      when = new Dsr.DateTime
      {
        Date = new Dsr.Date { Year = v.Year, Month = v.Month, Day = v.Day },
        Time = new Dsr.Time { Hour = v.Hour, Minute = v.Minute, Second = v.Second },
      };
    }

    await device.SetSystemDateAndTimeAsync(type, daylightSavings, zone, when);
  }

  private static System.DateTime? ToDateTime(Dsr.DateTime? d, DateTimeKind kind)
  {
    if (d?.Date == null || d.Time == null) return null;
    try
    {
      return new System.DateTime(d.Date.Year, d.Date.Month, d.Date.Day, d.Time.Hour, d.Time.Minute, d.Time.Second, 0, kind);
    }
    catch (ArgumentOutOfRangeException)
    {
      // A camera with an unset clock can report day 0 or month 0. That is "no time", not a crash.
      return null;
    }
  }

  // ── Discovery scopes ──────────────────────────────────────────────────────────

  public async Task<List<OnvifScope>> GetScopesAsync()
  {
    using var device = await GetDevice();
    var resp = await device.GetScopesAsync();
    return (resp?.Scopes ?? [])
      .Select(s => new OnvifScope(s.ScopeItem ?? string.Empty, s.ScopeDef == Dsr.ScopeDefinition.Fixed))
      .ToList();
  }

  /// <summary>Replaces every configurable scope with <paramref name="scopes"/>.</summary>
  /// <remarks>Fixed scopes must not be in the list: cameras fault with <c>ter:ScopeOverwrite</c>.</remarks>
  public async Task SetScopesAsync(IReadOnlyList<string> scopes)
  {
    using var device = await GetDevice();
    await device.SetScopesAsync(scopes.ToArray());
  }

  public async Task AddScopesAsync(IReadOnlyList<string> scopes)
  {
    using var device = await GetDevice();
    await device.AddScopesAsync(scopes.ToArray());
  }

  /// <remarks>Fixed scopes cannot be removed (<c>ter:FixedScope</c>).</remarks>
  public async Task RemoveScopesAsync(IReadOnlyList<string> scopes)
  {
    using var device = await GetDevice();
    await device.RemoveScopesAsync(new Dsr.RemoveScopesRequest { ScopeItem = scopes.ToArray() });
  }

  // ── Network ───────────────────────────────────────────────────────────────────

  public async Task<List<OnvifNetworkInterface>> GetNetworkInterfacesAsync()
  {
    using var device = await GetDevice();
    var resp = await device.GetNetworkInterfacesAsync();
    return (resp?.NetworkInterfaces ?? []).Select(n =>
    {
      var config = n.IPv4?.Config;
      var manual = config?.Manual?.FirstOrDefault();
      return new OnvifNetworkInterface(
        n.token ?? string.Empty,
        n.Enabled,
        n.Info?.Name ?? string.Empty,
        n.Info?.HwAddress ?? string.Empty,
        n.Info is { MTUSpecified: true } info ? info.MTU : null,
        n.IPv4?.Enabled ?? false,
        config?.DHCP ?? false,
        manual?.Address,
        manual?.PrefixLength,
        config?.LinkLocal?.Address,
        config?.FromDHCP?.Address);
    }).ToList();
  }

  /// <summary>Switches an interface between DHCP and a static IPv4 address.</summary>
  /// <returns>True when the camera says it must reboot for the change to take effect.</returns>
  /// <remarks>
  /// Changing the address of the interface this Camera talks through drops the connection; the
  /// caller has to reconnect at the new address.
  /// </remarks>
  public async Task<bool> SetNetworkInterfaceAsync(string interfaceToken, bool dhcp, string? address = null, int? prefixLength = null)
  {
    if (!dhcp && string.IsNullOrWhiteSpace(address))
      throw new ArgumentException("A static configuration needs an address", nameof(address));

    using var device = await GetDevice();
    return await device.SetNetworkInterfacesAsync(interfaceToken, new Dsr.NetworkInterfaceSetConfiguration
    {
      IPv4 = new Dsr.IPv4NetworkInterfaceSetConfiguration
      {
        Enabled = true,
        EnabledSpecified = true,
        DHCP = dhcp,
        DHCPSpecified = true,
        Manual = dhcp ? null : [new Dsr.PrefixedIPv4Address { Address = address!.Trim(), PrefixLength = prefixLength ?? 24 }],
      },
    });
  }

  public async Task<OnvifDns> GetDnsAsync()
  {
    using var device = await GetDevice();
    var d = await device.GetDNSAsync();
    return new OnvifDns(
      d?.FromDHCP ?? false,
      (d?.SearchDomain ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
      (d?.DNSManual ?? []).Select(AddressToString).Where(s => s.Length > 0).ToList(),
      (d?.DNSFromDHCP ?? []).Select(AddressToString).Where(s => s.Length > 0).ToList());
  }

  /// <param name="servers">IPv4 or IPv6 addresses. Ignored by the camera when <paramref name="fromDhcp"/> is true.</param>
  /// <param name="fromDhcp">Take DNS servers from DHCP instead of the list.</param>
  /// <param name="searchDomains">Search domains; empty for none.</param>
  public async Task SetDnsAsync(bool fromDhcp, IReadOnlyList<string> searchDomains, IReadOnlyList<string> servers)
  {
    var manual = servers.Select(s => s.Trim()).Where(s => s.Length > 0).Select(s =>
      System.Net.IPAddress.TryParse(s, out var ip)
        ? ip.AddressFamily == AddressFamily.InterNetworkV6
          ? new Dsr.IPAddress { Type = Dsr.IPType.IPv6, IPv6Address = s }
          : new Dsr.IPAddress { Type = Dsr.IPType.IPv4, IPv4Address = s }
        : throw new ArgumentException($"'{s}' is not an IP address; DNS servers must be given by address", nameof(servers)))
      .ToArray();

    using var device = await GetDevice();
    await device.SetDNSAsync(fromDhcp,
      searchDomains.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray(),
      manual.Length == 0 ? null : manual);
  }

  public async Task<OnvifNtp> GetNtpAsync()
  {
    using var device = await GetDevice();
    var n = await device.GetNTPAsync();
    return new OnvifNtp(
      n?.FromDHCP ?? false,
      (n?.NTPManual ?? []).Select(HostToString).Where(s => s.Length > 0).ToList(),
      (n?.NTPFromDHCP ?? []).Select(HostToString).Where(s => s.Length > 0).ToList());
  }

  /// <param name="servers">IP addresses or host names; a host name is sent as a DNS entry.</param>
  /// <param name="fromDhcp">Take NTP servers from DHCP instead of the list.</param>
  public async Task SetNtpAsync(bool fromDhcp, IReadOnlyList<string> servers)
  {
    var manual = servers.Select(s => s.Trim()).Where(s => s.Length > 0).Select(s =>
      System.Net.IPAddress.TryParse(s, out var ip)
        ? ip.AddressFamily == AddressFamily.InterNetworkV6
          ? new Dsr.NetworkHost { Type = Dsr.NetworkHostType.IPv6, IPv6Address = s }
          : new Dsr.NetworkHost { Type = Dsr.NetworkHostType.IPv4, IPv4Address = s }
        : new Dsr.NetworkHost { Type = Dsr.NetworkHostType.DNS, DNSname = s })
      .ToArray();

    using var device = await GetDevice();
    await device.SetNTPAsync(fromDhcp, manual.Length == 0 ? null : manual);
  }

  public async Task<OnvifHostname> GetHostnameAsync()
  {
    using var device = await GetDevice();
    var h = await device.GetHostnameAsync();
    return new OnvifHostname(h?.Name ?? string.Empty, h?.FromDHCP ?? false);
  }

  public async Task SetHostnameAsync(string name)
  {
    using var device = await GetDevice();
    await device.SetHostnameAsync(name.Trim());
  }

  public async Task<List<OnvifNetworkProtocol>> GetNetworkProtocolsAsync()
  {
    using var device = await GetDevice();
    var resp = await device.GetNetworkProtocolsAsync();
    return (resp?.NetworkProtocols ?? [])
      .Select(p => new OnvifNetworkProtocol(p.Name.ToString(), p.Enabled, (p.Port ?? []).ToList()))
      .ToList();
  }

  /// <remarks>
  /// Disabling HTTP turns off the ONVIF endpoint itself, and some firmware restarts its web server
  /// on any change here — expect the connection to drop for a moment.
  /// </remarks>
  public async Task SetNetworkProtocolsAsync(IReadOnlyList<OnvifNetworkProtocol> protocols)
  {
    var list = protocols.Select(p => new Dsr.NetworkProtocol
    {
      Name = Enum.TryParse<Dsr.NetworkProtocolType>(p.Name, ignoreCase: true, out var name) && Enum.IsDefined(name)
        ? name
        : throw new ArgumentException($"'{p.Name}' is not a protocol ONVIF knows; expected HTTP, HTTPS or RTSP", nameof(protocols)),
      Enabled = p.Enabled,
      Port = p.Ports.ToArray(),
    }).ToArray();

    using var device = await GetDevice();
    await device.SetNetworkProtocolsAsync(list);
  }

  public async Task<OnvifNetworkGateway> GetNetworkDefaultGatewayAsync()
  {
    using var device = await GetDevice();
    var g = await device.GetNetworkDefaultGatewayAsync();
    return new OnvifNetworkGateway(
      (g?.IPv4Address ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
      (g?.IPv6Address ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList());
  }

  /// <param name="ipv6">Null rather than empty when there is none: some firmware rejects an empty element.</param>
  /// <param name="ipv4">IPv4 gateway addresses.</param>
  public async Task SetNetworkDefaultGatewayAsync(IReadOnlyList<string> ipv4, IReadOnlyList<string>? ipv6 = null)
  {
    using var device = await GetDevice();
    await device.SetNetworkDefaultGatewayAsync(
      ipv4.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray(),
      ipv6 is { Count: > 0 } v6 ? v6.ToArray() : null);
  }

  private static string AddressToString(Dsr.IPAddress a) => a.IPv4Address ?? a.IPv6Address ?? string.Empty;

  private static string HostToString(Dsr.NetworkHost h) => h.IPv4Address ?? h.IPv6Address ?? h.DNSname ?? string.Empty;
}
