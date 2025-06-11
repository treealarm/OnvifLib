using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;

namespace OnvifLib
{
  public interface IOnvifServiceFactory<T> where T : class
  {
    static abstract string[] GetSupportedWsdls();
    static abstract Task<T?> CreateAsync(string url, CustomBinding binding, string username, string password, string wsdlKey);
  }
}
