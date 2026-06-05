using System;
using System.Collections.Concurrent;
using System.ServiceModel.Channels;
using System.Threading.Tasks;

namespace OnvifLib
{
  public class OnvifServiceCache
  {
    private readonly ConcurrentDictionary<Type, (object Service, DateTime CreatedAt)> _serviceCache
        = new ConcurrentDictionary<Type, (object, DateTime)>();

    private readonly TimeSpan _serviceTtl;

    private readonly CustomBindingProvider _bindingProvider;
    private readonly string _username;
    private readonly string _password;
    private readonly Func<SecurityToken>? _tokenFactory;
    private readonly IOnvifLogger? _logger;

    public OnvifServiceCache(CustomBindingProvider bindingProvider, string username, string password, Func<SecurityToken>? tokenFactory = null, TimeSpan? ttl = null, IOnvifLogger? logger = null)
    {
      _bindingProvider = bindingProvider;
      _username = username;
      _password = password;
      _tokenFactory = tokenFactory;
      _serviceTtl = ttl ?? TimeSpan.FromMinutes(10);
      _logger = logger;
    }

    public async Task<T?> GetServiceAsync<T>(Dictionary<string, string> services)
        where T : class, IOnvifServiceFactory<T>
    {
      var type = typeof(T);

      // Check cache
      if (_serviceCache.TryGetValue(type, out var cached))
      {
        if (DateTime.UtcNow - cached.CreatedAt < _serviceTtl)
        {
          return cached.Service as T;
        }
      }

      // Create a new service via OnvifServiceSelector using the current binding
      var service = await OnvifServiceSelector.TryCreateService<T>(services, _bindingProvider.Current, _username, _password, _tokenFactory, _logger);

      if (service != null)
      {
        _serviceCache.AddOrUpdate(type, (service, DateTime.UtcNow), (_, _) => (service, DateTime.UtcNow));
      }

      return service;
    }

    /// <summary>
    /// Invalidate cached instance for a specific service type.
    /// </summary>
    public void Invalidate<T>() where T : class
    {
      _serviceCache.TryRemove(typeof(T), out _);
    }

    /// <summary>
    /// Clear the entire cache.
    /// </summary>
    public void Clear()
    {
      _serviceCache.Clear();
    }
  }
}
