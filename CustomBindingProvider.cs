using System.Net;
using System.ServiceModel.Channels;
using System.Text;

namespace OnvifLib
{
  /// <summary>
  /// Holds the active CustomBinding and can switch from Anonymous to Digest once
  /// when a camera rejects WS-Security-only auth with a 401.
  /// </summary>
  public class CustomBindingProvider
  {
    private CustomBinding _binding;
    private readonly double _timeout;

    public CustomBindingProvider(double timeout)
    {
      _timeout = timeout;
      _binding = Build(AuthenticationSchemes.Anonymous);
    }

    public CustomBinding Current => _binding;

    public bool IsDigest => _binding.Elements
      .OfType<HttpTransportBindingElement>()
      .FirstOrDefault()?.AuthenticationScheme == AuthenticationSchemes.Digest;

    /// <summary>Switches to Digest transport; no-op if already Digest.</summary>
    public bool SwitchToDigest()
    {
      if (IsDigest) return false;
      _binding = Build(AuthenticationSchemes.Digest);
      return true;
    }

    private CustomBinding Build(AuthenticationSchemes auth)
    {
      var b = new CustomBinding(
        new TextMessageEncodingBindingElement(MessageVersion.Soap12WSAddressing10, Encoding.UTF8),
        new HttpTransportBindingElement { AuthenticationScheme = auth }
      );
      b.OpenTimeout = b.CloseTimeout = b.SendTimeout = b.ReceiveTimeout =
        TimeSpan.FromSeconds(_timeout);
      return b;
    }
  }
}
