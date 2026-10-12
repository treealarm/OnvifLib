using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;

namespace OnvifLib.Gui.ViewModels;

/// <summary>Video sources and on-screen display (text or images burnt into the picture).</summary>
public sealed partial class MediaViewModel
{
  /// <summary>Set by the shell. Without it, deleting an OSD is refused rather than done unasked.</summary>
  public IDialogService? Dialogs { get; set; }

  public ObservableCollection<OnvifVideoSourceConfig> VideoSourceConfigs { get; } = [];
  public ObservableCollection<OnvifVideoSource> VideoSources { get; } = [];
  public ObservableCollection<OnvifOsd> Osds { get; } = [];

  public ObservableCollection<string> OsdTypes { get; } = [];
  public ObservableCollection<string> OsdTextTypes { get; } = [];
  public ObservableCollection<string> OsdPositions { get; } = [];
  public ObservableCollection<string> OsdDateFormats { get; } = [];
  public ObservableCollection<string> OsdTimeFormats { get; } = [];

  [ObservableProperty] private OnvifVideoSourceConfig? _selectedVideoSourceConfig;
  [ObservableProperty] private OnvifOsd? _selectedOsd;
  [ObservableProperty] private string _osdOptionsText = "";
  [ObservableProperty] private string _videoSourcesNote = "";

  [ObservableProperty] private string _editOsdType = "Text";
  [ObservableProperty] private string _editTextType = "Plain";
  [ObservableProperty] private string _editPlainText = "";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(IsCustomPosition))]
  private string _editPosition = "UpperLeft";

  [ObservableProperty] private double _editX;
  [ObservableProperty] private double _editY;
  [ObservableProperty] private double _editFontSize = 32;
  [ObservableProperty] private double _fontSizeMin = 8;
  [ObservableProperty] private double _fontSizeMax = 128;
  [ObservableProperty] private string? _editDateFormat;
  [ObservableProperty] private string? _editTimeFormat;
  [ObservableProperty] private string _editImagePath = "";

  public bool IsCustomPosition => EditPosition == "Custom";

  partial void OnSelectedVideoSourceConfigChanged(OnvifVideoSourceConfig? value)
  {
    if (!_loading && value is not null && Session?.Media is not null) _ = LoadOsdsAsync();
  }

  partial void OnSelectedOsdChanged(OnvifOsd? value)
  {
    if (value is null) return;
    EditOsdType = value.Type;
    EditTextType = value.TextType ?? EditTextType;
    EditPlainText = value.PlainText ?? "";
    EditPosition = value.PositionType ?? EditPosition;
    EditX = value.X ?? 0;
    EditY = value.Y ?? 0;
    if (value.FontSize is { } size) EditFontSize = size;
    EditDateFormat = value.DateFormat ?? EditDateFormat;
    EditTimeFormat = value.TimeFormat ?? EditTimeFormat;
    EditImagePath = value.ImagePath ?? "";
  }

  [RelayCommand]
  private async Task LoadVideoSourcesAsync()
  {
    if (Session?.Media is not { } media) return;
    var (ok, configs) = await Runner.RunAsync("GetVideoSourceConfigurations", media.GetVideoSourceConfigurationsAsync);
    if (ok)
    {
      _loading = true;
      VideoSourceConfigs.Clear();
      foreach (var c in configs ?? []) VideoSourceConfigs.Add(c);
      _loading = false;
      // The configuration the selected stream uses is the one whose OSDs the user is looking at.
      SelectedVideoSourceConfig =
        VideoSourceConfigs.FirstOrDefault(c => c.Token == SelectedProfile?.VideoSourceConfigToken)
        ?? VideoSourceConfigs.FirstOrDefault();
    }

    var (sok, sources) = await Runner.RunAsync("GetVideoSources", media.GetVideoSourcesAsync);
    if (sok)
    {
      VideoSources.Clear();
      foreach (var v in sources ?? []) VideoSources.Add(v);
      VideoSourcesNote = VideoSources.Count > 0 && VideoSources.All(v => v.Framerate == 0)
        ? "Framerate and size are unknown: Media2 has no GetVideoSources, so these are derived from the configurations."
        : "";
    }
  }

  [RelayCommand]
  private async Task LoadOsdsAsync()
  {
    if (Session?.Media is not { } media || SelectedVideoSourceConfig is not { } config) return;

    var (ok, osds) = await Runner.RunAsync($"GetOSDs [{config.Name}]", () => media.GetOsdsAsync(config.Token));
    if (ok)
    {
      Osds.Clear();
      foreach (var o in osds ?? []) Osds.Add(o);
    }

    var (ook, options) = await Runner.RunAsync("GetOSDOptions", () => media.GetOsdOptionsAsync(config.Token));
    if (!ook) return;
    if (options is null) { OsdOptionsText = "the camera reported no OSD options"; return; }

    Fill(OsdTypes, options.Types);
    Fill(OsdTextTypes, options.TextTypes);
    Fill(OsdPositions, options.PositionOptions);
    Fill(OsdDateFormats, options.DateFormats);
    Fill(OsdTimeFormats, options.TimeFormats);
    if (options.FontSizeMin is { } min && options.FontSizeMax is { } max)
    {
      FontSizeMax = max;
      FontSizeMin = min;
      EditFontSize = Math.Clamp(EditFontSize, min, max);
    }
    if (OsdPositions.Count > 0 && !OsdPositions.Contains(EditPosition)) EditPosition = OsdPositions[0];
    EditDateFormat ??= OsdDateFormats.FirstOrDefault();
    EditTimeFormat ??= OsdTimeFormats.FirstOrDefault();

    OsdOptionsText = $"{Osds.Count} of {options.MaxTotal} used" +
                     (options.MaxPlainText is { } plain ? $", up to {plain} plain text" : "") +
                     (options.FontSizeMin is null ? "" : $", font {options.FontSizeMin}–{options.FontSizeMax}");

    static void Fill(ObservableCollection<string> target, IEnumerable<string> values)
    {
      target.Clear();
      foreach (var v in values) target.Add(v);
    }
  }

  private OnvifOsd BuildOsd(string token, string configToken) => new(
    token,
    configToken,
    EditOsdType,
    PositionType: EditPosition,
    X: IsCustomPosition ? (float)EditX : null,
    Y: IsCustomPosition ? (float)EditY : null,
    TextType: EditOsdType == "Text" ? EditTextType : null,
    PlainText: EditOsdType == "Text" && EditTextType == "Plain" ? EditPlainText : null,
    DateFormat: EditOsdType == "Text" && EditTextType is "Date" or "DateAndTime" ? EditDateFormat : null,
    TimeFormat: EditOsdType == "Text" && EditTextType is "Time" or "DateAndTime" ? EditTimeFormat : null,
    FontSize: EditOsdType == "Text" ? (int)EditFontSize : null,
    ImagePath: EditOsdType == "Image" ? EditImagePath : null);

  /// <summary>Adds a new OSD from the editor. Reversible — delete it again — so it does not ask.</summary>
  [RelayCommand]
  private async Task CreateOsdAsync()
  {
    if (Session?.Media is not { } media || SelectedVideoSourceConfig is not { } config) return;
    if (EditOsdType == "Text" && EditTextType == "Plain" && string.IsNullOrWhiteSpace(EditPlainText))
    {
      Runner.Report("Type the text to show first", isError: true);
      return;
    }
    var osd = BuildOsd("", config.Token);
    var (ok, token) = await Runner.RunAsync("CreateOSD", () => media.CreateOsdAsync(osd));
    if (!ok) return;
    await LoadOsdsAsync();
    SelectedOsd = Osds.FirstOrDefault(o => o.Token == token);
  }

  /// <summary>Writes the editor onto the selected OSD; members the editor does not show are kept.</summary>
  [RelayCommand]
  private async Task ApplyOsdAsync()
  {
    if (Session?.Media is not { } media || SelectedOsd is not { } selected) return;
    var osd = BuildOsd(selected.Token, selected.VideoSourceConfigToken);
    if (await Runner.RunAsync($"SetOSD [{selected.Token}]", () => media.SetOsdAsync(osd)))
    {
      await LoadOsdsAsync();
      SelectedOsd = Osds.FirstOrDefault(o => o.Token == selected.Token);
    }
  }

  [RelayCommand]
  private async Task DeleteOsdAsync()
  {
    if (Session?.Media is not { } media || SelectedOsd is not { } selected) return;
    var label = selected.PlainText ?? selected.TextType ?? selected.Type;
    if (Dialogs is null || !await Dialogs.ConfirmAsync("Delete an OSD",
          $"Remove \"{label}\" from the picture? The camera does not keep a copy, so its settings are gone."))
      return;
    if (await Runner.RunAsync($"DeleteOSD [{selected.Token}]", () => media.DeleteOsdAsync(selected.Token)))
      await LoadOsdsAsync();
  }

  private void ClearOsd()
  {
    VideoSourceConfigs.Clear();
    VideoSources.Clear();
    Osds.Clear();
    OsdOptionsText = VideoSourcesNote = "";
    SelectedVideoSourceConfig = null;
    SelectedOsd = null;
  }
}
