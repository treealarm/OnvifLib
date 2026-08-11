using OnvifLib.Probe;
using OnvifLib.Probe.Steps;

var options = ProbeOptions.Parse(args, out var error);
if (options is null)
{
  if (error is "") { Console.WriteLine(ProbeOptions.HelpText); return 0; }
  Console.Error.WriteLine($"OnvifLib.Probe: {error}");
  return 3;
}

var logger = new ConsoleLogger(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
var report = new Report
{
  Target = Camera.CreateUrl(options.Ip, options.Port, options.XAddr),
  User = options.User,
  AllowWrites = options.AllowWrites,
};
var runner = new ProbeRunner(options, report);
var context = new ProbeContext(options, runner, logger);

using var cancellation = new CancellationTokenSource();
context.Cancellation = cancellation.Token;
Console.CancelKeyPress += (_, e) =>
{
  // Take the first Ctrl+C ourselves so the run can finish its summary; a second one kills us.
  if (cancellation.IsCancellationRequested) return;
  e.Cancel = true;
  cancellation.Cancel();
  Console.Error.WriteLine("cancelling — press Ctrl+C again to abort immediately");
};

Con.Line(ConsoleColor.White, $"OnvifLib.Probe → {report.Target}");
Console.WriteLine($"  user       {(string.IsNullOrEmpty(options.User) ? "(anonymous)" : options.User + "/***")}");
Console.WriteLine($"  timeout    {options.TimeoutSeconds:0.#} s per call");
Console.WriteLine($"  writes     {(options.AllowWrites ? "reversible writes ENABLED (--allow-writes); destructive calls still skipped" : "read-only (pass --allow-writes to exercise setters)")}");

var exitCode = 0;
try
{
  if (options.Discovery && options.IsEnabled(Sections.Discovery))
    await DiscoverySteps.RunAsync(context);

  if (!await ConnectSteps.RunAsync(context))
  {
    exitCode = 2;
  }
  else
  {
    if (options.IsEnabled(Sections.Device)) await DeviceSteps.RunAsync(context);
    if (options.IsEnabled(Sections.Media)) await MediaSteps.RunAsync(context);
    if (options.IsEnabled(Sections.Ptz)) await PtzSteps.RunAsync(context);
    if (options.IsEnabled(Sections.Imaging)) await ImagingSteps.RunAsync(context);
    if (options.IsEnabled(Sections.Events)) await EventSteps.RunAsync(context);
    if (options.IsEnabled(Sections.Analytics)) await AnalyticsSteps.RunAsync(context);
    if (options.IsEnabled(Sections.Recording)) await RecordingSteps.RunAsync(context);
    if (options.IsEnabled(Sections.DeviceIo)) await DeviceIoSteps.RunAsync(context);
  }
}
finally
{
  context.DisposeServices();
}

PrintSummary(runner, report);

if (options.JsonPath is { } jsonPath)
{
  try
  {
    report.Write(jsonPath);
    Console.WriteLine($"  report written to {jsonPath}");
  }
  catch (Exception ex)
  {
    Con.Line(ConsoleColor.Red, $"  could not write {jsonPath}: {ex.Message}");
    if (exitCode == 0) exitCode = 1;
  }
}

// A failed step outranks a clean run, but "could not connect" (2) is the more specific answer
// and is kept.
if (exitCode == 0 && report.Fail > 0) exitCode = 1;
return exitCode;

static void PrintSummary(ProbeRunner runner, Report report)
{
  runner.Section("summary", "summary");

  var rows = report.Sections
    .Select(s => new List<object?> { s.Section, s.Ok, s.Fail, s.Skip, s.Ms })
    .ToList();
  rows.Add(["TOTAL", report.Ok, report.Fail, report.Skipped, report.Steps.Sum(s => s.Ms)]);
  runner.Table(["section", "ok", "fail", "skip", "ms"], rows);

  Console.WriteLine();
  if (report.Connected)
  {
    runner.Value("available", report.AvailableServices.Count > 0 ? string.Join(", ", report.AvailableServices) : "none");
    runner.Value("not available", report.UnavailableServices.Count > 0 ? string.Join(", ", report.UnavailableServices) : "none");
    Console.WriteLine();
  }

  if (report.Fail == 0 && report.Connected)
    Con.Line(ConsoleColor.Green, $"  PASS — {report.Ok} ok, {report.Skipped} skipped, no failures");
  else if (!report.Connected)
    Con.Line(ConsoleColor.Red, "  COULD NOT CONNECT — nothing beyond the connect section was attempted");
  else
    Con.Line(ConsoleColor.Red, $"  {report.Fail} FAILED — {report.Ok} ok, {report.Skipped} skipped");
}
