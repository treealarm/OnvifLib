using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// Analytics (ver20): the modules a camera runs over a VideoAnalyticsConfiguration, and the rules
/// layered on them. Three of the calls return raw XML by design, so showing it is a feature here
/// rather than a fallback.
/// </summary>
public sealed partial class AnalyticsViewModel(OperationRunner runner, UiLogger logger, IDialogService dialogs)
  : TabViewModelBase("Analytics", runner, logger)
{
  public ObservableCollection<OnvifAnalyticsConfig> Configurations { get; } = [];
  public ObservableCollection<OnvifModuleDescription> SupportedModules { get; } = [];
  public ObservableCollection<OnvifAnalyticsModule> Modules { get; } = [];
  public ObservableCollection<OnvifModuleDescription> SupportedRules { get; } = [];
  public ObservableCollection<OnvifAnalyticsModule> Rules { get; } = [];
  public ObservableCollection<OnvifSimpleItem> SelectedItems { get; } = [];

  [ObservableProperty] private OnvifAnalyticsConfig? _selectedConfiguration;
  private bool _syncing;
  [ObservableProperty] private OnvifModuleDescription? _selectedSupportedModule;
  [ObservableProperty] private OnvifModuleDescription? _selectedSupportedRule;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(StructuredXml))]
  private OnvifAnalyticsModule? _selectedModule;

  [ObservableProperty] private string _rawXml = "";

  /// <summary>
  /// The structured parameters — polygons, line segments, schedules. They have no fixed schema
  /// across vendors, which is why the library hands them over as raw XML and why they are shown
  /// read-only: a Modify has to round-trip them untouched or the camera silently loses whatever
  /// the app did not understand.
  /// </summary>
  public string StructuredXml => SelectedModule is null
    ? ""
    : string.Join(Environment.NewLine + Environment.NewLine,
        SelectedModule.ElementItems.Select(e => $"<!-- {e.Name} -->" + Environment.NewLine + XmlPretty.Format(e.Xml)));

  partial void OnSelectedModuleChanged(OnvifAnalyticsModule? value)
  {
    SelectedItems.Clear();
    if (value is null) return;
    foreach (var item in value.SimpleItems) SelectedItems.Add(item);
  }

  protected override string? DescribeUnavailability(CameraSession session) => session.Analytics is null
    ? session.Advertises(AnalyticsService.GetSupportedWsdls())
      ? "The camera advertises analytics, but the library could not create a client for it — check the Log tab."
      : "This camera does not advertise an analytics service."
    : null;

  protected override void OnCleared()
  {
    Configurations.Clear();
    SupportedModules.Clear();
    Modules.Clear();
    SupportedRules.Clear();
    Rules.Clear();
    SelectedItems.Clear();
    RawXml = "";
  }

  /// <summary>
  /// The configuration tokens come from the media service and have no other source, so the
  /// Media tab pushes them here when it loads them.
  /// </summary>
  public void SetConfigurations(IReadOnlyList<OnvifAnalyticsConfig> configs)
  {
    _syncing = true;
    Configurations.Clear();
    foreach (var config in configs) Configurations.Add(config);
    SelectedConfiguration ??= Configurations.FirstOrDefault();
    _syncing = false;
  }

  partial void OnSelectedConfigurationChanged(OnvifAnalyticsConfig? value)
  {
    if (_syncing || value is null || Session?.Analytics is null) return;
    _ = ReloadSelectionAsync();
  }

  private async Task ReloadSelectionAsync()
  {
    await LoadModulesAsync();
    await LoadRulesAsync();
  }

  public override async Task ActivateAsync()
  {
    if (!IsAvailable) return;
    if (Configurations.Count == 0) await LoadConfigurationsAsync();
    if (SelectedConfiguration is null) return;
    await LoadModulesAsync();
    await LoadRulesAsync();
  }

  [RelayCommand]
  private async Task LoadConfigurationsAsync()
  {
    if (Session?.Media is not { } media)
    {
      Runner.Report("Analytics configurations come from the media service, which is not available", isError: true);
      return;
    }

    var (ok, configs) = await Runner.RunAsync("GetAnalyticsConfigs", media.GetAnalyticsConfigsAsync);
    if (ok && configs is not null) SetConfigurations(configs);
  }

  [RelayCommand]
  private async Task LoadModulesAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;

    var (okSupported, supported) = await Runner.RunAsync($"GetSupportedAnalyticsModules [{token}]",
      () => analytics.GetSupportedAnalyticsModulesAsync(token));
    if (okSupported && supported is not null)
    {
      SupportedModules.Clear();
      foreach (var description in supported) SupportedModules.Add(description);
    }

    var (ok, modules) = await Runner.RunAsync($"GetAnalyticsModules [{token}]",
      () => analytics.GetAnalyticsModulesAsync(token));
    if (!ok || modules is null) return;

    Modules.Clear();
    foreach (var module in modules) Modules.Add(module);
  }

  [RelayCommand]
  private async Task LoadRulesAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;

    var (okSupported, supported) = await Runner.RunAsync($"GetSupportedRules [{token}]",
      () => analytics.GetSupportedRulesAsync(token));
    if (okSupported && supported is not null)
    {
      SupportedRules.Clear();
      foreach (var description in supported) SupportedRules.Add(description);
    }

    var (ok, rules) = await Runner.RunAsync($"GetRules [{token}]", () => analytics.GetRulesAsync(token));
    if (!ok || rules is null) return;

    Rules.Clear();
    foreach (var rule in rules) Rules.Add(rule);
  }

  [RelayCommand]
  private async Task LoadModuleOptionsXmlAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;
    if (SelectedSupportedModule is not { } description) { Runner.Report("Select a supported module type first", isError: true); return; }

    var (ok, documents) = await Runner.RunAsync($"GetAnalyticsModuleOptionsXml [{description.Type}]",
      () => analytics.GetAnalyticsModuleOptionsXmlAsync(description.Type, token));
    if (ok && documents is not null) RawXml = Join(documents);
  }

  [RelayCommand]
  private async Task LoadRuleOptionsXmlAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;
    if (SelectedSupportedRule is not { } description) { Runner.Report("Select a supported rule type first", isError: true); return; }

    var (ok, documents) = await Runner.RunAsync($"GetRuleOptionsXml [{description.Type}]",
      () => analytics.GetRuleOptionsXmlAsync(description.Type, token));
    if (ok && documents is not null) RawXml = Join(documents);
  }

  [RelayCommand]
  private async Task LoadSupportedRulesXmlAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;

    var (ok, documents) = await Runner.RunAsync($"GetSupportedRulesXml [{token}]",
      () => analytics.GetSupportedRulesXmlAsync(token));
    if (ok && documents is not null) RawXml = Join(documents);
  }

  [RelayCommand]
  private async Task ApplyModuleAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;
    if (SelectedModule is not { } module) return;

    if (!await dialogs.ConfirmAsync("Modify an analytics module",
          $"Send the edited parameters of \"{module.Name}\" to the camera?\n\n" +
          "This changes what the camera detects. The structured parameters (polygons, schedules) " +
          "are round-tripped exactly as they were read."))
      return;

    // ElementItems is passed through untouched on purpose: rebuilding it from parsed state is
    // how vendor structures get silently corrupted.
    var edited = module with { SimpleItems = SelectedItems.ToList() };

    if (await Runner.RunAsync($"ModifyAnalyticsModule [{module.Name}]",
          () => analytics.ModifyAnalyticsModuleAsync(token, edited)))
      await LoadModulesAsync();
  }

  [RelayCommand]
  private async Task DeleteModuleAsync()
  {
    if (Session?.Analytics is not { } analytics || Token is not { } token) return;
    if (SelectedModule is not { } module) return;

    if (!await dialogs.ConfirmAsync("Delete an analytics module",
          $"Delete \"{module.Name}\"?\n\nThis disables whatever it was detecting, and there is no undo."))
      return;

    if (await Runner.RunAsync($"DeleteAnalyticsModule [{module.Name}]",
          () => analytics.DeleteAnalyticsModuleAsync(token, module.Name)))
      await LoadModulesAsync();
  }

  private string? Token => SelectedConfiguration?.Token is { Length: > 0 } token ? token : null;

  private static string Join(IReadOnlyList<string> documents) => documents.Count == 0
    ? "(the camera returned no XML)"
    : string.Join(Environment.NewLine + Environment.NewLine, documents.Select(XmlPretty.Format));
}
