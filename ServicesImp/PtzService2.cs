using PtzServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  public class PtzService2 : OnvifServiceBase, IOnvifServiceFactory<PtzService2>
  {
    public const string WSDL_V20 = "http://www.onvif.org/ver20/ptz/wsdl";
    private PTZClient? _ptzClient;
    protected PtzService2(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20 };
    }
    public static async Task<PtzService2?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new PtzService2(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();
      _ptzClient = _onvifClientFactory.CreateClient<PTZClient, PTZ>(
        new EndpointAddress(_url), 
        _binding, 
        _username, 
        _password);
      await _ptzClient.OpenAsync();
    }

    public bool SupportedCaps() => true;

    public async Task<PtzCapabilities> GetCapabilitiesAsync(string profileToken)
    {
      if (_ptzClient == null)
        return new PtzCapabilities(false, false, false);

      try
      {
        // GetConfigurations is more reliable than GetNodes:
        // DefaultXxxSpace is null when the camera hasn't actually configured that move mode,
        // whereas GetNodes.SupportedPTZSpaces can list spaces the camera doesn't truly implement.
        var cfgResp = await _ptzClient.GetConfigurationsAsync();
        var configs = cfgResp.PTZConfiguration;
        if (configs == null || configs.Length == 0)
          return new PtzCapabilities(false, false, false);

        return new PtzCapabilities(
          AbsoluteMove:   configs.Any(c => !string.IsNullOrEmpty(c.DefaultAbsolutePantTiltPositionSpace)),
          RelativeMove:   configs.Any(c => !string.IsNullOrEmpty(c.DefaultRelativePanTiltTranslationSpace)),
          ContinuousMove: configs.Any(c => !string.IsNullOrEmpty(c.DefaultContinuousPanTiltVelocitySpace))
        );
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetCapabilities failed for {_url}: {OnvifErrors.Describe(ex)}");
        return new PtzCapabilities(false, false, false);
      }
    }
    /// <summary>Which coordinate spaces the camera declares, and over what ranges.</summary>
    /// <remarks>
    /// <see cref="GetCapabilitiesAsync"/> reduces all of this to three booleans, losing both the
    /// space URI and the range. A caller that wants to name a space explicitly, or to know whether
    /// a camera answers in -1..1 or in degrees, needs the detail -- so it is a separate call rather
    /// than a wider capabilities record every existing caller would have to be updated for.
    /// <para>
    /// Declared, not verified. A camera can list a space and ignore commands in it; only moving the
    /// camera and watching the picture settles that.
    /// </para>
    /// </remarks>
    public async Task<PtzSpaces?> GetSpacesAsync(string configurationToken = "")
    {
      if (_ptzClient == null)
        return null;

      try
      {
        var token = configurationToken;
        if (string.IsNullOrEmpty(token))
        {
          // No token given: take the camera's first configuration, which is what a single-headed
          // camera has and all this library has ever assumed elsewhere.
          var configs = await _ptzClient.GetConfigurationsAsync();
          token = configs?.PTZConfiguration?.FirstOrDefault()?.token ?? string.Empty;
          if (string.IsNullOrEmpty(token))
            return null;
        }

        var options = await _ptzClient.GetConfigurationOptionsAsync(token);
        var spaces = options?.Spaces;
        if (spaces == null)
          return null;

        return new PtzSpaces(
          AbsolutePanTilt:   Describe(spaces.AbsolutePanTiltPositionSpace),
          RelativePanTilt:   Describe(spaces.RelativePanTiltTranslationSpace),
          ContinuousPanTilt: Describe(spaces.ContinuousPanTiltVelocitySpace));
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetConfigurationOptions failed for {_url}: {OnvifErrors.Describe(ex)}");
        return null;
      }
    }

    // The first declared space of each kind. Cameras may list several; the first is the one a
    // command with no space attribute lands in, so it is the one worth reporting.
    private static PtzSpace? Describe(Space2DDescription[]? spaces)
    {
      var space = spaces?.FirstOrDefault();
      return space == null
        ? null
        : new PtzSpace(
            space.URI ?? string.Empty,
            space.XRange?.Min ?? 0f, space.XRange?.Max ?? 0f,
            space.YRange?.Min ?? 0f, space.YRange?.Max ?? 0f);
    }

    /// <param name="space">
    /// The coordinate space to command in. Null leaves the attribute off the wire, so the camera
    /// applies its own DefaultAbsolutePantTiltPositionSpace -- which is what every caller of this
    /// library has always relied on, and stays the default here for that reason. Name one when the
    /// camera's default is not the space the values are in.
    /// </param>
    public async Task AbsoluteMoveAsync(string profileToken, float panTiltX, float panTiltY, float zoom = 0f, float speedPanTilt = 0.5f, float speedZoom = 0.5f, string? space = null)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      var position = new PTZVector
      {
        PanTilt = new Vector2D { x = panTiltX, y = panTiltY, space = space },
        Zoom = new Vector1D { x = zoom }
      };

      var speed = new PTZSpeed
      {
        PanTilt = new Vector2D { x = speedPanTilt, y = speedPanTilt },
        Zoom = new Vector1D { x = speedZoom }
      };

      await _ptzClient.AbsoluteMoveAsync(profileToken, position, speed);
    }

    public async Task ContinuousMoveAsync(
      string profileToken, 
      float panTiltX, 
      float panTiltY, 
      float zoom = 0f, 
      string timeout = "PT3S")
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      var velocity = new PTZSpeed
      {
        PanTilt = new Vector2D { x = panTiltX, y = panTiltY },
        Zoom = new Vector1D { x = zoom }
      };

      await _ptzClient.ContinuousMoveAsync(profileToken, velocity, timeout);
    }
    /// <param name="space">
    /// The translation space to command in. Null leaves the attribute off the wire and the camera
    /// applies its own default -- see <see cref="AbsoluteMoveAsync"/>.
    /// </param>
    public async Task RelativeMoveAsync(
      string profileToken, 
      float panTiltX, 
      float panTiltY, 
      float zoom = 0f, 
      float speedPanTilt = 0.5f, 
      float speedZoom = 0.5f,
      string? space = null)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      var translation = new PTZVector
      {
        PanTilt = new Vector2D { x = panTiltX, y = panTiltY, space = space },
        Zoom = new Vector1D { x = zoom }
      };

      var speed = new PTZSpeed
      {
        PanTilt = new Vector2D { x = speedPanTilt, y = speedPanTilt },
        Zoom = new Vector1D { x = speedZoom }
      };

      await _ptzClient.RelativeMoveAsync(profileToken, translation, speed);
    }


    public async Task StopAsync(string profileToken, bool panTilt = true, bool zoom = true)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      await _ptzClient.StopAsync(profileToken, panTilt, zoom);
    }

    public async Task<List<PtzPresetDto>> GetPresetsAsync(string profileToken)
    {
      if (_ptzClient == null) return [];
      try
      {
        var resp = await _ptzClient.GetPresetsAsync(profileToken);
        return (resp.Preset ?? [])
          .Select(p => new PtzPresetDto(p.token ?? string.Empty, p.Name ?? string.Empty))
          .ToList();
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetPresets failed for {_url}: {ex.Message}");
        return [];
      }
    }

    public async Task<string> SetPresetAsync(string profileToken, string presetName, string presetToken)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");
      var resp = await _ptzClient.SetPresetAsync(new SetPresetRequest
      {
        ProfileToken = profileToken,
        PresetName   = presetName,
        PresetToken  = string.IsNullOrEmpty(presetToken) ? null : presetToken,
      });
      return resp.PresetToken ?? string.Empty;
    }

    /// <summary>Goes to a preset at half speed — the speed this library has always used.</summary>
    public Task GotoPresetAsync(string profileToken, string presetToken) =>
      GotoPresetAsync(profileToken, presetToken, 0.5f, 0.5f);

    /// <param name="speedPanTilt">
    /// Pan/tilt speed in the camera's speed space (usually 0..1). Null for both speeds leaves the
    /// Speed element off the wire, and the camera uses its own default speed.
    /// </param>
    /// <param name="profileToken">The media profile the PTZ configuration is attached to.</param>
    /// <param name="presetToken">The preset to go to.</param>
    /// <param name="speedZoom">Zoom speed; null leaves it to the camera.</param>
    public async Task GotoPresetAsync(string profileToken, string presetToken, float? speedPanTilt, float? speedZoom = null)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");
      await _ptzClient.GotoPresetAsync(profileToken, presetToken, MakeSpeed(speedPanTilt, speedZoom));
    }

    /// <summary>Moves to the home position. Null speeds let the camera use its default.</summary>
    public async Task GotoHomePositionAsync(string profileToken, float? speedPanTilt = null, float? speedZoom = null)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");
      await _ptzClient.GotoHomePositionAsync(profileToken, MakeSpeed(speedPanTilt, speedZoom));
    }

    /// <summary>Stores the current position as home.</summary>
    /// <remarks>
    /// Overwrites the stored home with no way back, and faults (<c>ter:CannotOverwriteHome</c>)
    /// on a node whose <see cref="PtzNodeInfo.FixedHomePosition"/> is set.
    /// </remarks>
    public async Task SetHomePositionAsync(string profileToken)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");
      await _ptzClient.SetHomePositionAsync(profileToken);
    }

    /// <summary>
    /// Sends one of the node's auxiliary commands — wiper, washer, IR lamp, heater — and returns
    /// the camera's reply.
    /// </summary>
    /// <remarks>
    /// The command strings are the camera's own, taken from <see cref="PtzNodeInfo.AuxiliaryCommands"/>
    /// (ONVIF suggests <c>tt:Wiper|On</c>-style names but does not mandate them). These drive
    /// physical hardware, so a caller should confirm before sending.
    /// </remarks>
    public async Task<string> SendAuxiliaryCommandAsync(string profileToken, string command)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");
      return await _ptzClient.SendAuxiliaryCommandAsync(profileToken, command) ?? string.Empty;
    }

    /// <summary>The PTZ nodes — the mechanics behind the configurations, and what each can do.</summary>
    /// <remarks>
    /// Empty when the camera will not answer. GetNodes faults on cameras that advertise the PTZ
    /// WSDL with no motor behind it, and that is not worth an exception to every caller that only
    /// wants to know whether a home button makes sense.
    /// </remarks>
    public async Task<List<PtzNodeInfo>> GetNodesAsync()
    {
      if (_ptzClient == null) return [];
      try
      {
        var resp = await _ptzClient.GetNodesAsync();
        return (resp?.PTZNode ?? []).Select(n =>
        {
          var spaces = n.SupportedPTZSpaces;
          return new PtzNodeInfo(
            n.token ?? string.Empty,
            n.Name ?? string.Empty,
            n.HomeSupported,
            n.FixedHomePositionSpecified && n.FixedHomePosition,
            n.MaximumNumberOfPresets,
            (n.AuxiliaryCommands ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList(),
            Uris(spaces?.AbsolutePanTiltPositionSpace),
            Uris(spaces?.AbsoluteZoomPositionSpace),
            Uris(spaces?.RelativePanTiltTranslationSpace),
            Uris(spaces?.RelativeZoomTranslationSpace),
            Uris(spaces?.ContinuousPanTiltVelocitySpace),
            Uris(spaces?.ContinuousZoomVelocitySpace),
            Uris(spaces?.PanTiltSpeedSpace),
            Uris(spaces?.ZoomSpeedSpace),
            n.Extension?.SupportedPresetTour?.MaximumNumberOfPresetTours ?? 0,
            n.GeoMoveSpecified && n.GeoMove);
        }).ToList();
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetNodes failed for {_url}: {ex.Message}");
        return [];
      }
    }

    private static IReadOnlyList<string> Uris(Space2DDescription[]? spaces) =>
      (spaces ?? []).Select(s => s.URI ?? string.Empty).Where(u => u.Length > 0).ToList();

    private static IReadOnlyList<string> Uris(Space1DDescription[]? spaces) =>
      (spaces ?? []).Select(s => s.URI ?? string.Empty).Where(u => u.Length > 0).ToList();

    // Null for both leaves the Speed element out entirely, which is the only way to ask for the
    // camera's own default — a zero speed is "don't move" on some firmware.
    private static PTZSpeed? MakeSpeed(float? panTilt, float? zoom) =>
      panTilt is null && zoom is null
        ? null
        : new PTZSpeed
          {
            PanTilt = panTilt is { } pt ? new Vector2D { x = pt, y = pt } : null,
            Zoom = zoom is { } z ? new Vector1D { x = z } : null,
          };

    public async Task RemovePresetAsync(string profileToken, string presetToken)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");
      await _ptzClient.RemovePresetAsync(profileToken, presetToken);
    }

    /// <summary>Where the camera is pointing, and whether it is still on its way there.</summary>
    /// <remarks>
    /// The counterpart the library was missing: it could command all three kinds of move and had no
    /// way to read the result, which is why the probe skipped AbsoluteMove outright -- there was no
    /// way to put the camera back. Reading the position is also what lets a position be recorded at
    /// all, so anything that has to learn a correspondence between a picture and an aim needs this.
    /// <para>
    /// Null means the camera would not answer. That is not the same as a camera reporting zeroes:
    /// firmware exists that answers GetStatus with constants and IDLE while the motor is plainly
    /// turning, so a caller that cares must verify by moving the camera and watching the numbers,
    /// not by the call succeeding.
    /// </para>
    /// </remarks>
    public async Task<PtzStatus?> GetStatusAsync(string profileToken)
    {
      if (_ptzClient == null)
        return null;

      try
      {
        var status = await _ptzClient.GetStatusAsync(profileToken);
        if (status == null)
          return null;

        return new PtzStatus(
          Pan:  status.Position?.PanTilt?.x ?? 0f,
          Tilt: status.Position?.PanTilt?.y ?? 0f,
          Zoom: status.Position?.Zoom?.x ?? 0f,
          // Absent MoveStatus is not "moving": a camera that reports no move state is one that
          // cannot be waited on, and treating that as perpetual motion would hang every caller.
          Moving:
            status.MoveStatus?.PanTilt == MoveStatus.MOVING ||
            status.MoveStatus?.Zoom == MoveStatus.MOVING,
          UtcTime: status.UtcTime);
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetStatus failed for {_url}: {OnvifErrors.Describe(ex)}");
        return null;
      }
    }

    public override void Dispose()
    {
      try { _ptzClient?.Close(); } catch { }
      base.Dispose();
    }
  }

  public record PtzCapabilities(bool AbsoluteMove, bool RelativeMove, bool ContinuousMove);

  /// <summary>One declared coordinate space and the range it spans.</summary>
  public record PtzSpace(string Uri, float MinX, float MaxX, float MinY, float MaxY);

  /// <summary>The pan/tilt spaces a camera declares for each kind of move.</summary>
  /// <remarks>
  /// Null for a kind the camera declares no space for. Declaring one is not a promise to obey it:
  /// a camera with an absolute space that ignores AbsoluteMove is a real thing, and the only test
  /// that settles it is moving the camera and watching the picture.
  /// </remarks>
  public record PtzSpaces(PtzSpace? AbsolutePanTilt, PtzSpace? RelativePanTilt, PtzSpace? ContinuousPanTilt);
  public record PtzPresetDto(string Token, string Name);

  /// <summary>One PTZ node: the mechanics a configuration drives, and its limits.</summary>
  /// <param name="HomeSupported">GotoHomePosition does something on this node.</param>
  /// <param name="FixedHomePosition">Home is set at the factory; SetHomePosition will fault.</param>
  /// <param name="AuxiliaryCommands">The strings SendAuxiliaryCommandAsync accepts, as the camera names them.</param>
  /// <param name="MaximumNumberOfPresetTours">Zero when the camera did not say.</param>
  /// <param name="Token">The node token.</param>
  /// <param name="Name">Display name.</param>
  /// <param name="MaximumNumberOfPresets">How many presets the node stores.</param>
  /// <param name="AbsolutePanTiltSpaces">URIs of the absolute pan/tilt spaces.</param>
  /// <param name="AbsoluteZoomSpaces">URIs of the absolute zoom spaces.</param>
  /// <param name="RelativePanTiltSpaces">URIs of the relative pan/tilt spaces.</param>
  /// <param name="RelativeZoomSpaces">URIs of the relative zoom spaces.</param>
  /// <param name="ContinuousPanTiltSpaces">URIs of the continuous pan/tilt spaces.</param>
  /// <param name="ContinuousZoomSpaces">URIs of the continuous zoom spaces.</param>
  /// <param name="PanTiltSpeedSpaces">URIs of the pan/tilt speed spaces.</param>
  /// <param name="ZoomSpeedSpaces">URIs of the zoom speed spaces.</param>
  /// <param name="GeoMove">The node accepts GeoMove.</param>
  public record PtzNodeInfo(
    string Token,
    string Name,
    bool HomeSupported,
    bool FixedHomePosition,
    int MaximumNumberOfPresets,
    IReadOnlyList<string> AuxiliaryCommands,
    IReadOnlyList<string> AbsolutePanTiltSpaces,
    IReadOnlyList<string> AbsoluteZoomSpaces,
    IReadOnlyList<string> RelativePanTiltSpaces,
    IReadOnlyList<string> RelativeZoomSpaces,
    IReadOnlyList<string> ContinuousPanTiltSpaces,
    IReadOnlyList<string> ContinuousZoomSpaces,
    IReadOnlyList<string> PanTiltSpeedSpaces,
    IReadOnlyList<string> ZoomSpeedSpaces,
    int MaximumNumberOfPresetTours,
    bool GeoMove);

  /// <summary>A camera's reported aim, in whatever space it answers in.</summary>
  /// <param name="Pan">Horizontal position, uninterpreted: the camera's own units.</param>
  /// <param name="Tilt">Vertical position, likewise.</param>
  /// <param name="Zoom">Zoom position, likewise.</param>
  /// <param name="Moving">The camera says it is still travelling. Absent move state reads as not moving.</param>
  /// <param name="UtcTime">
  /// The camera's own clock. Worth carrying because it advances on firmware whose position does
  /// not, which is how a stubbed GetStatus gives itself away.
  /// </param>
  public record PtzStatus(float Pan, float Tilt, float Zoom, bool Moving, DateTime UtcTime);
}
