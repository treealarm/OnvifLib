using System.Net.Sockets;
using System.ServiceModel;

namespace OnvifLib
{
  /// How a failed call is written to the log.
  internal static class OnvifErrors
  {
    /// The exception as a log line should carry it. A camera that cannot be reached is an ordinary
    /// state of the network: the message names the address and the reason, and the stack is the same
    /// few kilobytes every time, repeated as often as the camera is asked. Anything else keeps its
    /// stack, which is where a bug would show.
    public static string Describe(Exception ex) =>
      IsUnreachable(ex) ? $"{ex.GetType().Name}: {ex.Message}" : ex.ToString();

    private static bool IsUnreachable(Exception ex)
    {
      // WCF wraps the transport's failure (CommunicationException -> HttpRequestException ->
      // SocketException), so walk the chain as IsAuthError does.
      for (var e = ex; e != null; e = e.InnerException)
      {
        switch (e)
        {
          case SocketException:
          case TimeoutException:
          case EndpointNotFoundException:
          // No status code: the request never got an answer, as opposed to an HTTP error from it.
          case HttpRequestException { StatusCode: null }:
            return true;
        }
      }
      return false;
    }
  }
}
