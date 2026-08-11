namespace OnvifLib.Probe.Steps;

/// <summary>
/// Analytics (ver20): the modules a camera runs over a VideoAnalyticsConfiguration and the rules
/// layered on them. Three of the calls return raw XML by design — printing it is the point.
/// </summary>
public static class AnalyticsSteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Analytics, "analytics");

    if (ctx.Analytics is not { } analytics) { r.Skip("analytics", "service not available"); return; }

    // The configuration tokens come from the media service, so the media section has to have run.
    if (ctx.AnalyticsConfigs.Count == 0)
    {
      var configs = ctx.Media is { } media
        ? await r.StepAsync("GetAnalyticsConfigs (media)", media.GetAnalyticsConfigsAsync)
        : null;
      if (configs is not null) ctx.AnalyticsConfigs.AddRange(configs);
    }

    if (ctx.AnalyticsConfigs.Count == 0)
    {
      r.Skip("analytics", "no VideoAnalyticsConfiguration to address — the media service listed none");
      return;
    }

    foreach (var config in ctx.AnalyticsConfigs)
    {
      r.Value("configuration", $"{config.Token} ({config.Name})");
      await ModulesAsync(ctx, analytics, config.Token);
      await RulesAsync(ctx, analytics, config.Token);
    }

    // Every mutating analytics call is destructive: a module or rule is vendor-specific state
    // with no reliable round trip, and deleting one silently disables detection.
    r.SkipDestructive(
      "CreateAnalyticsModule", "ModifyAnalyticsModule", "DeleteAnalyticsModule",
      "CreateRule", "ModifyRule", "DeleteRule");
  }

  private static async Task ModulesAsync(ProbeContext ctx, AnalyticsService analytics, string token)
  {
    var r = ctx.Runner;

    var supported = await r.StepAsync($"GetSupportedAnalyticsModules [{token}]",
      () => analytics.GetSupportedAnalyticsModulesAsync(token),
      list => r.Table(["type", "max instances", "parameters", "cell layout"],
        list.Select(d => new List<object?>
        {
          d.Type,
          d.MaxInstances,
          string.Join(" ", d.Parameters.Select(p => $"{p.Name}:{p.Type}")),
          d.CellLayout is { } c ? $"{c.Columns}x{c.Rows}" : null,
        })));

    await r.StepAsync($"GetAnalyticsModules [{token}]",
      () => analytics.GetAnalyticsModulesAsync(token),
      list => PrintModules(r, list));

    if (supported?.FirstOrDefault() is { } first)
      await r.StepAsync($"GetAnalyticsModuleOptionsXml [{first.Type}]",
        () => analytics.GetAnalyticsModuleOptionsXmlAsync(first.Type, token),
        xml => PrintXml(r, xml));
    else
      r.Skip("GetAnalyticsModuleOptionsXml", "the camera advertised no module types");
  }

  private static async Task RulesAsync(ProbeContext ctx, AnalyticsService analytics, string token)
  {
    var r = ctx.Runner;

    var supported = await r.StepAsync($"GetSupportedRules [{token}]",
      () => analytics.GetSupportedRulesAsync(token),
      list => r.Table(["type", "max instances", "parameters", "cell layout"],
        list.Select(d => new List<object?>
        {
          d.Type,
          d.MaxInstances,
          string.Join(" ", d.Parameters.Select(p => $"{p.Name}:{p.Type}")),
          d.CellLayout is { } c ? $"{c.Columns}x{c.Rows}" : null,
        })));

    await r.StepAsync($"GetRules [{token}]",
      () => analytics.GetRulesAsync(token),
      list => PrintModules(r, list));

    await r.StepAsync($"GetSupportedRulesXml [{token}]",
      () => analytics.GetSupportedRulesXmlAsync(token),
      xml => PrintXml(r, xml));

    if (supported?.FirstOrDefault() is { } first)
      await r.StepAsync($"GetRuleOptionsXml [{first.Type}]",
        () => analytics.GetRuleOptionsXmlAsync(first.Type, token),
        xml => PrintXml(r, xml));
    else
      r.Skip("GetRuleOptionsXml", "the camera advertised no rule types");
  }

  private static void PrintModules(ProbeRunner r, IReadOnlyList<OnvifAnalyticsModule> modules)
  {
    r.Table(["name", "type", "simple items", "element items"],
      modules.Select(m => new List<object?>
      {
        m.Name,
        m.Type,
        string.Join(" ", m.SimpleItems.Select(s => $"{s.Name}={s.Value}")),
        string.Join(" ", m.ElementItems.Select(e => e.Name)),
      }));

    // The structured parameters are where polygons and schedules live. They have no fixed schema
    // across vendors, which is why the library hands them over as raw XML and why any Modify has
    // to round-trip them untouched.
    foreach (var element in modules.SelectMany(m => m.ElementItems))
    {
      r.Value("element item", element.Name);
      r.Block(ProbeRunner.PrettyXml(element.Xml), maxLines: 12);
    }
  }

  private static void PrintXml(ProbeRunner r, IReadOnlyList<string> documents)
  {
    if (documents.Count == 0) { r.Note("(no XML returned)"); return; }
    foreach (var document in documents) r.Block(ProbeRunner.PrettyXml(document), maxLines: 25);
  }
}
