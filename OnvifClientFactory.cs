using System.ServiceModel.Channels;
using System.ServiceModel;


namespace OnvifLib
{
  public class OnvifClientFactory
  {
    private SecurityToken? _securityToken = null;
    public TClient CreateClient<TClient, TInterface>(
        EndpointAddress endpoint,
        CustomBinding binding,
        string username,
        string password)
        where TInterface : class
        where TClient : ClientBase<TInterface>, TInterface
    {
      var clientInspector = new CustomMessageInspector();
      var behavior = new CustomEndpointBehavior(clientInspector);

      var client = (TClient)Activator.CreateInstance(typeof(TClient), binding, endpoint)!;

      // Установка данных авторизации
      client.ClientCredentials.UserName.UserName = username;
      client.ClientCredentials.UserName.Password = password;

      client.ClientCredentials.HttpDigest.ClientCredential.UserName = username;
      client.ClientCredentials.HttpDigest.ClientCredential.Password = password;

      if (_securityToken != null)
        clientInspector.Headers.Add(new DigestSecurityHeader(client.ClientCredentials.HttpDigest.ClientCredential, _securityToken));

      client.Endpoint.EndpointBehaviors.Add(behavior);

      return client;
    }

    public void SetSecurityToken(SecurityToken token)
    {
      _securityToken = token;
    }
  }

}
