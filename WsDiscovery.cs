using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;

namespace OnvifLib
{
  public record DiscoveredDevice(
    string EndpointUuid,
    string Ip,
    int Port,
    IReadOnlyList<string> XAddrs,
    IReadOnlyList<string> Scopes,
    string? Hardware,
    string? Name);

  /// `ScanOk` is false when no network interface could even send a probe (e.g. every
  /// interface failed to join the multicast group, or there were none) — distinguishes
  /// "scanned, found nothing" from "couldn't scan at all" so callers don't misreport the
  /// latter as "no cameras on the LAN".
  public record ProbeResult(List<DiscoveredDevice> Devices, bool ScanOk);

  /// WS-Discovery (SOAP-over-UDP multicast) prober. Distinct from CameraScanner — that one
  /// brute-forces an IP range with HTTP, this one asks devices to announce themselves, so it
  /// finds the camera's actual ONVIF port even when it isn't the default.
  public static class WsDiscovery
  {
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int MulticastPort = 3702;

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Disc = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private static readonly XNamespace Dn = "http://www.onvif.org/ver10/network/wsdl";

    public static async Task<ProbeResult> ProbeAsync(
      TimeSpan timeout, CancellationToken ct, IOnvifLogger? logger = null)
    {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(timeout);

      var listenTasks = GetCandidateInterfaces()
        .Select(addr => ListenOnInterfaceAsync(addr, cts.Token, logger))
        .ToList();

      var results = await Task.WhenAll(listenTasks);

      var devices = results
        .SelectMany(r => r.Devices)
        .GroupBy(d => d.EndpointUuid)
        .Select(g => g.First())
        .ToList();
      var scanOk = results.Any(r => r.Probed);

      return new ProbeResult(devices, scanOk);
    }

    // Name fragments of virtual adapters (container bridges, VPN/tunnel interfaces) that can
    // never reach real LAN cameras — probing them only wastes a multicast join and risks
    // picking up unrelated WS-Discovery responders (e.g. a router) instead of cameras.
    private static readonly string[] VirtualInterfaceNameFragments =
      ["docker", "veth", "br-", "vmnet", "virtualbox", "vbox", "wsl", "wg", "tailscale", "zerotier", "tun", "tap"];

    private static IEnumerable<IPAddress> GetCandidateInterfaces()
    {
      foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
      {
        if (nic.OperationalStatus != OperationalStatus.Up)
          continue;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
          continue;

        var name = nic.Name.ToLowerInvariant();
        if (VirtualInterfaceNameFragments.Any(name.Contains))
          continue;

        foreach (var ua in nic.GetIPProperties().UnicastAddresses)
        {
          if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
            yield return ua.Address;
        }
      }
    }

    private static async Task<(List<DiscoveredDevice> Devices, bool Probed)> ListenOnInterfaceAsync(
      IPAddress localAddress,
      CancellationToken ct,
      IOnvifLogger? logger)
    {
      var devices = new List<DiscoveredDevice>();

      UdpClient? client = null;
      try
      {
        client = new UdpClient(new IPEndPoint(localAddress, 0));
        client.JoinMulticastGroup(MulticastAddress, localAddress);
      }
      catch (Exception ex)
      {
        logger?.Warning($"WS-Discovery: cannot bind/join multicast on {localAddress}: {ex.Message}");
        client?.Dispose();
        return (devices, false);
      }

      using (client)
      {
        try
        {
          var probe = BuildProbeMessage();
          var probeEndpoint = new IPEndPoint(MulticastAddress, MulticastPort);
          await client.SendAsync(probe, probe.Length, probeEndpoint);

          // WS-Discovery runs over UDP multicast with no delivery guarantee — a single probe
          // (or its reply) is commonly lost, especially over WiFi. Resend a couple of times
          // while still listening, instead of relying on one shot.
          var resendTask = ResendProbeAsync(client, probe, probeEndpoint, ct);

          while (!ct.IsCancellationRequested)
          {
            UdpReceiveResult result;
            try
            {
              var receiveTask = client.ReceiveAsync();
              result = await receiveTask.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
              break;
            }

            try
            {
              var device = ParseProbeMatch(result.Buffer);
              if (device != null)
                devices.Add(device);
            }
            catch (Exception ex)
            {
              logger?.Debug($"WS-Discovery: failed to parse response from {result.RemoteEndPoint}: {ex.Message}");
            }
          }

          await resendTask;
          return (devices, true);
        }
        catch (Exception ex)
        {
          logger?.Warning($"WS-Discovery: probe on {localAddress} failed: {ex.Message}");
          return (devices, false);
        }
      }
    }

    private static async Task ResendProbeAsync(
      UdpClient client, byte[] probe, IPEndPoint probeEndpoint, CancellationToken ct)
    {
      try
      {
        for (var i = 0; i < 2; i++)
        {
          await Task.Delay(TimeSpan.FromSeconds(1), ct);
          await client.SendAsync(probe, probe.Length, probeEndpoint);
        }
      }
      catch (OperationCanceledException)
      {
        // timeout elapsed before a resend was due — fine, at least one probe was sent
      }
    }

    private static byte[] BuildProbeMessage()
    {
      var messageId = $"uuid:{Guid.NewGuid()}";

      var envelope = new XElement(Soap + "Envelope",
        new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "wsa", Wsa.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "d", Disc.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "dn", Dn.NamespaceName),
        new XElement(Soap + "Header",
          new XElement(Wsa + "MessageID", messageId),
          new XElement(Wsa + "To", "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
          new XElement(Wsa + "Action", "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe")),
        new XElement(Soap + "Body",
          new XElement(Disc + "Probe",
            new XElement(Disc + "Types", "dn:NetworkVideoTransmitter"))));

      var doc = new XDocument(envelope);
      return System.Text.Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    }

    private static DiscoveredDevice? ParseProbeMatch(byte[] buffer)
    {
      var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer));

      var probeMatch = doc.Descendants(Disc + "ProbeMatch").FirstOrDefault();
      if (probeMatch == null)
        return null;

      var endpointUuid = probeMatch.Descendants(Wsa + "Address").FirstOrDefault()?.Value?.Trim();
      var typesRaw = probeMatch.Element(Disc + "Types")?.Value ?? string.Empty;
      var xAddrsRaw = probeMatch.Element(Disc + "XAddrs")?.Value ?? string.Empty;
      var scopesRaw = probeMatch.Element(Disc + "Scopes")?.Value ?? string.Empty;

      // Split on any whitespace, not just a literal space — some devices format these
      // space-separated lists with newlines/tabs, which a literal-space Split would leave
      // as one unparseable token (new Uri() would throw on it).
      var xAddrs = xAddrsRaw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      var scopes = scopesRaw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

      if (xAddrs.Length == 0)
        return null;

      // Some non-ONVIF devices answer any WS-Discovery Probe regardless of the requested
      // Types filter (generic/non-compliant WSD stacks, e.g. on routers). Require the reply
      // to actually self-identify as an ONVIF NetworkVideoTransmitter before trusting it.
      var declaresOnvif = typesRaw.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase)
        || scopes.Any(s => s.StartsWith("onvif://", StringComparison.OrdinalIgnoreCase));
      if (!declaresOnvif)
        return null;

      var firstXAddr = new Uri(xAddrs[0]);

      return new DiscoveredDevice(
        EndpointUuid: endpointUuid ?? xAddrs[0],
        Ip: firstXAddr.Host,
        Port: firstXAddr.Port,
        XAddrs: xAddrs,
        Scopes: scopes,
        Hardware: ExtractScopeValue(scopes, "onvif://www.onvif.org/hardware/"),
        Name: ExtractScopeValue(scopes, "onvif://www.onvif.org/name/"));
    }

    private static string? ExtractScopeValue(string[] scopes, string prefix)
    {
      var match = scopes.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
      if (match == null)
        return null;

      var raw = match[prefix.Length..];
      return Uri.UnescapeDataString(raw);
    }
  }
}
