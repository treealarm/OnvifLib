using System.ServiceModel.Channels;

namespace OnvifLib
{
  public class OnvifServiceBase : IDisposable
  {
    protected readonly string _url;
    protected readonly string _username;
    protected readonly string _password;
    protected readonly CustomBinding _binding;
    protected readonly string _profile;
    protected readonly OnvifClientFactory _onvifClientFactory;
    protected OnvifServiceBase(string url, CustomBinding binding, string username, string password, string profile)
    {
      _url = url;
      _binding = binding;
      _username = username;
      _password = password;
      _profile = profile;
      _onvifClientFactory = new OnvifClientFactory();
    }
    public virtual void Dispose() { }
  }
}
