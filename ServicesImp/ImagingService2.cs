using ImagingServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  public class ImagingService2 : OnvifServiceBase, IOnvifServiceFactory<ImagingService2>
  {
    public const string WSDL_V20 = "http://www.onvif.org/ver20/imaging/wsdl";
    private ImagingPortClient? _imagingClient;

    protected ImagingService2(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20 };
    }

    public static async Task<ImagingService2?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new ImagingService2(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();
      _imagingClient = _onvifClientFactory.CreateClient<ImagingPortClient, ImagingPort>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _imagingClient.OpenAsync();
    }

    public async Task<OnvifImagingSettings?> GetImagingSettingsAsync(string videoSourceToken)
    {
      if (_imagingClient == null) return null;
      var resp = await _imagingClient.GetImagingSettingsAsync(
        new GetImagingSettingsRequest { VideoSourceToken = videoSourceToken });
      var s = resp.ImagingSettings;
      if (s == null) return null;

      var e = s.Exposure;
      var f = s.Focus;
      return new OnvifImagingSettings(
        s.BrightnessSpecified ? s.Brightness : null,
        s.ContrastSpecified ? s.Contrast : null,
        s.ColorSaturationSpecified ? s.ColorSaturation : null,
        s.SharpnessSpecified ? s.Sharpness : null,
        IrCutFilter: s.IrCutFilterSpecified ? s.IrCutFilter.ToString() : null,
        Exposure: e == null ? null : new OnvifExposure(
          e.Mode.ToString(),
          e.PrioritySpecified ? e.Priority.ToString() : null,
          e.MinExposureTimeSpecified ? e.MinExposureTime : null,
          e.MaxExposureTimeSpecified ? e.MaxExposureTime : null,
          e.MinGainSpecified ? e.MinGain : null,
          e.MaxGainSpecified ? e.MaxGain : null,
          e.MinIrisSpecified ? e.MinIris : null,
          e.MaxIrisSpecified ? e.MaxIris : null,
          e.ExposureTimeSpecified ? e.ExposureTime : null,
          e.GainSpecified ? e.Gain : null,
          e.IrisSpecified ? e.Iris : null),
        WhiteBalance: s.WhiteBalance == null ? null : new OnvifWhiteBalance(
          s.WhiteBalance.Mode.ToString(),
          s.WhiteBalance.CrGainSpecified ? s.WhiteBalance.CrGain : null,
          s.WhiteBalance.CbGainSpecified ? s.WhiteBalance.CbGain : null),
        Focus: f == null ? null : new OnvifFocusSettings(
          f.AutoFocusMode.ToString(),
          f.DefaultSpeedSpecified ? f.DefaultSpeed : null,
          f.NearLimitSpecified ? f.NearLimit : null,
          f.FarLimitSpecified ? f.FarLimit : null),
        WideDynamicRange: s.WideDynamicRange == null ? null : new OnvifModeLevel(
          s.WideDynamicRange.Mode.ToString(),
          s.WideDynamicRange.LevelSpecified ? s.WideDynamicRange.Level : null),
        BacklightCompensation: s.BacklightCompensation == null ? null : new OnvifModeLevel(
          s.BacklightCompensation.Mode.ToString(),
          s.BacklightCompensation.LevelSpecified ? s.BacklightCompensation.Level : null));
    }

    /// <summary>
    /// Applies the non-null members of <paramref name="settings"/> and leaves everything else as
    /// the camera has it.
    /// </summary>
    /// <remarks>
    /// Read-modify-write: the current settings are fetched and only the requested members changed,
    /// because some cameras treat an absent optional field in a Set as "reset to default" rather
    /// than "leave unchanged" — which would silently clobber settings the caller never touched.
    /// The same applies inside each group: asking for an exposure time does not reset the mode,
    /// the window or the gain limits.
    /// </remarks>
    /// <exception cref="ArgumentException">A mode string is not one ONVIF defines for that setting.</exception>
    public async Task SetImagingSettingsAsync(string videoSourceToken, OnvifImagingSettings settings)
    {
      if (_imagingClient == null) return;

      // If the camera doesn't return its current settings, refuse rather than push a blank
      // ImagingSettings20 — see the remarks above.
      var current = await _imagingClient.GetImagingSettingsAsync(
        new GetImagingSettingsRequest { VideoSourceToken = videoSourceToken });
      var existing = current.ImagingSettings
        ?? throw new InvalidOperationException("Camera did not return current imaging settings; refusing to apply a partial update");

      if (settings.Brightness.HasValue)
      {
        existing.Brightness = settings.Brightness.Value;
        existing.BrightnessSpecified = true;
      }
      if (settings.Contrast.HasValue)
      {
        existing.Contrast = settings.Contrast.Value;
        existing.ContrastSpecified = true;
      }
      if (settings.ColorSaturation.HasValue)
      {
        existing.ColorSaturation = settings.ColorSaturation.Value;
        existing.ColorSaturationSpecified = true;
      }
      if (settings.Sharpness.HasValue)
      {
        existing.Sharpness = settings.Sharpness.Value;
        existing.SharpnessSpecified = true;
      }
      if (settings.IrCutFilter is { } irCut)
      {
        existing.IrCutFilter = ParseMode<IrCutFilterMode>(irCut, nameof(settings.IrCutFilter));
        existing.IrCutFilterSpecified = true;
      }

      if (settings.Exposure is { } exposure)
      {
        var e = existing.Exposure ??= NewGroup<Exposure20>(exposure.Mode, nameof(settings.Exposure));
        if (exposure.Mode is { } mode) e.Mode = ParseMode<ExposureMode>(mode, "Exposure.Mode");
        if (exposure.Priority is { } priority)
        {
          e.Priority = ParseMode<ExposurePriority>(priority, "Exposure.Priority");
          e.PrioritySpecified = true;
        }
        Apply(exposure.MinExposureTime, v => { e.MinExposureTime = v; e.MinExposureTimeSpecified = true; });
        Apply(exposure.MaxExposureTime, v => { e.MaxExposureTime = v; e.MaxExposureTimeSpecified = true; });
        Apply(exposure.MinGain, v => { e.MinGain = v; e.MinGainSpecified = true; });
        Apply(exposure.MaxGain, v => { e.MaxGain = v; e.MaxGainSpecified = true; });
        Apply(exposure.MinIris, v => { e.MinIris = v; e.MinIrisSpecified = true; });
        Apply(exposure.MaxIris, v => { e.MaxIris = v; e.MaxIrisSpecified = true; });
        Apply(exposure.ExposureTime, v => { e.ExposureTime = v; e.ExposureTimeSpecified = true; });
        Apply(exposure.Gain, v => { e.Gain = v; e.GainSpecified = true; });
        Apply(exposure.Iris, v => { e.Iris = v; e.IrisSpecified = true; });
      }

      if (settings.WhiteBalance is { } wb)
      {
        var w = existing.WhiteBalance ??= NewGroup<WhiteBalance20>(wb.Mode, nameof(settings.WhiteBalance));
        if (wb.Mode is { } mode) w.Mode = ParseMode<WhiteBalanceMode>(mode, "WhiteBalance.Mode");
        Apply(wb.CrGain, v => { w.CrGain = v; w.CrGainSpecified = true; });
        Apply(wb.CbGain, v => { w.CbGain = v; w.CbGainSpecified = true; });
      }

      if (settings.Focus is { } focus)
      {
        var f = existing.Focus ??= NewGroup<FocusConfiguration20>(focus.AutoFocusMode, nameof(settings.Focus));
        if (focus.AutoFocusMode is { } mode) f.AutoFocusMode = ParseMode<AutoFocusMode>(mode, "Focus.AutoFocusMode");
        Apply(focus.DefaultSpeed, v => { f.DefaultSpeed = v; f.DefaultSpeedSpecified = true; });
        Apply(focus.NearLimit, v => { f.NearLimit = v; f.NearLimitSpecified = true; });
        Apply(focus.FarLimit, v => { f.FarLimit = v; f.FarLimitSpecified = true; });
      }

      if (settings.WideDynamicRange is { } wdr)
      {
        var w = existing.WideDynamicRange ??= NewGroup<WideDynamicRange20>(wdr.Mode, nameof(settings.WideDynamicRange));
        if (wdr.Mode is { } mode) w.Mode = ParseMode<WideDynamicMode>(mode, "WideDynamicRange.Mode");
        Apply(wdr.Level, v => { w.Level = v; w.LevelSpecified = true; });
      }

      if (settings.BacklightCompensation is { } blc)
      {
        var b = existing.BacklightCompensation ??= NewGroup<BacklightCompensation20>(blc.Mode, nameof(settings.BacklightCompensation));
        if (blc.Mode is { } mode) b.Mode = ParseMode<BacklightCompensationMode>(mode, "BacklightCompensation.Mode");
        Apply(blc.Level, v => { b.Level = v; b.LevelSpecified = true; });
      }

      await _imagingClient.SetImagingSettingsAsync(new SetImagingSettingsRequest
      {
        VideoSourceToken = videoSourceToken,
        ImagingSettings = existing,
      });
    }

    private static void Apply(float? value, Action<float> set)
    {
      if (value.HasValue) set(value.Value);
    }

    private static T ParseMode<T>(string value, string field) where T : struct, Enum =>
      Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
        ? parsed
        : throw new ArgumentException(
            $"'{value}' is not a valid {field}; expected one of {string.Join(", ", Enum.GetNames<T>())}", field);

    // A group the camera did not report has no mode to keep, and every one of these groups has a
    // mandatory mode — so a new group without a requested mode would go out with whatever the enum
    // defaults to, which is a setting the caller never chose.
    private static T NewGroup<T>(string? requestedMode, string group) where T : new() =>
      requestedMode is null
        ? throw new ArgumentException($"The camera reports no current {group} settings, so a mode must be given to create them", group)
        : new T();

    public async Task<OnvifImagingOptions?> GetOptionsAsync(string videoSourceToken)
    {
      if (_imagingClient == null) return null;
      var resp = await _imagingClient.GetOptionsAsync(
        new GetOptionsRequest { VideoSourceToken = videoSourceToken });
      var o = resp.ImagingOptions;
      if (o == null) return null;

      var e = o.Exposure;
      return new OnvifImagingOptions(
        Range(o.Brightness),
        Range(o.Contrast),
        Range(o.ColorSaturation),
        Range(o.Sharpness),
        IrCutFilterModes: Names(o.IrCutFilterModes),
        Exposure: e == null ? null : new OnvifExposureOptions(
          Names(e.Mode), Names(e.Priority),
          Range(e.MinExposureTime), Range(e.MaxExposureTime),
          Range(e.MinGain), Range(e.MaxGain),
          Range(e.MinIris), Range(e.MaxIris),
          Range(e.ExposureTime), Range(e.Gain), Range(e.Iris)),
        WhiteBalance: o.WhiteBalance == null ? null
          : new OnvifWhiteBalanceOptions(Names(o.WhiteBalance.Mode), Range(o.WhiteBalance.YrGain), Range(o.WhiteBalance.YbGain)),
        Focus: o.Focus == null ? null
          : new OnvifFocusOptions(Names(o.Focus.AutoFocusModes), Range(o.Focus.DefaultSpeed), Range(o.Focus.NearLimit), Range(o.Focus.FarLimit)),
        WideDynamicRange: o.WideDynamicRange == null ? null
          : new OnvifModeLevelOptions(Names(o.WideDynamicRange.Mode), Range(o.WideDynamicRange.Level)),
        BacklightCompensation: o.BacklightCompensation == null ? null
          : new OnvifModeLevelOptions(Names(o.BacklightCompensation.Mode), Range(o.BacklightCompensation.Level)));
    }

    private static OnvifFloatRange? Range(FloatRange? r) => r == null ? null : new OnvifFloatRange(r.Min, r.Max);

    private static IReadOnlyList<string> Names<T>(T[]? values) where T : struct, Enum =>
      (values ?? []).Select(v => v.ToString()).Distinct().ToList();

    // ---- Focus control ------------------------------------------------------------------------
    // The focus *configuration* (auto/manual, limits) is part of the imaging settings above. These
    // move the lens itself. Exactly one kind of move goes in each request — the schema makes them
    // a choice, and cameras fault on a request that names two.

    /// <summary>Moves focus to an absolute lens position, in the range GetMoveOptionsAsync reports.</summary>
    public Task MoveFocusAbsoluteAsync(string videoSourceToken, float position, float? speed = null) =>
      MoveFocusAsync(videoSourceToken, new FocusMove
      {
        Absolute = new AbsoluteFocus { Position = position, Speed = speed ?? 0, SpeedSpecified = speed.HasValue },
      });

    /// <summary>Moves focus by a distance; the sign picks near or far, as the camera defines it.</summary>
    public Task MoveFocusRelativeAsync(string videoSourceToken, float distance, float? speed = null) =>
      MoveFocusAsync(videoSourceToken, new FocusMove
      {
        Relative = new RelativeFocus { Distance = distance, Speed = speed ?? 0, SpeedSpecified = speed.HasValue },
      });

    /// <summary>Starts focus moving at a speed until <see cref="StopFocusAsync"/>; the sign picks direction.</summary>
    public Task MoveFocusContinuousAsync(string videoSourceToken, float speed) =>
      MoveFocusAsync(videoSourceToken, new FocusMove { Continuous = new ContinuousFocus { Speed = speed } });

    private async Task MoveFocusAsync(string videoSourceToken, FocusMove move)
    {
      if (_imagingClient == null)
        throw new InvalidOperationException("Imaging client not initialized");
      await _imagingClient.MoveAsync(new MoveRequest { VideoSourceToken = videoSourceToken, Focus = move });
    }

    public async Task StopFocusAsync(string videoSourceToken)
    {
      if (_imagingClient == null)
        throw new InvalidOperationException("Imaging client not initialized");
      await _imagingClient.StopAsync(new StopRequest { VideoSourceToken = videoSourceToken });
    }

    public async Task<OnvifFocusMoveOptions?> GetMoveOptionsAsync(string videoSourceToken)
    {
      if (_imagingClient == null) return null;
      var resp = await _imagingClient.GetMoveOptionsAsync(new GetMoveOptionsRequest { VideoSourceToken = videoSourceToken });
      var m = resp?.MoveOptions;
      if (m == null) return null;
      return new OnvifFocusMoveOptions(
        Range(m.Absolute?.Position), Range(m.Absolute?.Speed),
        Range(m.Relative?.Distance), Range(m.Relative?.Speed),
        Range(m.Continuous?.Speed));
    }

    /// <summary>The focus motor's position and whether it is still moving.</summary>
    /// <remarks>
    /// Null means the call failed; a camera with no focus status to report answers with a record
    /// whose members are all null. The two are kept apart for the same reason as
    /// PtzService2.GetStatusAsync: "no motor" and "would not answer" are different diagnoses.
    /// </remarks>
    public async Task<OnvifImagingStatus?> GetStatusAsync(string videoSourceToken)
    {
      if (_imagingClient == null) return null;
      var resp = await _imagingClient.GetStatusAsync(new GetStatusRequest { VideoSourceToken = videoSourceToken });
      var focus = resp?.Status?.FocusStatus20;
      return focus == null
        ? new OnvifImagingStatus(null, null, null)
        : new OnvifImagingStatus(focus.Position, focus.MoveStatus.ToString(), string.IsNullOrEmpty(focus.Error) ? null : focus.Error);
    }

    public override void Dispose()
    {
      try { _imagingClient?.Close(); } catch { }
      base.Dispose();
    }
  }
}
