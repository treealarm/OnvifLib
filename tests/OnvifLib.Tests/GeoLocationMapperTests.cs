using DeviceServiceReference;
using OnvifLib;

namespace OnvifLib.Tests;

// The generated geolocation types mark every value optional with a *Specified flag. A camera that
// reports no elevation reports elevation 0 with the flag off — that is "unknown", not sea level —
// so the mapper's whole job is to keep the two apart.
public class GeoLocationMapperTests
{
  private static LocationEntity Camera() => new()
  {
    Entity = "Device",
    Token = "cam1",
  };

  [Fact]
  public void MapsEveryReportedField()
  {
    var dto = OnvifGeoMapper.ToDto(new LocationEntity
    {
      Entity = "Device",
      Token = "cam1",
      Fixed = true,
      FixedSpecified = true,
      GeoSource = "urn:onvif:geo",
      AutoGeo = false,
      AutoGeoSpecified = true,
      GeoLocation = new GeoLocation
      {
        lat = 38.9923,
        latSpecified = true,
        lon = -76.9378,
        lonSpecified = true,
        elevation = 12.5f,
        elevationSpecified = true,
      },
      GeoOrientation = new GeoOrientation
      {
        yaw = 90,
        yawSpecified = true,
        pitch = -5,
        pitchSpecified = true,
        roll = 1.5f,
        rollSpecified = true,
      },
    });

    Assert.Equal("Device", dto.Entity);
    Assert.Equal("cam1", dto.Token);
    Assert.True(dto.Fixed);
    Assert.Equal("urn:onvif:geo", dto.GeoSource);
    Assert.False(dto.AutoGeo);
    Assert.Equal(new OnvifGeoLocation(38.9923, -76.9378, 12.5), dto.Location);
    Assert.Equal(new OnvifGeoOrientation(90, -5, 1.5f), dto.Orientation);
  }

  [Fact]
  public void ValuesTheCameraDidNotReportBecomeNull()
  {
    var dto = OnvifGeoMapper.ToDto(new LocationEntity
    {
      Entity = "Device",
      Token = "cam1",
      GeoLocation = new GeoLocation(),
      GeoOrientation = new GeoOrientation(),
    });

    Assert.Equal(new OnvifGeoLocation(null, null, null), dto.Location);
    Assert.Equal(new OnvifGeoOrientation(null, null, null), dto.Orientation);
    Assert.Null(dto.Fixed);
    Assert.Null(dto.AutoGeo);
    Assert.Null(dto.GeoSource);
  }

  [Fact]
  public void AnEntityWithNoPointAtAllHasNoLocation()
  {
    var dto = OnvifGeoMapper.ToDto(Camera());

    Assert.Null(dto.Location);
    Assert.Null(dto.Orientation);
  }

  [Fact]
  public void BuildsARequestWithTheSpecifiedFlagsTheCameraUnderstands()
  {
    var request = OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      "Device", "cam1", new OnvifGeoLocation(38.9923, -76.9378, 12.5)));

    Assert.Equal("Device", request.Entity);
    Assert.Equal("cam1", request.Token);
    Assert.True(request.GeoLocation.latSpecified);
    Assert.Equal(38.9923, request.GeoLocation.lat);
    Assert.True(request.GeoLocation.lonSpecified);
    Assert.Equal(-76.9378, request.GeoLocation.lon);
    Assert.True(request.GeoLocation.elevationSpecified);
    Assert.Equal(12.5f, request.GeoLocation.elevation);

    // Nothing was said about these, so nothing is claimed.
    Assert.Null(request.GeoOrientation);
    Assert.False(request.FixedSpecified);
    Assert.False(request.AutoGeoSpecified);
    Assert.Null(request.GeoSource);
  }

  [Fact]
  public void OmitsAnElevationThatWasNotGiven()
  {
    var request = OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      "Device", "cam1", new OnvifGeoLocation(38.9923, -76.9378)));

    Assert.False(request.GeoLocation.elevationSpecified);
  }

  [Fact]
  public void ARequestNeedsAnEntity()
  {
    Assert.Throws<ArgumentException>(() => OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      " ", "cam1", new OnvifGeoLocation(38.9923, -76.9378))));
  }

  [Fact]
  public void ARequestNeedsAPoint()
  {
    Assert.Throws<ArgumentException>(() => OnvifGeoMapper.ToRequest(new OnvifLocationEntity("Device", "cam1")));
    Assert.Throws<ArgumentException>(() => OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      "Device", "cam1", new OnvifGeoLocation(null, -76.9378))));
    Assert.Throws<ArgumentException>(() => OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      "Device", "cam1", new OnvifGeoLocation(38.9923, null))));
  }

  [Theory]
  [InlineData(90.1, 0)]
  [InlineData(-90.1, 0)]
  [InlineData(double.NaN, 0)]
  [InlineData(0, 180.1)]
  [InlineData(0, -180.1)]
  [InlineData(0, double.PositiveInfinity)]
  public void ARequestRefusesAPointOffTheEarth(double latitude, double longitude)
  {
    Assert.Throws<ArgumentException>(() => OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      "Device", "cam1", new OnvifGeoLocation(latitude, longitude))));
  }

  [Theory]
  [InlineData(90, 180)]
  [InlineData(-90, -180)]
  public void ThePolesAndTheAntimeridianAreOnTheEarth(double latitude, double longitude)
  {
    var request = OnvifGeoMapper.ToRequest(new OnvifLocationEntity(
      "Device", "cam1", new OnvifGeoLocation(latitude, longitude)));

    Assert.Equal(latitude, request.GeoLocation.lat);
    Assert.Equal(longitude, request.GeoLocation.lon);
  }
}
