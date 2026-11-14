using TT = Transformations.Text;

namespace OpenTail.Dolphin.Tests;

/// <summary>
/// B4 (ingestion-ideas section 6): the extraction library's warnings are passed through the extractor, and the pipeline records a content-type
/// mismatch as a validation warning with its own code. The text is still exported.
/// </summary>
public sealed class ExtractionWarningTests
{
    private sealed class Mismatched : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) =>
            new(true, "The quarterly report.", Warnings: [new TextExtractionWarning(TextExtractionWarningCodes.ContentTypeMismatch, fileName, "a PDF named .docx")]);
    }

    private sealed class Plain : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, "The quarterly report.");
    }

    private sealed class Source : ISourceConnector
    {
        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new SourceItem("a", "report.docx", "docx", 1, DateTimeOffset.UnixEpoch, "acme");
            await Task.CompletedTask;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.UTF8.GetBytes("a PDF body"));
    }

    private sealed class Exporter(List<string> log) : IConnectivityExporter
    {
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            log.Add(document.SourceRef);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_content_type_mismatch_is_a_validation_warning_with_its_own_code_and_the_text_is_still_exported()
    {
        var exported = new List<string>();
        var report = await new ConnectivityPipeline(new Source(), new Mismatched(), [new Exporter(exported)], new PipelineOptions()).RunAsync();

        var issue = Assert.Single(report.ValidationIssues, i => i.Code == "CONTENT_TYPE_MISMATCH");
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Contains("the content does not match its file type", issue.Message, StringComparison.Ordinal);
        Assert.Equal(["a"], exported);
    }

    [Fact]
    public async Task Without_a_mismatch_the_pipeline_records_no_such_warning()
    {
        var report = await new ConnectivityPipeline(new Source(), new Plain(), [new Exporter([])], new PipelineOptions()).RunAsync();

        Assert.DoesNotContain(report.ValidationIssues, i => i.Code == "CONTENT_TYPE_MISMATCH");
    }

    [Fact]
    public void The_library_mismatch_is_passed_through_under_dolphins_own_code_and_other_codes_are_unchanged()
    {
        // The library reports a mismatch only after a successful read, and no fixture file of another type is in the repository, so this tests
        // the mapping with the library's own warning type. The end-to-end case is not reproduced here.
        var mapped = TransformationsTextExtractor.ConvertWarnings(
        [
            new TT.ExtractionWarning(TT.ExtractionWarningCodes.ContentTypeMismatch, "quarterly.docx", "a PDF body"),
            new TT.ExtractionWarning("some-other-code", "quarterly.docx", "unrelated"),
        ])!;

        Assert.Equal(TextExtractionWarningCodes.ContentTypeMismatch, mapped[0].Code);
        Assert.Equal("a PDF body", mapped[0].Message);
        Assert.Equal("some-other-code", mapped[1].Code);
        Assert.Null(TransformationsTextExtractor.ConvertWarnings([]));
    }
}
