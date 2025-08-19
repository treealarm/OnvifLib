using OnvifLib;
using System.Collections.Concurrent;
using System.Net;
namespace OnvifLib
{
  public static class CameraScanner
  {
    public static async Task<bool> ScanAsync(
        string ipStart,
        string ipEnd,
        List<int> ports,
        List<(string username, string password)> credentials,
        Func<int, string, Task> onProgress,
        Func<Camera, object, Task> onCameraDiscovered,
        CancellationToken token,
        List<Camera> existing,
        object context)
    {
      var startIp = IPAddress.Parse(ipStart);
      var endIp = IPAddress.Parse(ipEnd);
      var ipRange = new IpRangeEnumerator(startIp, endIp).ToList();

      var allTasks = new ConcurrentBag<Task>();
      var totalSteps = ipRange.Count * ports.Count * credentials.Count;
      var completedSteps = 0;
      var progressLock = new object();
      var lastReportedProgress = 0;

      var existingUrls = existing.Select(c => c.Url).ToHashSet();


      // Параллельная обработка всех комбинаций
      await Parallel.ForEachAsync(ipRange, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = token }, async (ip, ct) =>
      {
        foreach (var port in ports)
        {
          foreach (var cred in credentials)
          {
            ct.ThrowIfCancellationRequested();

            var url = Camera.CreateUrl(ip.ToString(), port);
            if (existingUrls.Contains(url))
            {
              var existingCam = existing.FirstOrDefault(c => c.Url == url);
              if (existingCam != null && await existingCam.IsAlive())
                continue;
            }

            var cam = Camera.Create(ip.ToString(), port, cred.username, cred.password);
            if (cam == null || !await cam.IsAlive())
              continue;

            await onCameraDiscovered(cam, context);
          }

          lock (progressLock)
          {
            completedSteps += credentials.Count;
            int progress = (int)(completedSteps * 100.0 / totalSteps);
            if (progress != lastReportedProgress)
            {
              lastReportedProgress = progress;
              _ = onProgress(progress, $"Scanning {ip}");
            }
          }
        }
      });

      await onProgress(100, "finished");
      return true;
    }
  }
  }