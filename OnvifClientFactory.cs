using System;
using System.Collections.Generic;

using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel;


namespace OnvifLib
{
  public static class OnvifClientFactory
  {
    public static TClient CreateClient<TClient, TInterface>(
        string url,
        CustomBinding binding,
        string username,
        string password)
        where TInterface : class
        where TClient : ClientBase<TInterface>, TInterface
    {
      var clientInspector = new CustomMessageInspector();
      var behavior = new CustomEndpointBehavior(clientInspector);

      var endpoint = new EndpointAddress(url);
      var client = (TClient)Activator.CreateInstance(typeof(TClient), binding, endpoint)!;

      // Установка данных авторизации
      client.ClientCredentials.UserName.UserName = username;
      client.ClientCredentials.UserName.Password = password;

      client.ClientCredentials.HttpDigest.ClientCredential.UserName = username;
      client.ClientCredentials.HttpDigest.ClientCredential.Password = password;

      client.Endpoint.EndpointBehaviors.Add(behavior);

      return client;
    }
  }

}
