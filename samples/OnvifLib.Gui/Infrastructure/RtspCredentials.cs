namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Splices credentials into an RTSP URI, because the library never does: both
/// <c>MediaService.GetStreamUri</c> and <c>ReplayService.GetReplayUriAsync</c> return the URI
/// exactly as the camera reported it, which is almost always without a user info component.
/// </summary>
public static class RtspCredentials
{
  public static string Inject(string uri, string user, string password)
  {
    if (string.IsNullOrEmpty(user) || string.IsNullOrWhiteSpace(uri)) return uri;

    try
    {
      var builder = new UriBuilder(uri);

      // Some firmwares already embed credentials. Overwriting them would replace a working pair
      // with whatever happens to be in the connection bar.
      if (!string.IsNullOrEmpty(builder.UserName)) return uri;

      // UriBuilder does NOT percent-encode the user info it is given. Without this, a password
      // containing '@' silently truncates the host — 'p@ss' turns rtsp://cam/live into a request
      // for host 'ss' — and ':' or '/' corrupt it in other ways. This is the single most likely
      // source of "works on my camera, not on the customer's".
      builder.UserName = Uri.EscapeDataString(user);
      builder.Password = Uri.EscapeDataString(password);

      // rtsp:// has no registered default port, so UriBuilder treats it generically: an explicit
      // :554 survives and an absent port stays absent.
      return builder.Uri.ToString();
    }
    catch (UriFormatException)
    {
      // Malformed device URIs do exist. Splice by hand rather than refusing to play.
      var separator = uri.IndexOf("://", StringComparison.Ordinal);
      if (separator < 0) return uri;
      var scheme = uri[..(separator + 3)];
      var rest = uri[(separator + 3)..];
      return $"{scheme}{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{rest}";
    }
  }

  /// <summary>The same URI with the password blanked, for anything shown on screen or logged.</summary>
  public static string Mask(string uri)
  {
    if (string.IsNullOrWhiteSpace(uri)) return uri;

    var separator = uri.IndexOf("://", StringComparison.Ordinal);
    if (separator < 0) return uri;

    var start = separator + 3;
    var at = uri.IndexOf('@', start);
    if (at < 0) return uri;

    var colon = uri.IndexOf(':', start);
    if (colon < 0 || colon > at) return uri;

    return string.Concat(uri.AsSpan(0, colon + 1), "•••", uri.AsSpan(at));
  }
}
