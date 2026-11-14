using OpenTail.Dolphin.Memory;

namespace OpenTail.Dolphin.Tests.Memory;

/// <summary>
/// Batch 12 (ingestion-ideas section 6): the chunk limit as an option, tables cut on row boundaries with the header repeated, and the
/// index text (off by default). The chunks are checked as text; the gate's numbers are in docs/design/memory.md.
/// </summary>
public sealed class ChunkerBatch12Tests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Header = ["| Site | Count | Cost centre |", "| --- | --- | --- |"];

    // ---- the chunk limit

    [Fact]
    public async Task The_chunk_limit_is_an_option_and_a_smaller_one_gives_shorter_chunks()
    {
        var prose = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"Sentence number {i} is here."));

        var byDefault = await Chunk(prose);
        var small = await Chunk(prose, limit: 120);

        Assert.All(byDefault, c => Assert.True(c.Content.Length <= 480));
        Assert.All(small, c => Assert.True(c.Content.Length <= 120));
        Assert.True(small.Count > byDefault.Count);
    }

    [Fact]
    public void A_limit_below_forty_characters_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DefaultMemoryIngestionChunker(new DefaultMemoryChunkerOptions(MaxChunkLength: 10)));
    }

    // ---- tables

    [Fact]
    public async Task A_markdown_table_is_cut_on_row_boundaries_with_the_header_repeated()
    {
        var rows = Enumerable.Range(1, 20).Select(i => $"| Site {i:00} | {i * 7} | CC-{200 + i} |").ToList();
        var table = string.Join("\n", [.. Header, .. rows]);

        var chunks = await Chunk(table, limit: 160);

        Assert.True(chunks.Count > 2);
        foreach (var chunk in chunks)
        {
            var lines = chunk.Content.Split('\n');
            Assert.Equal(Header[0], lines[0]);
            Assert.Equal(Header[1], lines[1]);
            Assert.All(lines.Skip(2), line => Assert.Contains(line, rows));
            Assert.True(chunk.Content.Length <= 160, $"piece is {chunk.Content.Length} characters");
        }

        // Every row appears once, in order: nothing is lost and nothing is cut.
        Assert.Equal(rows, chunks.SelectMany(c => c.Content.Split('\n').Skip(2)).ToList());
    }

    [Fact]
    public async Task The_first_piece_is_tagged_table_and_later_pieces_also_continuation()
    {
        var rows = Enumerable.Range(1, 20).Select(i => $"| Site {i:00} | {i * 7} | CC-{200 + i} |");
        var chunks = await Chunk(string.Join("\n", [.. Header, .. rows]), limit: 160);

        Assert.True(chunks.Count > 1);
        Assert.Contains("table:1", chunks[0].Tags!);
        Assert.DoesNotContain("continuation", chunks[0].Tags!);
        Assert.All(chunks.Skip(1), c => Assert.Contains("continuation", c.Tags!));
        Assert.All(chunks, c => Assert.Contains("table:1", c.Tags!));
    }

    [Fact]
    public async Task A_row_longer_than_the_limit_is_a_chunk_on_its_own_and_is_never_cut()
    {
        var longRow = "| Long site | " + string.Concat(Enumerable.Repeat("detail ", 30)) + "| CC-900 |";
        var table = string.Join("\n", [.. Header, "| Short | 1 | CC-1 |", longRow, "| Last | 2 | CC-2 |"]);

        var chunks = await Chunk(table, limit: 120);

        Assert.Contains(chunks, c => c.Content == longRow);
        Assert.DoesNotContain(chunks, c => c.Content.Contains("Long site", StringComparison.Ordinal) && c.Content != longRow);
    }

    [Fact]
    public async Task A_headerless_tab_block_repeats_nothing_each_piece_starts_with_its_own_row_and_row_one_is_in_one_piece()
    {
        var rows = Enumerable.Range(1, 30).Select(i => $"Asset {i:00}\tOwner {i:00}\t2027-0{1 + i % 9}-01\tSN-{4400 + i}").ToList();

        var chunks = await Chunk(string.Join("\n", rows), limit: 140);

        Assert.True(chunks.Count > 1);
        Assert.Equal(rows[0], chunks[0].Content.Split('\n')[0]);
        Assert.Equal(1, chunks.Count(c => c.Content.Split('\n').Contains(rows[0])));
        // No repeats and no cuts: the pieces, read in order, are the rows once each.
        Assert.Equal(rows, chunks.SelectMany(c => c.Content.Split('\n')).ToList());
        Assert.Contains("table:1", chunks[0].Tags!);
    }

    [Fact]
    public async Task Three_prose_lines_that_each_contain_one_pipe_are_not_a_table()
    {
        var chunks = await Chunk("Use a | b in the shell.\nSet c | d in the file.\nRun e | f before the release.");

        Assert.DoesNotContain(chunks, c => c.Tags!.Any(static t => t.StartsWith("table:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task One_line_that_looks_like_a_table_is_not_a_table()
    {
        var chunks = await Chunk("Use a | b | c in the shell, then restart the service.");

        Assert.DoesNotContain(chunks, c => c.Tags!.Any(static t => t.StartsWith("table:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Three_lines_of_three_pipe_columns_are_a_table_and_repeat_nothing_without_a_separator_row()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"| Site {i:00} | {i * 7} | CC-{200 + i} |").ToList();

        var chunks = await Chunk(string.Join("\n", lines), limit: 110);

        Assert.True(chunks.Count > 1);
        Assert.Contains("table:1", chunks[0].Tags!);
        Assert.Equal(lines, chunks.SelectMany(c => c.Content.Split('\n')).ToList());
    }

    [Fact]
    public async Task Two_tables_in_one_record_are_numbered_one_and_two()
    {
        var first = string.Join("\n", [.. Header, "| A | 1 | CC-1 |", "| B | 2 | CC-2 |"]);
        var second = string.Join("\n", [.. Header, "| C | 3 | CC-3 |", "| D | 4 | CC-4 |"]);

        var chunks = await Chunk(first + "\n\nA sentence between the two tables.\n\n" + second, limit: 480);

        Assert.Contains(chunks, c => c.Content.Contains("| A |", StringComparison.Ordinal) && c.Tags!.Contains("table:1"));
        Assert.Contains(chunks, c => c.Content.Contains("| C |", StringComparison.Ordinal) && c.Tags!.Contains("table:2"));
    }

    [Fact]
    public async Task The_default_chunker_does_not_cut_a_table_row_in_half()
    {
        // Longer than the default 480: the case the quality corpus's office table exposed (a row was split across two chunks).
        var rows = Enumerable.Range(1, 16).Select(i => $"| Office {i:00} | {i * 3} | CC-{300 + i} | 20{10 + i % 9} |");
        var table = "Office headcount by location, as of October.\n\n" + string.Join("\n", ["| Office | Headcount | Cost centre | Opened |", "| --- | --- | --- | --- |", .. rows]);

        var chunks = await Chunk(table);

        // Every line of every chunk is a whole line of the input: the header, the separator, a complete row, or the sentence.
        var allowed = rows.ToHashSet(StringComparer.Ordinal);
        allowed.Add("| Office | Headcount | Cost centre | Opened |");
        allowed.Add("| --- | --- | --- | --- |");
        allowed.Add("Office headcount by location, as of October.");
        foreach (var line in chunks.SelectMany(c => c.Content.Split('\n')))
            Assert.Contains(line, allowed);
    }

    [Fact]
    public async Task Prose_is_chunked_exactly_as_before_when_there_are_no_tables()
    {
        var prose = "First sentence here. Second sentence here.\n\nAnother paragraph with more words. And another one.\nA line that follows.";

        var withRecognition = await Chunk(prose, tables: true);
        var without = await Chunk(prose, tables: false);

        Assert.Equal(without.Select(static c => (c.Content, string.Join(",", c.Tags ?? []))), withRecognition.Select(static c => (c.Content, string.Join(",", c.Tags ?? []))));
    }

    // ---- the index text

    [Fact]
    public async Task The_index_text_is_off_by_default()
    {
        var chunks = await Chunk("A plain sentence about the rota.", title: "Rota", summary: "Weekly plan");

        Assert.All(chunks, static c => Assert.Null(c.IndexText));
    }

    [Fact]
    public async Task When_on_the_index_text_is_the_title_then_the_summary_then_the_content_and_the_content_is_unchanged()
    {
        var chunks = await Chunk("A plain sentence about the rota.", index: true, title: "Rota", summary: "Weekly plan");

        var chunk = Assert.Single(chunks);
        Assert.Equal("Rota\nWeekly plan\nA plain sentence about the rota.", chunk.IndexText);
        Assert.Equal("A plain sentence about the rota.", chunk.Content);
    }

    [Fact]
    public async Task The_thread_summary_chunk_gets_index_text_too()
    {
        var thread = "Sender: Ann\nHello there.\nSender: Ben\nReply here.\nSender: Ann\nThanks.\nSender: Ben\nBye.";

        var chunks = await Chunk(thread, index: true, title: "Ops thread", source: MemoryIngestionSourceType.Slack, thread: "root-1");

        var summary = Assert.Single(chunks, static c => c.Kind == MemoryChunkKind.Summary);
        Assert.StartsWith("Ops thread\n", summary.IndexText, StringComparison.Ordinal);
        Assert.EndsWith(summary.Content, summary.IndexText, StringComparison.Ordinal);
    }

    // ---- helpers

    private static async Task<IReadOnlyList<MemoryChunk>> Chunk(
        string content,
        int? limit = null,
        bool tables = true,
        bool index = false,
        string? title = null,
        string? summary = null,
        MemoryIngestionSourceType source = MemoryIngestionSourceType.UserNote,
        string? thread = null)
    {
        var chunker = new DefaultMemoryIngestionChunker(new DefaultMemoryChunkerOptions(limit ?? 480, tables, index));
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-1",
            SourceType: source,
            SourceSystem: "test",
            EventKind: MemoryIngestionEventKind.Unknown,
            ExternalId: "ext-1",
            CapturedAtUtc: Observed,
            ObservedAtUtc: Observed,
            Content: content,
            Title: title,
            Summary: summary,
            ThreadRootExternalId: thread);
        return await chunker.ChunkAsync(envelope, "rec-1");
    }
}
