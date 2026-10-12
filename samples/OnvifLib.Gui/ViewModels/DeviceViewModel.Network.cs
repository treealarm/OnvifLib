using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnvifLib.Gui.ViewModels;

/// <summary>One protocol row, editable in place.</summary>
public sealed partial class ProtocolRow(string name, bool enabled, string portsText) : ObservableObject
{
  public string Name { get; } = name;
  [ObservableProperty] private bool _enabled = enabled;
  [ObservableProperty] private string _portsText = portsText;
}

/// <summary>
/// Device network settings. Every Apply here can cut the connection this app is using, so every
/// one asks first and says how.
/// </summary>
public sealed partial class DeviceViewModel
{
  public ObservableCollection<OnvifNetworkInterface> Interfaces { get; } = [];

  [ObservableProperty] private OnvifNetworkInterface? _selectedInterface;
  [ObservableProperty] private bool _editDhcp;
  [ObservableProperty] private string _editAddress = "";
  [ObservableProperty] private int _editPrefix = 24;

  [ObservableProperty] private string _editHostname = "";
  [ObservableProperty] private string _hostnameNote = "";

  [ObservableProperty] private bool _dnsFromDhcp;
  [ObservableProperty] private string _dnsServers = "";
  [ObservableProperty] private string _dnsSearchDomains = "";
  [ObservableProperty] private string _dnsDhcpText = "";

  [ObservableProperty] private bool _ntpFromDhcp;
  [ObservableProperty] private string _ntpServers = "";
  [ObservableProperty] private string _ntpDhcpText = "";

  [ObservableProperty] private string _gatewayIPv4 = "";

  public ObservableCollection<ProtocolRow> Protocols { get; } = [];

  partial void OnSelectedInterfaceChanged(OnvifNetworkInterface? value)
  {
    if (value is null) return;
    EditDhcp = value.Dhcp;
    EditAddress = value.ManualAddress ?? value.FromDhcpAddress ?? "";
    EditPrefix = value.ManualPrefix ?? 24;
  }

  /// <summary>Reads every network setting. One failure does not stop the others.</summary>
  [RelayCommand]
  private async Task LoadNetworkAsync()
  {
    if (Session is not { } session) return;
    var camera = session.Camera;

    var (ok, interfaces) = await Runner.RunAsync("GetNetworkInterfaces", camera.GetNetworkInterfacesAsync);
    if (ok)
    {
      Interfaces.Clear();
      foreach (var i in interfaces ?? []) Interfaces.Add(i);
      SelectedInterface = Interfaces.FirstOrDefault();
    }

    var (hok, hostname) = await Runner.RunAsync("GetHostname", camera.GetHostnameAsync);
    if (hok && hostname is not null)
    {
      EditHostname = hostname.Name;
      HostnameNote = hostname.FromDhcp ? "currently from DHCP — setting one turns that off" : "";
    }

    var (dok, dns) = await Runner.RunAsync("GetDNS", camera.GetDnsAsync);
    if (dok && dns is not null)
    {
      DnsFromDhcp = dns.FromDhcp;
      DnsServers = string.Join(", ", dns.ManualServers);
      DnsSearchDomains = string.Join(", ", dns.SearchDomains);
      DnsDhcpText = dns.DhcpServers.Count == 0 ? "" : $"via DHCP: {string.Join(", ", dns.DhcpServers)}";
    }

    var (nok, ntp) = await Runner.RunAsync("GetNTP", camera.GetNtpAsync);
    if (nok && ntp is not null)
    {
      NtpFromDhcp = ntp.FromDhcp;
      NtpServers = string.Join(", ", ntp.ManualServers);
      NtpDhcpText = ntp.DhcpServers.Count == 0 ? "" : $"via DHCP: {string.Join(", ", ntp.DhcpServers)}";
    }

    var (gok, gateway) = await Runner.RunAsync("GetNetworkDefaultGateway", camera.GetNetworkDefaultGatewayAsync);
    if (gok && gateway is not null) GatewayIPv4 = string.Join(", ", gateway.IPv4);

    var (pok, protocols) = await Runner.RunAsync("GetNetworkProtocols", camera.GetNetworkProtocolsAsync);
    if (pok)
    {
      Protocols.Clear();
      foreach (var p in protocols ?? []) Protocols.Add(new ProtocolRow(p.Name, p.Enabled, string.Join(", ", p.Ports)));
    }
  }

  [RelayCommand]
  private async Task ApplyInterfaceAsync()
  {
    if (Session is not { } session || SelectedInterface is not { } nic) return;
    var dhcp = EditDhcp;
    var address = EditAddress.Trim();
    var prefix = EditPrefix;
    if (!dhcp && !System.Net.IPAddress.TryParse(address, out _))
    {
      Runner.Report($"'{address}' is not an IPv4 address", isError: true);
      return;
    }
    var talkingThroughIt = nic.ManualAddress == session.Camera.Ip || nic.FromDhcpAddress == session.Camera.Ip;
    if (!await dialogs.ConfirmAsync("Change the camera's address",
          $"Set {nic.Name} to {(dhcp ? "DHCP" : $"{address}/{prefix}")}?" +
          (talkingThroughIt
            ? $"\n\nThis app reaches the camera through this interface ({session.Camera.Ip}). The connection will drop, and you will have to find the camera again at its new address."
            : "") +
          "\n\nA wrong address can make the camera unreachable from this network."))
      return;

    var (ok, rebootNeeded) = await Runner.RunAsync($"SetNetworkInterface [{nic.Token}]",
      () => session.Camera.SetNetworkInterfaceAsync(nic.Token, dhcp, dhcp ? null : address, prefix));
    if (ok) Runner.Report(rebootNeeded
      ? "Interface changed — the camera says it must reboot for it to take effect"
      : "Interface changed");
  }

  [RelayCommand]
  private async Task ApplyHostnameAsync()
  {
    if (Session is not { } session) return;
    var name = EditHostname.Trim();
    if (name.Length == 0) { Runner.Report("Type a hostname first", isError: true); return; }
    if (!await dialogs.ConfirmAsync("Change the hostname", $"Set the camera's hostname to '{name}'?"))
      return;
    if (await Runner.RunAsync("SetHostname", () => session.Camera.SetHostnameAsync(name)))
      HostnameNote = "";
  }

  [RelayCommand]
  private async Task ApplyDnsAsync()
  {
    if (Session is not { } session) return;
    var servers = SplitList(DnsServers);
    var domains = SplitList(DnsSearchDomains);
    var fromDhcp = DnsFromDhcp;
    if (!await dialogs.ConfirmAsync("Change DNS",
          fromDhcp
            ? "Take DNS servers from DHCP?"
            : $"Set DNS servers to {(servers.Count == 0 ? "none" : string.Join(", ", servers))}?" +
              "\n\nWrong DNS breaks NTP by host name and anything else the camera resolves."))
      return;
    if (await Runner.RunAsync("SetDNS", () => session.Camera.SetDnsAsync(fromDhcp, domains, servers)))
      await LoadNetworkAsync();
  }

  [RelayCommand]
  private async Task ApplyNtpAsync()
  {
    if (Session is not { } session) return;
    var servers = SplitList(NtpServers);
    var fromDhcp = NtpFromDhcp;
    if (!await dialogs.ConfirmAsync("Change NTP",
          fromDhcp
            ? "Take NTP servers from DHCP?"
            : $"Set NTP servers to {(servers.Count == 0 ? "none" : string.Join(", ", servers))}?" +
              "\n\nIn NTP mode the camera's clock follows these servers — and its recordings are indexed by that clock."))
      return;
    if (await Runner.RunAsync("SetNTP", () => session.Camera.SetNtpAsync(fromDhcp, servers)))
      await LoadNetworkAsync();
  }

  [RelayCommand]
  private async Task ApplyGatewayAsync()
  {
    if (Session is not { } session) return;
    var gateways = SplitList(GatewayIPv4);
    if (!await dialogs.ConfirmAsync("Change the default gateway",
          $"Set the IPv4 gateway to {(gateways.Count == 0 ? "none" : string.Join(", ", gateways))}?" +
          "\n\nA wrong gateway makes the camera unreachable from other subnets."))
      return;
    if (await Runner.RunAsync("SetNetworkDefaultGateway", () => session.Camera.SetNetworkDefaultGatewayAsync(gateways)))
      await LoadNetworkAsync();
  }

  [RelayCommand]
  private async Task ApplyProtocolsAsync()
  {
    if (Session is not { } session || Protocols.Count == 0) return;
    var list = new List<OnvifNetworkProtocol>();
    foreach (var row in Protocols)
    {
      var ports = new List<int>();
      foreach (var part in SplitList(row.PortsText))
      {
        if (!int.TryParse(part, out var port) || port is < 1 or > 65535)
        {
          Runner.Report($"'{part}' is not a port number ({row.Name})", isError: true);
          return;
        }
        ports.Add(port);
      }
      list.Add(new OnvifNetworkProtocol(row.Name, row.Enabled, ports));
    }

    var httpOff = list.Any(p => p.Name == "HTTP" && !p.Enabled);
    if (!await dialogs.ConfirmAsync("Change network protocols",
          string.Join("\n", list.Select(p => $"{p.Name}: {(p.Enabled ? "on" : "off")} {string.Join(", ", p.Ports)}")) +
          (httpOff ? "\n\nHTTP off turns off the ONVIF endpoint this app talks to." : "") +
          "\n\nSome cameras restart their web server on any change here; the connection may drop for a moment."))
      return;
    if (await Runner.RunAsync("SetNetworkProtocols", () => session.Camera.SetNetworkProtocolsAsync(list)))
      await LoadNetworkAsync();
  }

  private static List<string> SplitList(string text) =>
    text.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

  private void ClearNetwork()
  {
    Interfaces.Clear();
    Protocols.Clear();
    EditHostname = HostnameNote = DnsServers = DnsSearchDomains = DnsDhcpText = NtpServers = NtpDhcpText = GatewayIPv4 = EditAddress = "";
  }
}
