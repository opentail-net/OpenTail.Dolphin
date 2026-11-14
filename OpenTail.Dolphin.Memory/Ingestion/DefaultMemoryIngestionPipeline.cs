// Moved from opentail's OpenTail.Core.Memory (DefaultMemoryIngestionPipeline.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Dolphin.Memory;

public interface IMemoryIngestionExtractor
{
    Task<MemoryNormalizedEnvelope> ExtractAsync(
        MemoryNormalizedEnvelope envelope,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionChunker
{
    Task<IReadOnlyList<MemoryChunk>> ChunkAsync(
        MemoryNormalizedEnvelope envelope,
        string parentRecordId,
        CancellationToken cancellationToken = default);
}

/// <summary>Embeds chunks. It embeds <see cref="MemoryChunk.IndexText"/> when it is set and <see cref="MemoryChunk.Content"/> otherwise,
/// never a metadata value: metadata is filtered on, not searched.</summary>
public interface IMemoryIngestionEmbedder
{
    Task<IReadOnlyList<MemoryChunk>> EmbedAsync(
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionStore
{
    Task<string> StoreAsync(
        MemoryNormalizedEnvelope envelope,
        IReadOnlyList<MemoryChunk> chunks,
        MemoryIngestionDecision decision,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionLinker
{
    Task LinkAsync(
        string recordId,
        MemoryNormalizedEnvelope envelope,
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpMemoryIngestionExtractor : IMemoryIngestionExtractor
{
    public Task<MemoryNormalizedEnvelope> ExtractAsync(MemoryNormalizedEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.FromResult(envelope);
}

/// <summary>Options of <see cref="DefaultMemoryIngestionChunker"/>. The defaults are the chunker's behaviour before batch 12.</summary>
/// <param name="MaxChunkLength">The longest chunk, in characters (default 480). A row of a table longer than this is a chunk on its own.</param>
/// <param name="RecogniseTables">Cut Markdown tables and delimited blocks on row boundaries. A Markdown table repeats its header and separator row in
/// each piece; a block without a separator row repeats nothing. On by default:
/// the quality gate's numbers do not change on any backend, and rows are no longer cut in half (docs/design/memory.md).</param>
/// <param name="FillIndexText">Fill <see cref="MemoryChunk.IndexText"/> with the title, the summary and the content. Off by default: on the
/// quality corpus a title moved one VectorData result to the wrong record (docs/design/memory.md).</param>
public sealed record DefaultMemoryChunkerOptions(int MaxChunkLength = 480, bool RecogniseTables = true, bool FillIndexText = false);

public sealed class DefaultMemoryIngestionChunker : IMemoryIngestionChunker
{
    private static readonly Regex ParagraphSplitRegex = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly Regex ChatTurnSplitRegex = new(@"\r?\n(?=(?:Sender:|Author:|Actor:|Coordinator:|Invoker:|Reporter:|Message:|Question:|Answer:))", RegexOptions.Compiled);
    private static readonly Regex MarkdownSeparatorRegex = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private readonly int _maxChunkLength;
    private readonly bool _recogniseTables;
    private readonly bool _fillIndexText;

    /// <summary>Options: the chunk limit (default 480), table recognition (on) and index text (off). See <see cref="DefaultMemoryChunkerOptions"/>.</summary>
    public DefaultMemoryIngestionChunker(DefaultMemoryChunkerOptions? options = null)
    {
        var chosen = options ?? new DefaultMemoryChunkerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(chosen.MaxChunkLength, 40);
        _maxChunkLength = chosen.MaxChunkLength;
        _recogniseTables = chosen.RecogniseTables;
        _fillIndexText = chosen.FillIndexText;
    }

    private sealed record TextPiece(string Content, IReadOnlyList<string>? Tags = null);
    private sealed record TableBlock(IReadOnlyList<string> Header, IReadOnlyList<string> Rows);
    private sealed record TextBlock(string Text, TableBlock? Table);
    private sealed record ChunkSegment(string Content, MemoryChunkKind? KindHint = null, IReadOnlyList<string>? SegmentTags = null);

    public Task<IReadOnlyList<MemoryChunk>> ChunkAsync(MemoryNormalizedEnvelope envelope, string parentRecordId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);

        var content = envelope.Content?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return Task.FromResult<IReadOnlyList<MemoryChunk>>([]);

        var chunks = new List<MemoryChunk>();
        var ordinal = 0;
        var tableNumber = 0;

        foreach (var segment in BuildSegments(envelope, content))
        {
            foreach (var piece in ChunkText(segment.Content, ref tableNumber))
            {
                var chunkKind = ResolveChunkKind(envelope, piece.Content, segment.KindHint);
                var tags = BuildChunkTags(envelope, chunkKind, MergeTags(segment.SegmentTags, piece.Tags));

                chunks.Add(new MemoryChunk(
                    Id: $"chunk-{parentRecordId}-{ordinal}",
                    ParentRecordId: parentRecordId,
                    ChunkOrdinal: ordinal,
                    Kind: chunkKind,
                    Content: piece.Content,
                    Summary: envelope.Summary,
                    Tags: tags,
                    IndexText: IndexTextFor(envelope, piece.Content)));

                ordinal++;
            }
        }

        if (ShouldAddThreadSummaryChunk(envelope, chunks))
        {
            var summaryContent = BuildThreadSummaryContent(envelope, chunks);
            chunks.Add(new MemoryChunk(
                Id: $"chunk-{parentRecordId}-{ordinal}",
                ParentRecordId: parentRecordId,
                ChunkOrdinal: ordinal,
                Kind: MemoryChunkKind.Summary,
                Content: summaryContent,
                Summary: envelope.Summary,
                Tags: BuildChunkTags(envelope, MemoryChunkKind.Summary, ["semantic.source:thread-summary"]),
                IndexText: IndexTextFor(envelope, summaryContent)));
        }

        return Task.FromResult<IReadOnlyList<MemoryChunk>>(chunks);
    }

    /// <summary>
    /// The chunk texts of one segment. Without table recognition this is the sentence split, as before. With it, a table block is cut on
    /// row boundaries (see <see cref="TablePieces"/>) and the prose between tables goes through the sentence split.
    /// </summary>
    private List<TextPiece> ChunkText(string text, ref int tableNumber)
    {
        if (!_recogniseTables)
            return ChunkParagraphBySentence(text).Select(static c => new TextPiece(c)).ToList();

        var pieces = new List<TextPiece>();
        foreach (var block in SplitTextBlocks(text))
        {
            if (block.Table is null)
            {
                pieces.AddRange(ChunkParagraphBySentence(block.Text).Select(static c => new TextPiece(c)));
                continue;
            }

            tableNumber++;
            pieces.AddRange(TablePieces(block.Table, tableNumber));
        }

        return pieces;
    }

    /// <summary>Cuts the text into prose and tables. Lines are split on newline and rejoined the same way, so prose is unchanged.</summary>
    private static List<TextBlock> SplitTextBlocks(string text)
    {
        var lines = text.Split('\n');
        var blocks = new List<TextBlock>();
        var prose = new List<string>();
        var i = 0;
        while (i < lines.Length)
        {
            var run = TableRunLength(lines, i);
            if (run == 0)
            {
                prose.Add(lines[i]);
                i++;
                continue;
            }

            if (prose.Count > 0)
            {
                blocks.Add(new TextBlock(string.Join("\n", prose), null));
                prose.Clear();
            }

            var rows = lines[i..(i + run)];
            // Only a Markdown table has a header to repeat: a header row followed by a separator row. Any other block repeats nothing.
            var headerLength = rows.Length > 1 && MarkdownSeparatorRegex.IsMatch(rows[1]) ? 2 : 0;
            blocks.Add(new TextBlock(string.Empty, new TableBlock(rows[..headerLength], rows[headerLength..])));
            i += run;
        }

        if (prose.Count > 0)
            blocks.Add(new TextBlock(string.Join("\n", prose), null));
        return blocks;
    }

    /// <summary>The number of consecutive lines from <paramref name="start"/> with the same separators, when that is three or more; otherwise 0.
    /// One line that looks like a table is not a table.</summary>
    private static int TableRunLength(string[] lines, int start)
    {
        var shape = Separators(lines[start]);
        if (shape.Count == 0)
            return 0;

        var length = 1;
        while (start + length < lines.Length && Separators(lines[start + length]) == shape)
            length++;
        return length >= 3 ? length : 0;
    }

    /// <summary>The separators of a line: its tabs when it has any (one is enough), otherwise its pipes, which must be at least two. A line
    /// with one pipe is prose, and a pipe block needs three columns.</summary>
    private static (char Kind, int Count) Separators(string line)
    {
        var tabs = line.Count(static c => c == '\t');
        if (tabs > 0)
            return ('\t', tabs);
        var pipes = line.Count(static c => c == '|');
        return pipes >= 2 ? ('|', pipes) : ('\0', 0);
    }

    /// <summary>
    /// The pieces of one table. A Markdown table repeats its header and separator row at the top of each piece. Any other block repeats
    /// nothing, so each piece starts with its own first row, and a row appears in one piece only. Rows stay whole and in order, and each piece
    /// is within the limit. A row that cannot share the limit with the header is a piece on its own, never cut. Each piece is tagged
    /// table:n; the second and later pieces also carry continuation.
    /// </summary>
    private List<TextPiece> TablePieces(TableBlock table, int number)
    {
        var prefix = table.Header.Count == 0 ? string.Empty : string.Join("\n", table.Header) + "\n";
        var texts = new List<string>();
        var rows = new List<string>();
        foreach (var row in table.Rows)
        {
            if (prefix.Length + row.Length > _maxChunkLength)
            {
                AddPiece(texts, prefix, rows);
                texts.Add(row);
                continue;
            }

            // The piece with this row added: the prefix, the rows, one newline before each row, and the new row.
            var withRow = prefix.Length + rows.Sum(static r => r.Length) + rows.Count + row.Length;
            if (rows.Count > 0 && withRow > _maxChunkLength)
                AddPiece(texts, prefix, rows);
            rows.Add(row);
        }

        AddPiece(texts, prefix, rows);

        var tag = $"table:{number}";
        return texts
            .Select((text, index) => new TextPiece(text, index == 0 ? new[] { tag } : new[] { tag, "continuation" }))
            .ToList();
    }

    private static void AddPiece(List<string> texts, string prefix, List<string> rows)
    {
        if (rows.Count == 0)
            return;
        texts.Add(prefix + string.Join("\n", rows));
        rows.Clear();
    }

    private static IReadOnlyList<string>? MergeTags(IReadOnlyList<string>? segmentTags, IReadOnlyList<string>? pieceTags)
    {
        if (pieceTags is null)
            return segmentTags;
        return segmentTags is null ? pieceTags : [.. segmentTags, .. pieceTags];
    }

    /// <summary>The text that is embedded and keyword-indexed when FillIndexText is on: the title, then the summary, then the content.
    /// Null otherwise, so the content is what is indexed.</summary>
    private string? IndexTextFor(MemoryNormalizedEnvelope envelope, string text)
    {
        if (!_fillIndexText)
            return null;
        return string.Join("\n", new[] { envelope.Title, envelope.Summary, text }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static IEnumerable<ChunkSegment> BuildSegments(MemoryNormalizedEnvelope envelope, string content)
    {
        if (IsChatLikeSource(envelope.SourceType) && !string.IsNullOrWhiteSpace(envelope.ThreadRootExternalId))
        {
            var turns = ChatTurnSplitRegex
                .Split(content)
                .Select(static t => t.Trim())
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (turns.Count > 1)
            {
                foreach (var turn in turns)
                    yield return new ChunkSegment(turn, SegmentTags: ["semantic.source:chat-thread"]);

                foreach (var attachmentSegment in BuildAttachmentTextSegments(envelope))
                    yield return attachmentSegment;

                yield break;
            }
        }

        foreach (var paragraph in SplitParagraphs(content))
            yield return new ChunkSegment(paragraph);

        foreach (var attachmentSegment in BuildAttachmentTextSegments(envelope))
            yield return attachmentSegment;
    }

    private static IEnumerable<ChunkSegment> BuildAttachmentTextSegments(MemoryNormalizedEnvelope envelope)
    {
        if (envelope.Policy is { AllowDirectQuotation: false })
            yield break;

        if (envelope.Metadata is null)
            yield break;

        foreach (var entry in envelope.Metadata
                     .Where(static kvp => kvp.Key.StartsWith("attachment.text.", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var attachmentText = entry.Value?.Trim();
            if (string.IsNullOrWhiteSpace(attachmentText))
                continue;

            yield return new ChunkSegment(
                Content: $"AttachmentText: {attachmentText}",
                KindHint: MemoryChunkKind.RawEvidence,
                SegmentTags: ["semantic.source:attachment", $"attachment.key:{entry.Key}"]);
        }
    }

    private static IEnumerable<string> SplitParagraphs(string content)
        => ParagraphSplitRegex
            .Split(content)
            .Select(static p => p.Trim())
            .Where(static p => !string.IsNullOrWhiteSpace(p));

    private IEnumerable<string> ChunkParagraphBySentence(string paragraph)
    {
        var sentences = SentenceSplitRegex
            .Split(paragraph)
            .Select(static s => s.Trim())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (sentences.Count == 0)
        {
            foreach (var hardChunk in HardChunk(paragraph))
                yield return hardChunk;

            yield break;
        }

        var current = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (sentence.Length > _maxChunkLength)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                foreach (var hardChunk in HardChunk(sentence))
                    yield return hardChunk;

                continue;
            }

            var additionalLength = current.Length == 0 ? sentence.Length : sentence.Length + 1;
            if (current.Length > 0 && current.Length + additionalLength > _maxChunkLength)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');

            current.Append(sentence);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private IEnumerable<string> HardChunk(string text)
    {
        var normalized = text.Trim();
        for (var i = 0; i < normalized.Length; i += _maxChunkLength)
        {
            var length = Math.Min(_maxChunkLength, normalized.Length - i);
            yield return normalized.Substring(i, length).Trim();
        }
    }

    private static MemoryChunkKind ResolveChunkKind(MemoryNormalizedEnvelope envelope, string chunkContent, MemoryChunkKind? kindHint)
    {
        if (kindHint is not null && kindHint != MemoryChunkKind.Unknown)
            return kindHint.Value;

        if (IsGitHubReviewLike(envelope) && LooksLikeCodeDiff(chunkContent))
            return MemoryChunkKind.CodeContext;

        if (ContainsAny(chunkContent, "decision", "approved", "rejected", "resolved", "verdict"))
            return MemoryChunkKind.Decision;

        if (ContainsAny(chunkContent, "action item", "todo", "next step", "follow-up"))
            return MemoryChunkKind.ActionItem;

        if (chunkContent.Contains('?', StringComparison.Ordinal))
            return MemoryChunkKind.Question;

        if (ContainsAny(chunkContent, "answer", "response", "resolution"))
            return MemoryChunkKind.Answer;

        if (ContainsAny(chunkContent, "policy", "must", "required", "compliance"))
            return MemoryChunkKind.PolicyStatement;

        if (ContainsAny(chunkContent, "incident", "outage", "failure", "error"))
            return MemoryChunkKind.IncidentFact;

        return MemoryChunkKind.RawEvidence;
    }

    private static IReadOnlyList<string>? BuildChunkTags(MemoryNormalizedEnvelope envelope, MemoryChunkKind chunkKind, IReadOnlyList<string>? segmentTags)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (envelope.Metadata is not null)
        {
            foreach (var key in envelope.Metadata.Keys)
                tags.Add(key);
        }

        tags.Add($"semantic.kind:{chunkKind}");

        if (IsChatLikeSource(envelope.SourceType))
            tags.Add("semantic.source:chat-like");

        if (IsGitHubReviewLike(envelope))
            tags.Add("semantic.source:github-review");

        if (segmentTags is not null)
        {
            foreach (var tag in segmentTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                tags.Add(tag);
        }

        return tags.Count == 0 ? null : [.. tags];
    }

    private static bool ShouldAddThreadSummaryChunk(MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks)
        => IsChatLikeSource(envelope.SourceType)
           && !string.IsNullOrWhiteSpace(envelope.ThreadRootExternalId)
           && chunks.Count >= 4;

    private string BuildThreadSummaryContent(MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Summary))
            return $"ThreadSummary: {envelope.Summary}";

        var preview = string.Join(" ", chunks
            .Where(static c => c.Kind != MemoryChunkKind.Summary)
            .Take(3)
            .Select(static c => c.Content)
            .Where(static c => !string.IsNullOrWhiteSpace(c)));

        if (preview.Length > _maxChunkLength)
            preview = preview[.._maxChunkLength].TrimEnd() + "…";

        return string.IsNullOrWhiteSpace(preview)
            ? "ThreadSummary: Conversation thread summary unavailable."
            : $"ThreadSummary: {preview}";
    }

    private static bool IsChatLikeSource(MemoryIngestionSourceType sourceType)
        => sourceType is MemoryIngestionSourceType.Slack or MemoryIngestionSourceType.CrewHandoff;

    private static bool IsGitHubReviewLike(MemoryNormalizedEnvelope envelope)
        => envelope.SourceType == MemoryIngestionSourceType.GitHub
           && envelope.EventKind is MemoryIngestionEventKind.Review or MemoryIngestionEventKind.Comment or MemoryIngestionEventKind.PullRequest;

    private static bool LooksLikeCodeDiff(string content)
        => content.Contains("```diff", StringComparison.OrdinalIgnoreCase)
           || content.Contains("@@", StringComparison.Ordinal)
           || content.Contains("\n+", StringComparison.Ordinal)
           || content.Contains("\n-", StringComparison.Ordinal);

    private static bool ContainsAny(string content, params string[] tokens)
        => tokens.Any(token => content.Contains(token, StringComparison.OrdinalIgnoreCase));
}

public sealed class NoOpMemoryIngestionEmbedder : IMemoryIngestionEmbedder
{
    public Task<IReadOnlyList<MemoryChunk>> EmbedAsync(IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
        => Task.FromResult(chunks);
}

public sealed class BatchingMemoryIngestionEmbedder : IMemoryIngestionEmbedder
{
    private readonly IMemoryIngestionEmbedder _inner;
    private readonly int _batchSize;

    public BatchingMemoryIngestionEmbedder(IMemoryIngestionEmbedder inner, int batchSize = 32)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _batchSize = Math.Max(1, batchSize);
    }

    public async Task<IReadOnlyList<MemoryChunk>> EmbedAsync(IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count <= _batchSize)
            return await _inner.EmbedAsync(chunks, cancellationToken);

        var embedded = new List<MemoryChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i += _batchSize)
        {
            var take = Math.Min(_batchSize, chunks.Count - i);
            var batch = chunks.Skip(i).Take(take).ToList();
            var embeddedBatch = await _inner.EmbedAsync(batch, cancellationToken);
            embedded.AddRange(embeddedBatch);
        }

        return embedded;
    }
}

public sealed class InMemoryIngestionStore : IMemoryIngestionStore
{
    private readonly IMemoryIngestionRegistry _registry;

    public InMemoryIngestionStore(IMemoryIngestionRegistry registry)
    {
        _registry = registry;
    }

    public Task<string> StoreAsync(
        MemoryNormalizedEnvelope envelope,
        IReadOnlyList<MemoryChunk> chunks,
        MemoryIngestionDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var recordId = decision.ExistingRecordId ?? $"record-{envelope.NormalizedId}";
        if (!string.IsNullOrWhiteSpace(decision.DedupeKey))
            _registry.Upsert(decision.DedupeKey!, envelope, recordId);

        return Task.FromResult(recordId);
    }
}

public sealed class NoOpMemoryIngestionLinker : IMemoryIngestionLinker
{
    public Task LinkAsync(string recordId, MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class DefaultMemoryIngestionPipeline : IMemoryIngestionPipeline
{
    private readonly IMemoryIngestionNormalizer _normalizer;
    private readonly IMemoryIngestionPolicy _policy;
    private readonly IMemoryIngestionRegistry _registry;
    private readonly IMemoryIngestionExtractor _extractor;
    private readonly IMemoryIngestionChunker _chunker;
    private readonly IMemoryIngestionEmbedder _embedder;
    private readonly IMemoryIngestionStore _store;
    private readonly IMemoryIngestionLinker _linker;

    public DefaultMemoryIngestionPipeline(
        IMemoryIngestionNormalizer normalizer,
        IMemoryIngestionPolicy policy,
        IMemoryIngestionRegistry registry,
        IMemoryIngestionExtractor? extractor = null,
        IMemoryIngestionChunker? chunker = null,
        IMemoryIngestionEmbedder? embedder = null,
        IMemoryIngestionStore? store = null,
        IMemoryIngestionLinker? linker = null)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _extractor = extractor ?? new NoOpMemoryIngestionExtractor();
        _chunker = chunker ?? new DefaultMemoryIngestionChunker();
        _embedder = embedder ?? new NoOpMemoryIngestionEmbedder();
        _store = store ?? new InMemoryIngestionStore(registry);
        _linker = linker ?? new NoOpMemoryIngestionLinker();
    }

    public async Task<MemoryIngestionResult> IngestAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var audit = new List<MemoryIngestionAuditEvent>();

        MemoryIngestionAuditEvent Event(MemoryIngestionStage stage, bool success, string? message = null)
            => new(
                AuditId: Guid.NewGuid().ToString("N"),
                EnvelopeId: capture.EnvelopeId,
                Stage: stage,
                TimestampUtc: DateTimeOffset.UtcNow,
                Success: success,
                Message: message);

        var currentStage = MemoryIngestionStage.Capture;

        try
        {
            if (string.IsNullOrWhiteSpace(capture.RawPayload))
            {
                audit.Add(Event(MemoryIngestionStage.Capture, success: false, "malformed payload: raw payload is empty"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, "ingestion rejected: malformed payload"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: capture.DedupeKey,
                    AuditTrail: audit,
                    Error: "malformed payload");
            }

            audit.Add(Event(MemoryIngestionStage.Capture, success: true, "capture envelope accepted"));

            currentStage = MemoryIngestionStage.Normalize;
            var normalized = await _normalizer.NormalizeAsync(capture, cancellationToken);

            if (string.IsNullOrWhiteSpace(normalized.Content))
            {
                audit.Add(Event(MemoryIngestionStage.Normalize, success: false, "malformed payload: normalized content is empty"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, "ingestion rejected: malformed normalized content"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: capture.DedupeKey,
                    AuditTrail: audit,
                    Error: "malformed normalized content");
            }

            audit.Add(Event(MemoryIngestionStage.Normalize, success: true, "normalized envelope"));

            var decision = _policy.Evaluate(
                normalized,
                _registry.SnapshotByDedupeKey(),
                _registry.SnapshotDedupeToRecordId());

            if (decision.Kind == MemoryIngestionDecisionKind.ScrubAndAccept)
            {
                normalized = normalized with
                {
                    Content = decision.SanitizedContent ?? normalized.Content,
                    Summary = decision.SanitizedSummary ?? normalized.Summary,
                };
            }

            var isDuplicate = decision.Kind == MemoryIngestionDecisionKind.Duplicate;
            var isReplay = decision.Kind == MemoryIngestionDecisionKind.ReplayUpdate;

            if (decision.Kind == MemoryIngestionDecisionKind.Duplicate)
            {
                audit.Add(Event(MemoryIngestionStage.Audit, success: true, decision.Reason ?? "duplicate ignored"));
                return new MemoryIngestionResult(
                    Success: true,
                    RecordId: decision.ExistingRecordId,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: true,
                    IsReplay: false,
                    AuditTrail: audit);
            }

            if (decision.Kind == MemoryIngestionDecisionKind.Reject)
            {
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, decision.Reason ?? "rejected by policy"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: false,
                    IsReplay: false,
                    AuditTrail: audit,
                    Error: decision.Reason ?? "rejected");
            }

            currentStage = MemoryIngestionStage.Extract;
            var extracted = await _extractor.ExtractAsync(normalized, cancellationToken);
            audit.Add(Event(MemoryIngestionStage.Extract, success: true, "extracted normalized content"));

            var parentRecordId = decision.ExistingRecordId ?? $"record-{extracted.NormalizedId}";

            currentStage = MemoryIngestionStage.Chunk;
            var chunks = await _chunker.ChunkAsync(extracted, parentRecordId, cancellationToken);
            if (chunks.Count == 0)
            {
                audit.Add(Event(MemoryIngestionStage.Chunk, success: false, "chunker returned no chunks"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: true, "ingestion completed with no chunks"));
                return new MemoryIngestionResult(
                    Success: false,
                    Error: "chunker returned no chunks",
                    RecordId: decision.ExistingRecordId,
                    ChunkGroupId: extracted.NormalizedId,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: false,
                    IsReplay: isReplay,
                    AuditTrail: audit);
            }

            audit.Add(Event(MemoryIngestionStage.Chunk, success: true, $"chunked into {chunks.Count} chunk(s)"));

            currentStage = MemoryIngestionStage.Embed;
            var embedded = await _embedder.EmbedAsync(chunks, cancellationToken);
            if (embedded.Count != chunks.Count)
            {
                audit.Add(Event(MemoryIngestionStage.Embed, success: false, "embedder changed chunk count"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, "ingestion rejected: embedder output mismatch"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: false,
                    IsReplay: isReplay,
                    AuditTrail: audit,
                    Error: "embedder changed chunk count");
            }

            audit.Add(Event(MemoryIngestionStage.Embed, success: true, "embedding stage completed"));

            currentStage = MemoryIngestionStage.Store;
            var recordId = await _store.StoreAsync(extracted, embedded, decision, cancellationToken);
            audit.Add(Event(MemoryIngestionStage.Store, success: true, "store stage completed"));

            try
            {
                currentStage = MemoryIngestionStage.Link;
                await _linker.LinkAsync(recordId, extracted, embedded, cancellationToken);
                audit.Add(Event(MemoryIngestionStage.Link, success: true, "link stage completed"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception linkEx)
            {
                audit.Add(Event(MemoryIngestionStage.Link, success: false, linkEx.Message));
                audit.Add(Event(MemoryIngestionStage.Audit, success: true, "ingestion completed with partial recovery"));

                return new MemoryIngestionResult(
                    Success: true,
                    RecordId: recordId,
                    ChunkGroupId: extracted.NormalizedId,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: isDuplicate,
                    IsReplay: isReplay,
                    AuditTrail: audit,
                    Error: $"link stage failed: {linkEx.Message}");
            }

            audit.Add(Event(MemoryIngestionStage.Audit, success: true, decision.Reason ?? "ingestion completed"));

            return new MemoryIngestionResult(
                Success: true,
                RecordId: recordId,
                ChunkGroupId: extracted.NormalizedId,
                DedupeKey: decision.DedupeKey,
                IsDuplicate: isDuplicate,
                IsReplay: isReplay,
                AuditTrail: audit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            audit.Add(Event(currentStage, success: false, ex.Message));
            audit.Add(Event(MemoryIngestionStage.Audit, success: false, ex.Message));
            return new MemoryIngestionResult(
                Success: false,
                DedupeKey: capture.DedupeKey,
                AuditTrail: audit,
                Error: ex.Message);
        }
    }
}
