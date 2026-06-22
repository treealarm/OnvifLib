using System.Net;
using System.ServiceModel.Channels;
using System.Text;

namespace OnvifLib
{
  /// <summary>
  /// Holds the active CustomBinding and can switch its HTTP transport authentication
  /// scheme (Anonymous/Digest/Basic) when a camera rejects a request with a 401.
  /// If constructed with a cache key, the initial scheme is taken from
  /// <see cref="AuthSchemeCache"/> (last scheme that worked for that URL), and any
  /// switch is persisted back to the cache so the next connection to the same
  /// endpoint starts with the right scheme instead of re-discovering it.
  /// </summary>
  public class CustomBindingProvider
  {
    private CustomBinding _binding;
    private readonly double _timeout;
    private readonly string? _cacheKey;

    public CustomBindingProvider(double timeout, string? cacheKey = null)
      : this(timeout, (cacheKey != null ? AuthSchemeCache.TryGet(cacheKey) : null) ?? AuthenticationSchemes.Anonymous, cacheKey)
    {
    }

    public CustomBindingProvider(double timeout, AuthenticationSchemes initialScheme, string? cacheKey = null)
    {
      _timeout = timeout;
      _cacheKey = cacheKey;
      _binding = Build(initialScheme);
    }

    public CustomBinding Current => _binding;

    public AuthenticationSchemes Scheme =>
      _binding.Elements.OfType<HttpTransportBindingElement>().First().AuthenticationScheme;

    public static AuthenticationSchemes SchemeOf(CustomBinding binding) =>
      binding.Elements.OfType<HttpTransportBindingElement>().First().AuthenticationScheme;

    public bool IsDigest => Scheme == AuthenticationSchemes.Digest;

    /// <summary>Switches to Digest transport; no-op if already Digest.</summary>
    public bool SwitchToDigest() => SwitchTo(AuthenticationSchemes.Digest);

    /// <summary>Switches to Basic transport; no-op if already Basic.</summary>
    public bool SwitchToBasic() => SwitchTo(AuthenticationSchemes.Basic);

    public bool SwitchTo(AuthenticationSchemes scheme)
    {
      if (Scheme == scheme) return false;
      _binding = Build(scheme);
      if (_cacheKey != null)
        AuthSchemeCache.Set(_cacheKey, scheme);
      return true;
    }

    /// <summary>
    /// Inspects the exception chain for an HTTP 401 challenge that names "Basic" or
    /// "Digest" (e.g. "The authentication header received from the server was 'Basic
    /// realm=...'") and switches the binding to that scheme if it differs from the
    /// current one. Returns true if the binding was switched — the caller should
    /// retry the request with <see cref="Current"/>.
    /// </summary>
    public bool TrySwitchToChallenged(Exception ex)
    {
      var required = ParseChallengedScheme(ex);
      return required.HasValue && SwitchTo(required.Value);
    }

    /// <summary>Persists the currently active scheme as the known-working one for the cache key.</summary>
    public void RememberWorking()
    {
      if (_cacheKey != null)
        AuthSchemeCache.Set(_cacheKey, Scheme);
    }

    private static AuthenticationSchemes? ParseChallengedScheme(Exception? ex)
    {
      for (var e = ex; e != null; e = e.InnerException)
      {
        var message = e.Message;
        if (message.Contains("Basic", StringComparison.OrdinalIgnoreCase))
          return AuthenticationSchemes.Basic;
        if (message.Contains("Digest", StringComparison.OrdinalIgnoreCase))
          return AuthenticationSchemes.Digest;
      }
      return null;
    }

    private CustomBinding Build(AuthenticationSchemes auth)
    {
      var b = new CustomBinding(
        new TextMessageEncodingBindingElement(MessageVersion.Soap12WSAddressing10, Encoding.UTF8),
        new HttpTransportBindingElement { AuthenticationScheme = auth, MaxReceivedMessageSize = 4 * 1024 * 1024 }
      );
      b.OpenTimeout = b.CloseTimeout = b.SendTimeout = b.ReceiveTimeout =
        TimeSpan.FromSeconds(_timeout);
      return b;
    }
  }
}
