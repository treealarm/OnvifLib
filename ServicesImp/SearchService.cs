using SearchServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  /// <summary>
  /// ONVIF Profile G RecordingSearch — what is on the camera's own storage and for which periods.
  /// </summary>
  public class SearchService : OnvifServiceBase, IOnvifServiceFactory<SearchService>
  {
    public const string WSDL_V10 = "http://www.onvif.org/ver10/search/wsdl";

    // A device with a broken SearchState would otherwise spin here forever, and VmsOnvif is
    // single-instance — one stuck camera would starve every other ONVIF call.
    private const int MaxSearchPolls = 40;
    private const int PollWaitSeconds = 2;

    private SearchPortClient? _client;

    protected SearchService(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls() => new[] { WSDL_V10 };

    public static async Task<SearchService?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new SearchService(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected override async Task InitializeAsync()
    {
      await base.InitializeAsync();
      _client = _onvifClientFactory.CreateClient<SearchPortClient, SearchPort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _client.OpenAsync();
    }

    public async Task<OnvifEdgeRecordingSummary?> GetRecordingSummaryAsync()
    {
      if (_client == null) return null;
      var resp = await _client.GetRecordingSummaryAsync(new GetRecordingSummaryRequest());
      var summary = resp?.Summary;
      if (summary == null) return null;
      return new OnvifEdgeRecordingSummary(
        Normalize(summary.DataFrom),
        Normalize(summary.DataUntil),
        summary.NumberRecordings);
    }

    public async Task<OnvifEdgeRecording?> GetRecordingInformationAsync(string recordingToken)
    {
      if (_client == null) return null;
      var resp = await _client.GetRecordingInformationAsync(
        new GetRecordingInformationRequest { RecordingToken = recordingToken });
      return resp?.RecordingInformation == null ? null : ToRecording(resp.RecordingInformation);
    }

    /// <summary>
    /// Runs a FindRecordings search over [from, to] and returns everything the device reports.
    /// A null <paramref name="recordingToken"/> searches every recording on the device.
    /// </summary>
    public async Task<List<OnvifEdgeRecording>> FindRecordingsAsync(
      DateTime from,
      DateTime to,
      string? recordingToken,
      int maxResults,
      CancellationToken ct = default)
    {
      if (_client == null) return [];

      var scope = new SearchScope();
      if (!string.IsNullOrWhiteSpace(recordingToken))
        scope.IncludedRecordings = new[] { recordingToken };

      // The window is expressed in the RecordingInformationFilter XPath dialect — the only way
      // FindRecordings accepts a time range (SearchScope itself carries no timestamps).
      scope.RecordingInformationFilter =
        $"boolean(//Track[TrackType = \"Video\" and DataFrom <= \"{Iso(to)}\" and DataTo >= \"{Iso(from)}\"])";

      var find = await _client.FindRecordingsAsync(new FindRecordingsRequest
      {
        Scope = scope,
        MaxMatches = maxResults > 0 ? maxResults : 100,
        KeepAliveTime = $"PT{PollWaitSeconds * MaxSearchPolls}S",
      });

      var searchToken = find?.SearchToken;
      if (string.IsNullOrWhiteSpace(searchToken)) return [];

      var found = new List<OnvifEdgeRecording>();
      try
      {
        for (var poll = 0; poll < MaxSearchPolls; poll++)
        {
          ct.ThrowIfCancellationRequested();

          var results = await _client.GetRecordingSearchResultsAsync(new GetRecordingSearchResultsRequest
          {
            SearchToken = searchToken,
            MinResults = 1,
            MaxResults = maxResults > 0 ? maxResults : 100,
            WaitTime = $"PT{PollWaitSeconds}S",
          });

          var list = results?.ResultList;
          if (list == null) break;

          // Results are cumulative across polls, so replace rather than append.
          found = (list.RecordingInformation ?? []).Select(ToRecording).ToList();
          if (list.SearchState == SearchState.Completed) break;
        }
      }
      finally
      {
        // Devices allow only a handful of concurrent searches; leaking tokens wedges the service
        // for everyone until they time out.
        try { await _client.EndSearchAsync(new EndSearchRequest { SearchToken = searchToken }); }
        catch (Exception ex) { _logger?.Error($"ONVIF EndSearch failed for {_url}: {ex.Message}"); }
      }

      return found;
    }

    /// <summary>
    /// Flattens found recordings into importable spans. Spans the camera is still writing have no
    /// end; they are closed at <paramref name="windowEnd"/> so the caller can still import them.
    /// </summary>
    public static List<OnvifEdgeInterval> ToIntervals(
      IEnumerable<OnvifEdgeRecording> recordings,
      DateTime? windowEnd)
    {
      var intervals = new List<OnvifEdgeInterval>();
      foreach (var rec in recordings)
      {
        foreach (var track in rec.Tracks)
        {
          if (track.DataFrom is not { } from) continue;
          var until = track.DataTo;
          if (until == null || until <= from) until = windowEnd;
          if (until != null && until <= from) continue;
          intervals.Add(new OnvifEdgeInterval(rec.RecordingToken, track.TrackToken, from, until));
        }
      }
      return intervals.OrderBy(i => i.From).ToList();
    }

    private static OnvifEdgeRecording ToRecording(RecordingInformation info) => new(
      info.RecordingToken ?? string.Empty,
      info.Source?.Name ?? string.Empty,
      info.Source?.Description ?? string.Empty,
      info.EarliestRecordingSpecified ? Normalize(info.EarliestRecording) : null,
      info.LatestRecordingSpecified ? Normalize(info.LatestRecording) : null,
      info.Content ?? string.Empty,
      info.RecordingStatus.ToString(),
      (info.Track ?? []).Select(t => new OnvifEdgeTrack(
        t.TrackToken ?? string.Empty,
        t.TrackType.ToString().ToLowerInvariant(),
        t.Description ?? string.Empty,
        Normalize(t.DataFrom),
        Normalize(t.DataTo))).ToList());

    // The schema makes these timestamps required, so an absent value arrives as default(DateTime)
    // rather than null — treat that as "not reported" instead of year 1.
    private static DateTime? Normalize(DateTime value) =>
      value == default ? null : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Iso(DateTime value) =>
      value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    public override void Dispose()
    {
      try { _client?.Close(); } catch { }
      base.Dispose();
    }
  }
}
