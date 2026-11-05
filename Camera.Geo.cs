using OnvifLib;

// Geolocation: where the camera says it stands on the Earth. It lives on the Device Management
// service, so both calls are made against the device endpoint rather than through a service object.
public partial class Camera
{
  /// <summary>
  /// The camera's geolocation entries, or an empty list when it reports none. An entry that names
  /// no point is reported with <see cref="OnvifLocationEntity.Location"/> null — that is a camera
  /// that knows the entity but not where it is, not an error.
  /// </summary>
  public async Task<List<OnvifLocationEntity>> GetGeoLocationAsync()
  {
    using var device = await GetDevice();
    var resp = await device.GetGeoLocationAsync();
    return (resp?.Location ?? []).Select(OnvifGeoMapper.ToDto).ToList();
  }

  /// <summary>
  /// Puts an entity — normally the camera itself, <c>"Device"</c> — at a place on the Earth.
  /// </summary>
  /// <remarks>
  /// The camera may refuse: a device mounted on a moving vehicle answers ActionNotSupported,
  /// because its place is decided by the vehicle and only reported through
  /// <see cref="GetGeoLocationAsync"/>.
  /// </remarks>
  public async Task SetGeoLocationAsync(OnvifLocationEntity location)
  {
    using var device = await GetDevice();
    await device.SetGeoLocationAsync([OnvifGeoMapper.ToRequest(location)]);
  }
}
