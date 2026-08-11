using System.Net.Sockets;
using System.ServiceModel;
using System.ServiceModel.Security;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Turns a WCF/ONVIF exception into one line a person can act on.
/// </summary>
/// <remarks>
/// Deliberately duplicated from OnvifLib.Probe rather than shared: the two samples are meant to
/// be readable and copyable one at a time, and a third "common" project to hold thirty lines
/// would make both harder to lift out of the repository.
/// </remarks>
public static class OnvifError
{
  public static string Describe(Exception ex)
  {
    // WCF wraps aggressively (CommunicationException → WebException → SocketException), so the
    // whole chain is searched for the most specific thing worth reporting.
    foreach (var e in Chain(ex))
    {
      switch (e)
      {
        case OperationCanceledException:
          return "cancelled";

        // ONVIF puts the real reason (ter:NotAuthorized, ter:ActionNotSupported, …) in the fault,
        // not in the exception message, which is a generic "The creator of this fault…".
        case FaultException fault:
          var reason = fault.CreateMessageFault().Reason?.ToString() ?? fault.Message;
          var code = fault.Code?.SubCode?.Name ?? fault.Code?.Name;
          return code is null ? $"SOAP fault: {reason}" : $"SOAP fault {code}: {reason}";

        case MessageSecurityException:
          return "authentication failed — check the user name and password";

        // Must come before CommunicationException: cameras report a rejected credential in
        // wildly different ways, and the common one — an HTTP 400 whose body says "Not
        // Authorized" — arrives as a ProtocolException, which would otherwise be described as a
        // generic communication error and read like a network problem.
        case { } when LooksLikeAuthFailure(e.Message):
          return "not authorized — check the user name and password";

        case EndpointNotFoundException:
          return "endpoint not found — this service is not served at that address";

        case TimeoutException:
          return "timed out — raise the timeout in the connection bar if the camera is slow";

        case SocketException socket:
          return $"network error: {socket.SocketErrorCode}";

        case HttpRequestException http:
          return http.StatusCode is { } status ? $"HTTP {(int)status} {status}" : $"network error: {http.Message}";

        // The base of the WCF family, so it must come after the specific ones above.
        case CommunicationException:
          return $"communication error: {e.Message}";

        // The library uses this for "client not initialized" and for refusing a read-modify-write.
        case InvalidOperationException:
          return e.Message;
      }
    }

    return $"{ex.GetType().Name}: {ex.Message}";
  }

  private static IEnumerable<Exception> Chain(Exception ex)
  {
    for (Exception? e = ex; e != null; e = e.InnerException) yield return e;
  }

  private static bool LooksLikeAuthFailure(string message) =>
    message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
    || message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
    || message.Contains("not Authorized", StringComparison.OrdinalIgnoreCase);
}
