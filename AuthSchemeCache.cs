using System.Collections.Concurrent;
using System.Net;

namespace OnvifLib
{
  /// <summary>
  /// Process-wide, in-memory cache of which HTTP transport authentication scheme
  /// (Anonymous/Digest/Basic) a given ONVIF endpoint URL accepted last time. Avoids
  /// repeating a failed scheme on every reconnect/resubscribe — once an endpoint is
  /// known to require e.g. Basic, new clients for the same URL start with Basic directly.
  /// </summary>
  public static class AuthSchemeCache
  {
    private static readonly ConcurrentDictionary<string, AuthenticationSchemes> _schemes = new();

    public static AuthenticationSchemes? TryGet(string url) =>
      _schemes.TryGetValue(url, out var scheme) ? scheme : null;

    public static void Set(string url, AuthenticationSchemes scheme) =>
      _schemes[url] = scheme;
  }
}
