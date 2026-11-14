namespace OpenTail.Dolphin.Tests;

/// <summary>
/// A2 (ingestion-ideas section 6): a refused credential stops the run. Nothing goes on the retry list, the cursor is not saved past the last
/// commit, and the report says so in its own field. A throttling answer is not a configuration failure.
/// </summary>
public sealed class ConfigurationStopTests : IDisposable
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), "otail-a2-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly List<string> _log = [];

    public void Dispose()
    {
        try { File.Delete(_stateFile); } catch (IOException) { }
    }

    /// <summary>A scripted source. "U:x" upserts item x, "C:n" a checkpoint, "X" refuses the change stream (a configuration failure). Items in
    /// <paramref name="refuseFetch"/> throw a configuration failure on fetch; items in <paramref name="failFetch"/> throw an ordinary failure.</summary>
    private sealed class Source(string[] script, string[]? refuseFetch = null, string[]? failFetch = null, string? throttleFetch = null) : IIncrementalSourceConnector
    {
        public string SyncScope => "test:acme:things";
        public List<string> Fetched { get; } = [];

        public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var step in script)
            {
                await Task.Yield();
                if (step == "X")
                    throw new SourceConfigurationException("the sign-in was revoked");
                yield return step.StartsWith("C:", StringComparison.Ordinal)
                    ? new SourceChange.Checkpoint(step[2..])
                    : new SourceChange.Upsert(new SourceItem(step[2..], step[2..] + ".txt", "txt", 1, DateTimeOffset.UnixEpoch, "acme"));
            }
        }

        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var c in GetChangesAsync(null, cancellationToken))
                if (c is SourceChange.Upsert u) yield return u.Item;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        {
            Fetched.Add(item.Id);
            if (refuseFetch is not null && refuseFetch.Contains(item.Id))
                throw new SourceConfigurationException($"the sign-in cannot read {item.Id}");
            if (throttleFetch is not null && throttleFetch == item.Id)
                throw new HttpRequestException("429 Too Many Requests");
            if (failFetch is not null && failFetch.Contains(item.Id))
                throw new HttpRequestException("timeout");
            return Task.FromResult(Encoding.UTF8.GetBytes("text of " + item.Id + new string('.', 60)));
        }
    }

    private sealed class Extractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) =>
            new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class Exporter(List<string> log) : IConnectivityExporter
    {
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            log.Add("export " + document.SourceRef);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private Task<IngestionReport> Run(Source source, bool incremental = true) =>
        new ConnectivityPipeline(source, new Extractor(), [new Exporter(_log)],
            incremental
                ? new PipelineOptions(SyncStore: new FileSyncCursorStore(_stateFile), Sync: new SyncOptions(CheckpointEveryItems: 1))
                : new PipelineOptions())
        .RunAsync();

    private (string? Cursor, List<string> Retries) Saved()
    {
        var state = new FileSyncCursorStore(_stateFile).LoadAsync("test:acme:things").GetAwaiter().GetResult();
        var (cursor, retries) = RetryEnvelope.Read(state?.Cursor);
        return (cursor, retries.Select(r => r.Item.Id).ToList());
    }

    [Fact]
    public async Task A_refused_fetch_stops_the_run_and_leaves_the_cursor_and_the_retry_list_where_they_were()
    {
        // Run 1 leaves a retry for "b" and the cursor at "2".
        await Run(new Source(["U:a", "C:1", "U:b", "C:2"], failFetch: ["b"]));
        var (before, retriesBefore) = Saved();
        Assert.Equal("2", before);
        Assert.Equal(["b"], retriesBefore);

        // Run 2: the sign-in is refused on "c". "d" is never fetched, and nothing about "c" is retried.
        var second = new Source(["U:c", "C:3", "U:d", "C:4"], refuseFetch: ["c"]);
        var report = await Run(second);

        Assert.NotNull(report.ConfigurationFailure);
        Assert.Contains("the sign-in cannot read c", report.ConfigurationFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("d", second.Fetched);
        Assert.Contains(report.Errors, e => e.Contains("The run stopped: the source refused its configuration", StringComparison.Ordinal));
        var (cursor, retries) = Saved();
        Assert.Equal("2", cursor);
        Assert.DoesNotContain("c", retries);
        Assert.DoesNotContain("export c", _log);
    }

    [Fact]
    public async Task A_refused_change_stream_commits_nothing_past_the_last_commit()
    {
        await Run(new Source(["U:a", "C:1"]));
        Assert.Equal("1", Saved().Cursor);

        // The stream is refused after "b" and before any checkpoint: "b" is exported, but the cursor stays at "1".
        var report = await Run(new Source(["U:b", "X", "U:c", "C:3"]));

        Assert.NotNull(report.ConfigurationFailure);
        Assert.Equal("1", Saved().Cursor);
        Assert.DoesNotContain(Saved().Retries, r => r == "b");
    }

    [Fact]
    public async Task A_throttling_answer_is_an_ordinary_failure_and_goes_on_the_retry_list()
    {
        var report = await Run(new Source(["U:a", "C:1", "U:b", "C:2"], throttleFetch: "b"));

        Assert.Null(report.ConfigurationFailure);
        Assert.Equal(["b"], Saved().Retries);
        Assert.Equal(1, report.RetriesPending);
    }

    [Fact]
    public async Task A_standalone_run_stops_at_a_refused_fetch_and_says_so_in_the_report()
    {
        var source = new Source(["U:a", "U:b", "U:c"], refuseFetch: ["b"]);

        var report = await Run(source, incremental: false);

        Assert.NotNull(report.ConfigurationFailure);
        Assert.Contains("export a", _log);
        Assert.DoesNotContain("c", source.Fetched);
        Assert.Equal(0, report.RetriesPending);
    }
}
