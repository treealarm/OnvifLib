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

    private readonly CustomBinding _binding;
    private readonly string _username;
    private readonly string _password;

    public OnvifServiceCache(CustomBinding binding, string username, string password, TimeSpan? ttl = null)
    {
      _binding = binding;
      _username = username;
      _password = password;
      _serviceTtl = ttl ?? TimeSpan.FromMinutes(10);
    }

    public async Task<T?> GetServiceAsync<T>(Dictionary<string, string> services)
        where T : class, IOnvifServiceFactory<T>
    {
      var type = typeof(T);

      // Проверяем кэш
      if (_serviceCache.TryGetValue(type, out var cached))
      {
        if (DateTime.UtcNow - cached.CreatedAt < _serviceTtl)
        {
          return cached.Service as T;
        }
      }

      // Создаём новый сервис через OnvifServiceSelector
      var service = await OnvifServiceSelector.TryCreateService<T>(services, _binding, _username, _password);

      if (service != null)
      {
        _serviceCache.AddOrUpdate(type, (service, DateTime.UtcNow), (_, _) => (service, DateTime.UtcNow));
      }

      return service;
    }

    /// <summary>
    /// Принудительно сбросить кэш конкретного сервиса
    /// </summary>
    public void Invalidate<T>() where T : class
    {
      _serviceCache.TryRemove(typeof(T), out _);
    }

    /// <summary>
    /// Очистить весь кэш
    /// </summary>
    public void Clear()
    {
      _serviceCache.Clear();
    }
  }
}
