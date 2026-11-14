# Using and writing connectors

## Ingest a folder to JSONL

```csharp
using OpenTail.Dolphin.Files.Connectors;
using OpenTail.Dolphin.Files.Export;
using OpenTail.Dolphin.Files.Extraction;
using OpenTail.Dolphin.Files.Pipeline;

var source = new FolderSourceConnector("/data/docs", tenantContext: "acme");
await using var jsonl = new JsonlDocumentExporter("out.jsonl");

var pipeline = new ConnectivityPipeline(
    source,
    new TransformationsTextExtractor(),
    [jsonl],
    new PipelineOptions(DeduplicateByContentHash: true));

var report = await pipeline.RunAsync();
Console.WriteLine($"{report.DocumentsProduced} documents, {report.ItemsFailed} failed");
```

`FolderSourceConnector` takes an optional `FolderConnectorOptions` (include/exclude patterns, `MaxFileSizeBytes`, `MaxDepth`,
`ModifiedSince`, and so on). For incremental runs, pass a `FolderSyncStateStore` as `stateStore` and call `SaveStateAsync` after
the run. Without a store, `SaveStateAsync` does nothing. The pipeline tells the connector which files it did not process
(`ISourceItemFeedback.NotProcessed`), and those are left out of the saved state, so they are retried next run. A connector with
sync state of its own should implement `ISourceItemFeedback` too.

Check `report.ValidationIssues` and `report.Errors` after a run: warnings do not stop an item, errors do.

## Write a new source

Implement `ISourceConnector` (in `OpenTail.Dolphin.Files.Contracts`):

```csharp
public interface ISourceConnector
{
    IAsyncEnumerable<SourceItem> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default);
}
```

Guidelines, taken from the existing connectors:

- Make `SourceItem.Id` stable and unique within the source; it becomes the document's source reference.
- Put a human-readable name in `DisplayName`. The pipeline uses it as the title, dropping the item's `Extension` only when the
  name ends with it (see [architecture](architecture.md#the-pipeline)).
- Set `Modified` from the source system where there is one, so incremental runs and `ModifiedSince` work.
- Set `TenantContext` on every item.
- Pass what you know about the item in `SourceItem.Metadata`; it lands in the document under `source.` (`SourceKeys`: `url`,
  `container`, `owner`, and the `acl.*` keys). For permissions use `SourceAcl.Captured(visibility, principals)`, or
  `SourceAcl.NotCaptured()` when the connector does not read them. Consumers treat anything not captured as restricted
  (`SourceAcl.IsCaptured`): absence is not permission.
- Read only. Never write back to the source.
- Honour the `CancellationToken`.
- Take an allow-list of fields or columns rather than reading everything (see `CsvConnectorConfig` and the Sql connector).
- Pair the connector with an `ITextExtractor` that understands the bytes you return. For structured sources such as CSV rows,
  the package supplies its own extractor (for example `CsvRowTextExtractor`).
- Report failures by kind (ingestion-ideas A2). Throw `SourceConfigurationException` when the source refuses the sign-in itself
  (a 401 that stays after one fresh token, or a 403 that names the token or a missing scope or permission): the run stops, and the
  operator is told to fix the configuration. A 403 on one item is an ordinary failure, not a refused sign-in. Before you throw, try
  one fresh token: an access token can expire mid-run. Do not throw it for throttling (429, or a provider's quota error): that is an
  ordinary failed item and is retried. For any other failure throw an ordinary exception with the reason; it is put on the retry list.
- If the source can check its credential cheaply, implement `IConnectionCheck`: one read-only request, answered at once, with the
  kinds of `ConnectionCheckStatus`.

## Write a new exporter

Implement `IConnectivityExporter`: `ExportAsync` is called per document, `FlushAsync` once at the end. The existing exporters are
`JsonlDocumentExporter` and `MarkdownDocumentExporter`. `JsonReportExporter` is separate: it writes the `IngestionReport` that
`RunAsync` returns, not documents.

## Tests

Tests live in `OpenTail.Dolphin.Tests` and run with `dotnet test --solution OpenTail.Dolphin.slnx`. A new connector
should come with tests there.
