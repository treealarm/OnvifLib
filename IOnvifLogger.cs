namespace OnvifLib
{
  /// Logging abstraction owned by OnvifLib so the library stays independent of any
  /// concrete logging framework. Hosts (services) provide an implementation that
  /// forwards to their own logger; pass it into Camera.Create.
  public interface IOnvifLogger
  {
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
  }
}
