using AnalyticsServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  /// <summary>One name/value parameter of an analytics module or rule.</summary>
  public record OnvifSimpleItem(string Name, string Value);

  /// <summary>
  /// A configured analytics module or rule. <paramref name="ElementItemsXml"/> carries the
  /// structured parameters (polygons, line segments, schedules) as raw XML: they have no fixed
  /// schema across vendors, and round-tripping them untouched is the only way a Modify call can
  /// avoid silently dropping whatever it did not understand.
  /// </summary>
  public record OnvifAnalyticsModule(
    string Name,
    string Type,
    IReadOnlyList<OnvifSimpleItem> SimpleItems,
    IReadOnlyList<string> ElementItemsXml);

  /// <summary>One parameter a module/rule type accepts, as advertised by the camera.</summary>
  public record OnvifParameterDescription(string Name, string Type);

  /// <summary>
  /// How a camera lays out its motion-detection cells. Needed to make sense of an ActiveCells
  /// bitmask: the mask is a flat run of bits, and only these two numbers say which cell is where.
  /// </summary>
  public record OnvifCellLayout(int Columns, int Rows);

  /// <summary>A module or rule type the camera says it supports, with its parameter list.</summary>
  public record OnvifModuleDescription(
    string Type,
    string? MaxInstances,
    IReadOnlyList<OnvifParameterDescription> Parameters,
    // Present only for types with a cell mask, and only when the camera reveals the layout —
    // it is optional in ONVIF and many firmwares omit it entirely.
    OnvifCellLayout? CellLayout = null);

  /// <summary>
  /// ONVIF ver20 Analytics: the modules a camera runs over a VideoAnalyticsConfiguration and the
  /// rules layered on top of them. Both live at the same XAddr but on two different ports
  /// (AnalyticsEnginePort for modules, RuleEnginePort for rules), so this wraps both.
  ///
  /// Everything here is keyed by a VideoAnalyticsConfiguration token, which the media service
  /// hands out — see MediaService.GetAnalyticsConfigurationsAsync.
  /// </summary>
  public class AnalyticsService : OnvifServiceBase, IOnvifServiceFactory<AnalyticsService>
  {
    public const string WSDL_V20 = "http://www.onvif.org/ver20/analytics/wsdl";

    private AnalyticsEnginePortClient? _modulesClient;
    private RuleEnginePortClient? _rulesClient;

    protected AnalyticsService(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20 };
    }

    public static async Task<AnalyticsService?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new AnalyticsService(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();

      _modulesClient = _onvifClientFactory.CreateClient<AnalyticsEnginePortClient, AnalyticsEnginePort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _modulesClient.OpenAsync();

      _rulesClient = _onvifClientFactory.CreateClient<RuleEnginePortClient, RuleEnginePort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _rulesClient.OpenAsync();
    }

    // ---- Analytics modules -------------------------------------------------------------------

    public async Task<IReadOnlyList<OnvifModuleDescription>> GetSupportedAnalyticsModulesAsync(string configurationToken)
    {
      if (_modulesClient == null) return [];
      var resp = await _modulesClient.GetSupportedAnalyticsModulesAsync(
        new GetSupportedAnalyticsModulesRequest { ConfigurationToken = configurationToken });
      var described = ToDescriptions(resp.SupportedAnalyticsModules?.AnalyticsModuleDescription);
      return await WithCellLayoutsAsync(described, configurationToken, ModuleOptionsCellLayoutAsync);
    }

    public async Task<IReadOnlyList<OnvifAnalyticsModule>> GetAnalyticsModulesAsync(string configurationToken)
    {
      if (_modulesClient == null) return [];
      var resp = await _modulesClient.GetAnalyticsModulesAsync(
        new GetAnalyticsModulesRequest { ConfigurationToken = configurationToken });
      return ToModules(resp.AnalyticsModule);
    }

    public async Task CreateAnalyticsModuleAsync(string configurationToken, OnvifAnalyticsModule module)
    {
      if (_modulesClient == null) return;
      await _modulesClient.CreateAnalyticsModulesAsync(new CreateAnalyticsModulesRequest
      {
        ConfigurationToken = configurationToken,
        AnalyticsModule = [ToConfig(module)],
      });
    }

    public async Task ModifyAnalyticsModuleAsync(string configurationToken, OnvifAnalyticsModule module)
    {
      if (_modulesClient == null) return;
      await _modulesClient.ModifyAnalyticsModulesAsync(new ModifyAnalyticsModulesRequest
      {
        ConfigurationToken = configurationToken,
        AnalyticsModule = [ToConfig(module)],
      });
    }

    public async Task DeleteAnalyticsModuleAsync(string configurationToken, string moduleName)
    {
      if (_modulesClient == null) return;
      await _modulesClient.DeleteAnalyticsModulesAsync(new DeleteAnalyticsModulesRequest
      {
        ConfigurationToken = configurationToken,
        AnalyticsModuleName = [moduleName],
      });
    }

    // ---- Rules -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnvifModuleDescription>> GetSupportedRulesAsync(string configurationToken)
    {
      if (_rulesClient == null) return [];
      var resp = await _rulesClient.GetSupportedRulesAsync(
        new GetSupportedRulesRequest { ConfigurationToken = configurationToken });
      var described = ToDescriptions(resp.SupportedRules?.RuleDescription);
      return await WithCellLayoutsAsync(described, configurationToken, RuleOptionsCellLayoutAsync);
    }

    public async Task<IReadOnlyList<OnvifAnalyticsModule>> GetRulesAsync(string configurationToken)
    {
      if (_rulesClient == null) return [];
      var resp = await _rulesClient.GetRulesAsync(
        new GetRulesRequest { ConfigurationToken = configurationToken });
      return ToModules(resp.Rule);
    }

    public async Task CreateRuleAsync(string configurationToken, OnvifAnalyticsModule rule)
    {
      if (_rulesClient == null) return;
      await _rulesClient.CreateRulesAsync(new CreateRulesRequest
      {
        ConfigurationToken = configurationToken,
        Rule = [ToConfig(rule)],
      });
    }

    public async Task ModifyRuleAsync(string configurationToken, OnvifAnalyticsModule rule)
    {
      if (_rulesClient == null) return;
      await _rulesClient.ModifyRulesAsync(new ModifyRulesRequest
      {
        ConfigurationToken = configurationToken,
        Rule = [ToConfig(rule)],
      });
    }

    public async Task DeleteRuleAsync(string configurationToken, string ruleName)
    {
      if (_rulesClient == null) return;
      await _rulesClient.DeleteRulesAsync(new DeleteRulesRequest
      {
        ConfigurationToken = configurationToken,
        RuleName = [ruleName],
      });
    }

    /// <summary>
    /// What values a module/rule type will accept, as the camera describes them. ONVIF returns this
    /// as arbitrary XML (ConfigOptions.Any) because the constraints are schema-defined per type, so
    /// it is handed back raw for the caller to interpret.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAnalyticsModuleOptionsXmlAsync(string type, string configurationToken)
    {
      if (_modulesClient == null) return [];
      var resp = await _modulesClient.GetAnalyticsModuleOptionsAsync(new GetAnalyticsModuleOptionsRequest
      {
        Type = new System.Xml.XmlQualifiedName(type),
        ConfigurationToken = configurationToken,
      });
      return (resp.Options ?? []).Select(o => o.Any?.OuterXml ?? string.Empty).ToList();
    }

    public async Task<IReadOnlyList<string>> GetRuleOptionsXmlAsync(string ruleType, string configurationToken)
    {
      if (_rulesClient == null) return [];
      var resp = await _rulesClient.GetRuleOptionsAsync(new GetRuleOptionsRequest
      {
        RuleType = new System.Xml.XmlQualifiedName(ruleType),
        ConfigurationToken = configurationToken,
      });
      return (resp.RuleOptions ?? []).Select(o => o.Any?.OuterXml ?? string.Empty).ToList();
    }

  /// <summary>Diagnostic: the camera's rule descriptions exactly as sent, including any extension
    /// content the typed mapping below drops.</summary>
    public async Task<IReadOnlyList<string>> GetSupportedRulesXmlAsync(string configurationToken)
    {
      if (_rulesClient == null) return [];
      var resp = await _rulesClient.GetSupportedRulesAsync(
        new GetSupportedRulesRequest { ConfigurationToken = configurationToken });
      var serializer = new System.Xml.Serialization.XmlSerializer(typeof(ConfigDescription));
      var result = new List<string>();
      foreach (var d in resp.SupportedRules?.RuleDescription ?? [])
      {
        using var sw = new StringWriter();
        serializer.Serialize(sw, d);
        result.Add(sw.ToString());
      }
      return result;
    }

  /// <summary>
    /// Fills in the cell layout for the types that need one. Only types carrying a base64Binary
    /// parameter are asked about: the layout is meaningless for the rest, and the options call it
    /// takes is optional in ONVIF — most firmwares answer it with a fault, which is not worth
    /// provoking once per type on every read of the tab.
    /// </summary>
    private static async Task<IReadOnlyList<OnvifModuleDescription>> WithCellLayoutsAsync(
      IReadOnlyList<OnvifModuleDescription> described,
      string configurationToken,
      Func<string, string, Task<OnvifCellLayout?>> fromOptions)
    {
      var result = new List<OnvifModuleDescription>(described.Count);
      foreach (var d in described)
      {
        if (d.CellLayout is not null || !HasCellMask(d))
        {
          result.Add(d);
          continue;
        }

        var layout = await fromOptions(d.Type, configurationToken);
        result.Add(layout is null ? d : d with { CellLayout = layout });
      }
      return result;
    }

    private static bool HasCellMask(OnvifModuleDescription d) =>
      d.Parameters.Any(p => p.Type.EndsWith("base64Binary", StringComparison.OrdinalIgnoreCase));

    private async Task<OnvifCellLayout?> ModuleOptionsCellLayoutAsync(string type, string configurationToken)
    {
      if (_modulesClient == null) return null;
      try
      {
        var resp = await _modulesClient.GetAnalyticsModuleOptionsAsync(new GetAnalyticsModuleOptionsRequest
        {
          Type = new System.Xml.XmlQualifiedName(type),
          ConfigurationToken = configurationToken,
        });
        return FindCellLayout((resp.Options ?? []).Select(o => o.Any));
      }
      catch (Exception)
      {
        // Optional operation; a fault here means "this camera will not say", not a failure.
        return null;
      }
    }

    private async Task<OnvifCellLayout?> RuleOptionsCellLayoutAsync(string ruleType, string configurationToken)
    {
      if (_rulesClient == null) return null;
      try
      {
        var resp = await _rulesClient.GetRuleOptionsAsync(new GetRuleOptionsRequest
        {
          RuleType = new System.Xml.XmlQualifiedName(ruleType),
          ConfigurationToken = configurationToken,
        });
        return FindCellLayout((resp.RuleOptions ?? []).Select(o => o.Any));
      }
      catch (Exception)
      {
        return null;
      }
    }

    /// <summary>
    /// Finds a tt:CellLayout anywhere in the given XML. Vendors nest it differently and the
    /// surrounding option shape is schema-defined per type, so the search is by local name rather
    /// than by a fixed path.
    /// </summary>
    private static OnvifCellLayout? FindCellLayout(IEnumerable<System.Xml.XmlElement?> elements)
    {
      foreach (var element in elements)
      {
        if (element is null) continue;

        var nodes = element.LocalName == "CellLayout"
          ? [element]
          : element.GetElementsByTagName("*").Cast<System.Xml.XmlNode>()
              .OfType<System.Xml.XmlElement>()
              .Where(e => e.LocalName == "CellLayout")
              .ToArray();

        foreach (var node in nodes)
        {
          if (int.TryParse(node.GetAttribute("Columns"), out var columns) &&
              int.TryParse(node.GetAttribute("Rows"), out var rows) &&
              columns > 0 && rows > 0)
          {
            return new OnvifCellLayout(columns, rows);
          }
        }
      }
      return null;
    }

  // ---- Mapping -----------------------------------------------------------------------------

    private static IReadOnlyList<OnvifAnalyticsModule> ToModules(Config[]? configs)
    {
      if (configs == null) return [];
      var result = new List<OnvifAnalyticsModule>(configs.Length);
      foreach (var c in configs)
      {
        if (c == null) continue;
        result.Add(new OnvifAnalyticsModule(
          c.Name ?? string.Empty,
          // The type is a qualified name; only the local part identifies the module across
          // vendors, and it is what GetSupported* advertises too.
          c.Type?.Name ?? string.Empty,
          (c.Parameters?.SimpleItem ?? [])
            .Where(i => i?.Name != null)
            .Select(i => new OnvifSimpleItem(i.Name, i.Value ?? string.Empty))
            .ToList(),
          (c.Parameters?.ElementItem ?? [])
            .Where(i => i?.Any != null)
            .Select(i => i.Any.OuterXml)
            .ToList()));
      }
      return result;
    }

    private static IReadOnlyList<OnvifModuleDescription> ToDescriptions(ConfigDescription[]? descriptions)
    {
      if (descriptions == null) return [];
      var result = new List<OnvifModuleDescription>(descriptions.Length);
      foreach (var d in descriptions)
      {
        if (d == null) continue;
        result.Add(new OnvifModuleDescription(
          d.Name?.Name ?? string.Empty,
          d.maxInstances,
          (d.Parameters?.SimpleItemDescription ?? [])
            .Where(p => p?.Name != null)
            .Select(p => new OnvifParameterDescription(p.Name, p.Type?.Name ?? string.Empty))
            .ToList(),
          // Some firmwares put the layout straight into the description's extension, which costs
          // nothing to read — it is already in this response.
          FindCellLayout(d.Extension?.Any ?? [])));
      }
      return result;
    }

    private static Config ToConfig(OnvifAnalyticsModule module)
    {
      var elementItems = new List<ItemListElementItem>();
      foreach (var xml in module.ElementItemsXml)
      {
        if (string.IsNullOrWhiteSpace(xml)) continue;
        var doc = new System.Xml.XmlDocument();
        try
        {
          doc.LoadXml(xml);
        }
        catch (System.Xml.XmlException)
        {
          // Refusing the whole update because one structured parameter came back malformed would
          // make the module uneditable; dropping just that item at least keeps the rest writable.
          continue;
        }
        if (doc.DocumentElement == null) continue;
        elementItems.Add(new ItemListElementItem
        {
          Name = doc.DocumentElement.LocalName,
          Any = doc.DocumentElement,
        });
      }

      return new Config
      {
        Name = module.Name,
        // Namespace-less: cameras match on the local name, and the ver20 analytics namespace
        // varies between the ONVIF standard modules and vendor-specific ones.
        Type = new System.Xml.XmlQualifiedName(module.Type),
        Parameters = new ItemList
        {
          SimpleItem = module.SimpleItems
            .Select(i => new ItemListSimpleItem { Name = i.Name, Value = i.Value })
            .ToArray(),
          ElementItem = elementItems.ToArray(),
        },
      };
    }
  }
}
