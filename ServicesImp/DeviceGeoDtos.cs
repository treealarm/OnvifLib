using Dsr = DeviceServiceReference;

namespace OnvifLib
{
  // Geolocation: where the camera says it stands on the Earth. It lives on the Device Management
  // service and is reached through Camera (Camera.Geo.cs), not through a service object.

  /// <summary>
  /// A point reported for a location entity. Every coordinate is optional in the ONVIF schema —
  /// each arrives with its own *Specified flag — so a value the camera did not send is null here,
  /// not zero. Zero is a real place (the equator, the prime meridian).
  /// </summary>
  public record OnvifGeoLocation(double? Latitude, double? Longitude, double? ElevationMeters = null);

  /// <summary>A direction in degrees: yaw is a compass bearing (0 = north, clockwise), pitch and roll tilt.</summary>
  public record OnvifGeoOrientation(float? Yaw, float? Pitch, float? Roll);

  /// <summary>
  /// One entry of the camera's geolocation list. <paramref name="Entity"/> is "Device" for the
  /// camera itself; <paramref name="Token"/> names it. <paramref name="Fixed"/> and
  /// <paramref name="AutoGeo"/> are null when the camera did not say, and
  /// <paramref name="GeoSource"/> is null when it reported none.
  /// </summary>
  public record OnvifLocationEntity(
    string Entity,
    string Token,
    OnvifGeoLocation? Location = null,
    OnvifGeoOrientation? Orientation = null,
    bool? Fixed = null,
    string? GeoSource = null,
    bool? AutoGeo = null);

  /// <summary>
  /// Between the generated schema types and the DTOs. Kept internal so the *Specified handling is
  /// in one place and can be exercised by the unit tests.
  /// </summary>
  internal static class OnvifGeoMapper
  {
    internal static OnvifLocationEntity ToDto(Dsr.LocationEntity source) => new(
      source.Entity ?? string.Empty,
      source.Token ?? string.Empty,
      source.GeoLocation is { } geo
        ? new OnvifGeoLocation(
            geo.latSpecified ? geo.lat : null,
            geo.lonSpecified ? geo.lon : null,
            geo.elevationSpecified ? (double?)geo.elevation : null)
        : null,
      source.GeoOrientation is { } orientation
        ? new OnvifGeoOrientation(
            orientation.yawSpecified ? orientation.yaw : null,
            orientation.pitchSpecified ? orientation.pitch : null,
            orientation.rollSpecified ? orientation.roll : null)
        : null,
      source.FixedSpecified ? source.Fixed : null,
      string.IsNullOrEmpty(source.GeoSource) ? null : source.GeoSource,
      source.AutoGeoSpecified ? source.AutoGeo : null);

    /// <summary>
    /// Builds the request entry. A point needs a latitude and a longitude — a request without one
    /// is a caller bug, not an instruction the camera could act on — and both must be on the Earth.
    /// </summary>
    internal static Dsr.LocationEntity ToRequest(OnvifLocationEntity source)
    {
      if (string.IsNullOrWhiteSpace(source.Entity))
        throw new ArgumentException("A geolocation needs the entity it describes, usually \"Device\"", nameof(source));
      if (source.Location is not { } location)
        throw new ArgumentException("A geolocation needs a location to set", nameof(source));

      var latitude = location.Latitude ?? throw new ArgumentException("A geolocation needs a latitude", nameof(source));
      var longitude = location.Longitude ?? throw new ArgumentException("A geolocation needs a longitude", nameof(source));
      if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
        throw new ArgumentException($"Latitude {latitude} is outside -90..90", nameof(source));
      if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
        throw new ArgumentException($"Longitude {longitude} is outside -180..180", nameof(source));

      var geo = new Dsr.GeoLocation
      {
        lat = latitude,
        latSpecified = true,
        lon = longitude,
        lonSpecified = true,
      };

      if (location.ElevationMeters is { } elevation)
      {
        if (!double.IsFinite(elevation))
          throw new ArgumentException($"Elevation {elevation} is not a number", nameof(source));
        geo.elevation = (float)elevation;
        geo.elevationSpecified = true;
      }

      Dsr.GeoOrientation? orientation = null;
      if (source.Orientation is { } o)
      {
        orientation = new Dsr.GeoOrientation
        {
          yaw = o.Yaw ?? 0,
          yawSpecified = o.Yaw.HasValue,
          pitch = o.Pitch ?? 0,
          pitchSpecified = o.Pitch.HasValue,
          roll = o.Roll ?? 0,
          rollSpecified = o.Roll.HasValue,
        };
      }

      return new Dsr.LocationEntity
      {
        Entity = source.Entity,
        Token = source.Token,
        GeoLocation = geo,
        GeoOrientation = orientation,
        Fixed = source.Fixed ?? false,
        FixedSpecified = source.Fixed.HasValue,
        GeoSource = string.IsNullOrEmpty(source.GeoSource) ? null : source.GeoSource,
        AutoGeo = source.AutoGeo ?? false,
        AutoGeoSpecified = source.AutoGeo.HasValue,
      };
    }
  }
}
