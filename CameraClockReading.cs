namespace OnvifLib;

/// <summary>
/// One measurement of a camera's clock against ours.
/// </summary>
/// <remarks>
/// A camera's on-board archive is indexed in the camera's own clock, which is frequently not ours.
/// Every time we hand the device a moment (a search window, a replay position) it must be expressed
/// in that clock, and everything the device tells us has to be brought back into ours before it
/// touches the archive.
///
/// There is no way to read this out of the video: the RTSP replay stream only carries timestamps
/// relative to the seek point, and the absolute markers that do exist (RTCP sender reports, the
/// ONVIF replay RTP header extension) are in the camera's clock as well — and libavformat does not
/// expose either. So the offset is measured out of band, via GetSystemDateAndTime.
/// </remarks>
/// <param name="CameraUtc">UTC as the camera reports it.</param>
/// <param name="ServerUtc">Our UTC at the midpoint of the request, which is what CameraUtc is compared against.</param>
/// <param name="RoundTrip">Duration of the call; the measurement is accurate to about half of it.</param>
public sealed record CameraClockReading(DateTime CameraUtc, DateTime ServerUtc, TimeSpan RoundTrip)
{
  /// <summary>camera − server. Positive means the camera runs ahead of us.</summary>
  public TimeSpan Offset => CameraUtc - ServerUtc;
}
