namespace OnvifLib.Probe;

/// <summary>
/// Edit these to point the probe at your camera without setting anything on the command line
/// or in the environment. They are the lowest-priority source: CLI &gt; environment &gt; these.
/// </summary>
public static class Defaults
{
  public const string Ip = "192.168.1.10";
  public const int Port = 80;
  public const string User = "admin";
  public const string Password = "";
  public const double TimeoutSeconds = 15;

  public const int EventsSeconds = 20;
  public const int MaxResults = 100;
  public const string ScanPorts = "80,8000,8080,2020";
}

/// <summary>All probe sections, in the order they run.</summary>
public static class Sections
{
  public const string Discovery = "discovery";
  public const string Connect = "connect";
  public const string Device = "device";
  public const string Media = "media";
  public const string Ptz = "ptz";
  public const string Imaging = "imaging";
  public const string Events = "events";
  public const string Analytics = "analytics";
  public const string Recording = "recording";
  public const string DeviceIo = "deviceio";

  public static readonly string[] All =
  [
    Discovery, Connect, Device, Media, Ptz, Imaging, Events, Analytics, Recording, DeviceIo
  ];
}

public sealed class ProbeOptions
{
  public string Ip { get; private set; } = Defaults.Ip;
  public int Port { get; private set; } = Defaults.Port;
  public string User { get; private set; } = Defaults.User;
  public string Password { get; private set; } = Defaults.Password;
  public double TimeoutSeconds { get; private set; } = Defaults.TimeoutSeconds;
  public string? XAddr { get; private set; }

  /// <summary>Enables the reversible writes described in the README. Never enables destructive calls.</summary>
  public bool AllowWrites { get; private set; }

  /// <summary>
  /// Pulsing a relay is electrically reversible but physically is not: the output is usually
  /// wired to a door strike, a gate or an alarm. It gets its own opt-in rather than riding
  /// along with --allow-writes.
  /// </summary>
  public bool AllowRelay { get; private set; }
  public bool Verbose { get; private set; }

  public bool Discovery { get; private set; }
  public double DiscoveryTimeoutSeconds { get; private set; } = 4;
  public string? ScanFrom { get; private set; }
  public string? ScanTo { get; private set; }
  public List<int> ScanPorts { get; private set; } = ParsePorts(Defaults.ScanPorts);

  public int EventsSeconds { get; private set; } = Defaults.EventsSeconds;
  public int MaxResults { get; private set; } = Defaults.MaxResults;

  public string? JsonPath { get; private set; }
  public string? SnapshotDir { get; private set; }

  private HashSet<string>? _only;
  private readonly HashSet<string> _skip = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// True when the section should run. `--only` restricts to a set; `--skip` removes from it.
  /// `connect` is never gated — every other section needs the session it builds.
  /// </summary>
  public bool IsEnabled(string section)
  {
    if (section == Sections.Connect) return true;
    if (_skip.Contains(section)) return false;
    return _only == null || _only.Contains(section);
  }

  public static ProbeOptions? Parse(string[] args, out string? error)
  {
    error = null;
    var o = new ProbeOptions();

    // Environment first, so command-line arguments can override it.
    o.Ip = Env("ONVIF_IP") ?? o.Ip;
    o.User = Env("ONVIF_USER") ?? o.User;
    o.Password = Env("ONVIF_PASSWORD") ?? o.Password;
    o.XAddr = Env("ONVIF_XADDR") ?? o.XAddr;
    if (Env("ONVIF_PORT") is { } envPort && int.TryParse(envPort, out var p)) o.Port = p;
    if (Env("ONVIF_TIMEOUT") is { } envTo && double.TryParse(envTo, out var t)) o.TimeoutSeconds = t;

    for (var i = 0; i < args.Length; i++)
    {
      var a = args[i];
      switch (a)
      {
        case "--ip": if (!Next(args, ref i, out var ip)) { error = "--ip requires a value"; return null; } o.Ip = ip; break;
        case "--user": if (!Next(args, ref i, out var u)) { error = "--user requires a value"; return null; } o.User = u; break;
        case "--password": if (!Next(args, ref i, out var pw)) { error = "--password requires a value"; return null; } o.Password = pw; break;
        case "--xaddr": if (!Next(args, ref i, out var xa)) { error = "--xaddr requires a value"; return null; } o.XAddr = xa; break;

        case "--port":
          if (!Next(args, ref i, out var ps) || !int.TryParse(ps, out var pv)) { error = "--port requires a number"; return null; }
          o.Port = pv; break;
        case "--timeout":
          if (!Next(args, ref i, out var ts) || !double.TryParse(ts, out var tv) || tv <= 0) { error = "--timeout requires a positive number of seconds"; return null; }
          o.TimeoutSeconds = tv; break;
        case "--events-seconds":
          if (!Next(args, ref i, out var es) || !int.TryParse(es, out var ev) || ev < 0) { error = "--events-seconds requires a non-negative number"; return null; }
          o.EventsSeconds = ev; break;
        case "--max-results":
          if (!Next(args, ref i, out var ms) || !int.TryParse(ms, out var mv) || mv <= 0) { error = "--max-results requires a positive number"; return null; }
          o.MaxResults = mv; break;
        case "--discovery-timeout":
          if (!Next(args, ref i, out var ds) || !double.TryParse(ds, out var dv) || dv <= 0) { error = "--discovery-timeout requires a positive number of seconds"; return null; }
          o.DiscoveryTimeoutSeconds = dv; break;

        case "--allow-writes": o.AllowWrites = true; break;
        case "--allow-relay": o.AllowRelay = true; o.AllowWrites = true; break;
        case "--verbose": o.Verbose = true; break;
        case "--discovery": o.Discovery = true; break;

        case "--scan":
          if (!Next(args, ref i, out var from) || !Next(args, ref i, out var to)) { error = "--scan requires two addresses: --scan <from> <to>"; return null; }
          o.ScanFrom = from; o.ScanTo = to; o.Discovery = true; break;
        case "--scan-ports":
          if (!Next(args, ref i, out var sp)) { error = "--scan-ports requires a comma-separated list"; return null; }
          o.ScanPorts = ParsePorts(sp);
          if (o.ScanPorts.Count == 0) { error = "--scan-ports contained no valid port numbers"; return null; }
          break;

        case "--json": if (!Next(args, ref i, out var jp)) { error = "--json requires a path"; return null; } o.JsonPath = jp; break;
        case "--save-snapshot": if (!Next(args, ref i, out var sd)) { error = "--save-snapshot requires a directory"; return null; } o.SnapshotDir = sd; break;

        case "--only":
          if (!Next(args, ref i, out var only)) { error = "--only requires a comma-separated list of sections"; return null; }
          o._only = new HashSet<string>(SplitList(only), StringComparer.OrdinalIgnoreCase);
          if (UnknownSection(o._only) is { } badOnly) { error = $"--only: unknown section '{badOnly}'. Known: {string.Join(", ", Sections.All)}"; return null; }
          break;
        case "--skip":
          if (!Next(args, ref i, out var skip)) { error = "--skip requires a comma-separated list of sections"; return null; }
          foreach (var s in SplitList(skip)) o._skip.Add(s);
          if (UnknownSection(o._skip) is { } badSkip) { error = $"--skip: unknown section '{badSkip}'. Known: {string.Join(", ", Sections.All)}"; return null; }
          break;

        case "-h" or "--help":
          error = "";   // empty string means "help was requested", not a usage mistake
          return null;

        default:
          error = $"unknown argument '{a}' (try --help)";
          return null;
      }
    }

    return o;
  }

  private static string? Env(string name)
  {
    var v = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrEmpty(v) ? null : v;
  }

  private static bool Next(string[] args, ref int i, out string value)
  {
    if (i + 1 >= args.Length) { value = ""; return false; }
    value = args[++i];
    return true;
  }

  private static IEnumerable<string> SplitList(string s) =>
    s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  private static string? UnknownSection(IEnumerable<string> sections) =>
    sections.FirstOrDefault(s => !Sections.All.Contains(s, StringComparer.OrdinalIgnoreCase));

  private static List<int> ParsePorts(string s) =>
    SplitList(s).Select(x => int.TryParse(x, out var v) ? v : -1).Where(v => v is > 0 and <= 65535).ToList();

  public const string HelpText = """
    OnvifLib.Probe — exercises the whole OnvifLib public API against a camera and reports what works.

    USAGE
      dotnet run --project samples/OnvifLib.Probe -- [options]

    CONNECTION            (CLI overrides environment, which overrides the constants in ProbeOptions.cs)
      --ip <addr>          ONVIF_IP          camera address
      --port <n>           ONVIF_PORT        ONVIF service port (default 80)
      --user <name>        ONVIF_USER        username
      --password <pw>      ONVIF_PASSWORD    password
      --timeout <sec>      ONVIF_TIMEOUT     per-call timeout baked into the binding (default 15)
      --xaddr <url>        ONVIF_XADDR       full device_service URL, for non-standard paths

    WHAT IT MAY DO
      --allow-writes       also run reversible writes (PTZ nudge + return, temporary preset,
                           imaging change + restore, no-op re-writes of configs).
                           Destructive calls (reboot, delete, clock changes, auxiliary commands,
                           analytics create/modify/delete) are never run and are reported as SKIP.
      --allow-relay        additionally pulse each relay output on and off (implies --allow-writes).
                           Separate because a relay is usually wired to a door, gate or alarm:
                           electrically reversible, physically not.

    SELECTION
      --only <a,b,...>     run only these sections
      --skip <a,b,...>     run everything but these
                           sections: discovery connect device media ptz imaging events
                                     analytics recording deviceio

    DISCOVERY (runs before connecting; works without credentials)
      --discovery                  WS-Discovery multicast probe
      --discovery-timeout <sec>    probe duration (default 4)
      --scan <from> <to>           brute-force an IP range with CameraScanner (implies --discovery)
      --scan-ports <a,b,...>       ports to try when scanning (default 80,8000,8080,2020)

    OUTPUT
      --verbose            full stack traces and the SOAP request/response dump on stderr
      --json <path>        write the machine-readable run report
      --save-snapshot <dir>  save fetched snapshots into <dir>
      --events-seconds <n> how long to listen for events (default 20; 0 skips the wait)
      --max-results <n>    cap for recording searches (default 100)
      -h, --help           this text

    EXIT CODES
      0  everything that ran passed          2  could not connect to the camera
      1  at least one step failed            3  bad arguments
    """;
}
