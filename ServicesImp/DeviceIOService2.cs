using DeviceIOServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Xml;
using System.Xml.Linq;

namespace OnvifLib
{
  public class DeviceIOService2 : OnvifServiceBase, IOnvifServiceFactory<DeviceIOService2>
  {
    public const string WSDL_V10 = "http://www.onvif.org/ver10/deviceIO/wsdl";
    private DeviceIOPortClient? _client;

    protected DeviceIOService2(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls() => new[] { WSDL_V10 };

    public static async Task<DeviceIOService2?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new DeviceIOService2(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();
      _client = _onvifClientFactory.CreateClient<DeviceIOPortClient, DeviceIOPort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _client.OpenAsync();
    }

    public async Task<List<OnvifDigitalInput>> GetDigitalInputsAsync()
    {
      if (_client == null) return [];
      var resp = await _client.GetDigitalInputsAsync();
      return (resp.DigitalInputs ?? [])
        .Select(d => new OnvifDigitalInput(
          d.token,
          d.IdleStateSpecified ? d.IdleState.ToString().ToLowerInvariant() : null))
        .ToList();
    }

    public async Task<OnvifDigitalInputOptions?> GetDigitalInputConfigurationOptionsAsync(string token)
    {
      if (_client == null) return null;
      var opts = await _client.GetDigitalInputConfigurationOptionsAsync(token);
      var count = opts?.IdleState?.Length ?? 0;
      // A camera that only ever reports one allowed idle state can't actually have it changed.
      return new OnvifDigitalInputOptions(token, count > 1);
    }

    public async Task<List<OnvifRelayOutputOptions>> GetRelayOutputOptionsAsync()
    {
      if (_client == null) return [];
      // Pass null (not "") so the optional RelayOutputToken element is omitted — per ONVIF spec
      // that means "all relays". An empty element makes some cameras read it as token="" → nothing.
      var resp = await _client.GetRelayOutputOptionsAsync(null!);
      return (resp.RelayOutputOptions ?? [])
        .Select(o =>
        {
          var delays = (o.DelayTimes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(OnvifDuration.ToMs)
            .ToList();
          return new OnvifRelayOutputOptions(
            o.token,
            (o.Mode ?? []).Select(m => m.ToString().ToLowerInvariant()).ToList(),
            o.Discrete,
            delays.Count > 0 ? delays.Min() : 0,
            delays.Count > 0 ? delays.Max() : 0,
            o.Discrete ? delays : null);
        })
        .ToList();
    }

    // Dahua and Orwell Elvees cameras report and only accept IdleState as an XML attribute on
    // DigitalInput (token+IdleState) — which is actually the ONVIF-spec-correct shape (see
    // DigitalInput in onvif.xsd), already what the typed generated client produces below. The
    // problem is some cameras' SetDigitalInputConfigurations is buggy for this exact shape: Dahua
    // rejects it ("token is NULL"), Orwell reports success without changing anything. This logic
    // is ported from a sibling project's known-good fix and is unverified against real hardware in
    // this environment: try the attribute format, verify by re-reading, and only fall back to a
    // raw nested-element SOAP body if the attribute format didn't actually take effect.
    public async Task SetDigitalInputIdleStateAsync(string token, string idleState)
    {
      if (_client == null)
        throw new InvalidOperationException("DeviceIO client not initialized");

      var idle = ParseIdleState(idleState);

      Exception? attributeFormatError = null;
      try
      {
        await _client.SetDigitalInputConfigurationsAsync(new[]
        {
          new DigitalInput { token = token, IdleState = idle, IdleStateSpecified = true }
        });
      }
      catch (Exception ex)
      {
        attributeFormatError = ex;
      }

      // Verify by re-reading. Some cameras don't echo IdleState on GetDigitalInputs at all
      // (IdleStateSpecified == false) — for those the result is Unknown and we can't confirm; in
      // that case trust the spec-correct attribute path as long as the Set call itself didn't
      // throw, rather than forcing the fallback and then failing on an unverifiable read.
      var afterAttribute = await VerifyIdleState(token, idle);
      if (afterAttribute == IdleVerify.Matches) return;
      if (afterAttribute == IdleVerify.Unknown && attributeFormatError == null) return;

      Exception? fallbackError = null;
      try
      {
        await SetDigitalInputIdleStateRawAsync(token, idle);
      }
      catch (Exception ex)
      {
        fallbackError = ex;
      }

      var afterFallback = await VerifyIdleState(token, idle);
      if (afterFallback == IdleVerify.Matches) return;
      if (afterFallback == IdleVerify.Unknown && fallbackError == null) return;

      throw new InvalidOperationException(
        $"Camera did not apply digital input idle state for '{token}'. " +
        $"Attribute-format attempt: {(attributeFormatError == null ? "reported success but no change observed" : attributeFormatError.Message)}. " +
        $"Fallback attempt: {(fallbackError == null ? "reported success but no change observed" : fallbackError.Message)}.");
    }

    private enum IdleVerify { Matches, Differs, Unknown }

    // Unknown = camera doesn't report IdleState on read (or the input token isn't in the list), so
    // the set can't be verified either way — the caller decides whether to trust a throw-free Set.
    private async Task<IdleVerify> VerifyIdleState(string token, DigitalIdleState expected)
    {
      var current = await GetDigitalInputsAsync();
      var match = current.FirstOrDefault(d => d.Token == token);
      if (match?.IdleState == null)
        return IdleVerify.Unknown;
      return match.IdleState == expected.ToString().ToLowerInvariant() ? IdleVerify.Matches : IdleVerify.Differs;
    }

    private static DigitalIdleState ParseIdleState(string idleState) =>
      idleState.Equals("open", StringComparison.OrdinalIgnoreCase) ? DigitalIdleState.open : DigitalIdleState.closed;

    // The generated typed DigitalInput type can only serialize IdleState as an attribute (fixed
    // by onvif.xsd) — this bypasses the typed proxy for one call to send the non-standard
    // nested-element shape some cameras' Set implementation expects instead.
    private async Task SetDigitalInputIdleStateRawAsync(string token, DigitalIdleState idleState)
    {
      const string tmd = "http://www.onvif.org/ver10/deviceIO/wsdl";
      const string tt = "http://www.onvif.org/ver10/schema";
      const string action = "http://www.onvif.org/ver10/deviceio/wsdl/SetDigitalInputConfigurations";

      var body = new XElement(XName.Get("SetDigitalInputConfigurations", tmd),
        new XElement(XName.Get("DigitalInputs", tmd),
          new XAttribute("token", token),
          new XElement(XName.Get("IdleState", tt), idleState.ToString())));

      using var bodyReader = body.CreateReader();
      using var message = Message.CreateMessage(MessageVersion.Soap12WSAddressing10, action, bodyReader);

      var factory = new ChannelFactory<IRequestChannel>(_binding, new EndpointAddress(_url));
      // Mirror OnvifClientFactory.CreateClient's credential setup so the fallback authenticates the
      // same way the typed proxy would: HTTP transport digest/basic (when the binding uses it) plus
      // the WS-Security UsernameToken header added via the inspector below.
      factory.Credentials.UserName.UserName = _username;
      factory.Credentials.UserName.Password = _password;
      factory.Credentials.HttpDigest.ClientCredential.UserName = _username;
      factory.Credentials.HttpDigest.ClientCredential.Password = _password;

      var inspector = new CustomMessageInspector(_logger);
      var securityToken = _onvifClientFactory.GetSecurityToken();
      if (securityToken != null)
        inspector.Headers.Add(new DigestSecurityHeader(new System.Net.NetworkCredential(_username, _password), securityToken));
      factory.Endpoint.EndpointBehaviors.Add(new CustomEndpointBehavior(inspector));

      IRequestChannel? channel = null;
      try
      {
        channel = factory.CreateChannel();
        await Task.Factory.FromAsync(channel.BeginOpen, channel.EndOpen, null);
        using var reply = await Task.Factory.FromAsync(channel.BeginRequest, channel.EndRequest, message, null);
        if (reply.IsFault)
          throw new InvalidOperationException("Camera returned a SOAP fault for the fallback SetDigitalInputConfigurations request");
      }
      finally
      {
        if (channel != null) { try { channel.Close(); } catch { channel.Abort(); } }
        try { factory.Close(); } catch { factory.Abort(); }
      }
    }

    public override void Dispose()
    {
      try { _client?.Close(); } catch { }
      base.Dispose();
    }
  }
}
