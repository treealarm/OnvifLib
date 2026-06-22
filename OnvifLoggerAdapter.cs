using Microsoft.Extensions.Logging;

namespace OnvifLib;

/// Forwards OnvifLib's logging abstraction to a host's ILogger (Serilog → file).
public sealed class OnvifLoggerAdapter(ILogger logger) : IOnvifLogger
{
  public void Debug(string message) => logger.LogDebug("{OnvifMessage}", message);
  public void Info(string message) => logger.LogInformation("{OnvifMessage}", message);
  public void Warning(string message) => logger.LogWarning("{OnvifMessage}", message);
  public void Error(string message) => logger.LogError("{OnvifMessage}", message);
}
