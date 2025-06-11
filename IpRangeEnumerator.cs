using System.Collections;
using System.Net;
namespace OnvifLib
{
  public class IpRangeEnumerator : IEnumerable<IPAddress>
  {
    private readonly uint _start;
    private readonly uint _end;

    public IpRangeEnumerator(IPAddress startIp, IPAddress endIp)
    {
      if (startIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
          endIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
      {
        throw new ArgumentException("Only IPv4 addresses are supported.");
      }

      _start = IpToUint(startIp);
      _end = IpToUint(endIp);

      if (_start > _end)
      {
        throw new ArgumentException("Start IP must be less than or equal to End IP.");
      }
    }

    public IEnumerator<IPAddress> GetEnumerator()
    {
      for (uint ip = _start; ip <= _end; ip++)
      {
        yield return UintToIp(ip);
      }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static uint IpToUint(IPAddress ip)
    {
      byte[] bytes = ip.GetAddressBytes();
      if (BitConverter.IsLittleEndian)
        Array.Reverse(bytes); // в big-endian
      return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress UintToIp(uint ip)
    {
      byte[] bytes = BitConverter.GetBytes(ip);
      if (BitConverter.IsLittleEndian)
        Array.Reverse(bytes);
      return new IPAddress(bytes);
    }
  }
}
