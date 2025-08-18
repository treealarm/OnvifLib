using System.ServiceModel.Channels;


namespace OnvifLib
{
  public static class OnvifServiceSelector
  {
    public static async Task<T?> TryCreateService<T>(
      Dictionary<string, string> services,
      CustomBinding binding,
      string username,
      string password)
      where T : class, IOnvifServiceFactory<T>
    {
      foreach (var wsdl in T.GetSupportedWsdls())
      {
        if (services.TryGetValue(wsdl, out var url))
        {
          try
          {
            var instance = await T.CreateAsync(url, binding, username, password, wsdl);
            if (instance != null)
              return instance;
          }
          catch(Exception ex) 
          {
            Console.WriteLine(ex.ToString());
          }
        }
      }

      return null;
    }
  }
}
