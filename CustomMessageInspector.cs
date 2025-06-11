using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;
using System.ServiceModel;
using System.ServiceModel.Description;

namespace OnvifLib
{
  public class CustomMessageInspector : IClientMessageInspector
  {
    private const string WsseNamespace = @"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public List<MessageHeader> Headers { get; } = new List<MessageHeader>();

    public void AfterReceiveReply(ref Message reply, object correlationState)
    {
      // Логгирование ответа
      Console.WriteLine("Received Reply: ");
      Console.WriteLine(reply.ToString());

      foreach (var header in reply.Headers)
        if (header.Name.IndexOf("Security", StringComparison.InvariantCulture) != -1 && header.Namespace.Contains(WsseNamespace))
          reply.Headers.UnderstoodHeaders.Add(header);
    }

    public object BeforeSendRequest(ref Message request, IClientChannel channel)
    {
      // Логгирование запроса
      Console.WriteLine("Sending Request: ");
      Console.WriteLine(request.ToString());
      if (Headers == null)
        return request;

      foreach (var header in Headers)
        request.Headers.Insert(0, header);

      return request;
    }
  }

  public class CustomEndpointBehavior : IEndpointBehavior
  {
    private readonly IClientMessageInspector _clientInspector;

    public CustomEndpointBehavior(IClientMessageInspector clientInspector)
    {
      if (clientInspector == null)
        throw new ArgumentNullException(nameof(clientInspector));

      _clientInspector = clientInspector;
    }

    public void Validate(ServiceEndpoint endpoint)
    {
    }

    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters)
    {
    }

    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
    {
    }

    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime)
    {
      clientRuntime.ClientMessageInspectors.Add(_clientInspector);
    }
  }

}
