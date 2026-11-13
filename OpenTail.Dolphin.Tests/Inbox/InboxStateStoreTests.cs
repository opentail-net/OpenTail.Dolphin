using System.Text.Json;
using OpenTail.Dolphin.Inbox.Gmail;
using OpenTail.Dolphin.Inbox.Graph;

namespace OpenTail.Dolphin.Tests.Inbox;

/// <summary>The inbox state files: saves that overlap keep every entry, and a file that cannot be read is neither moved nor overwritten.</summary>
public sealed class InboxStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inbox-state-" + Guid.NewGuid().ToString("N"));

    public InboxStateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Overlapping_graph_saves_for_many_folders_all_survive()
    {
        var file = Path.Combine(_dir, "graph.json");
        const int folders = 30;

        await Task.WhenAll(Enumerable.Range(0, folders).Select(i =>
            new GraphSyncStateStore(file).SaveAsync(new GraphSyncState("t", "me@example.com", $"https://g/f{i}", DateTimeOffset.UtcNow, $"folder{i}"))));

        var store = new GraphSyncStateStore(file);
        for (var i = 0; i < folders; i++)
            Assert.Equal($"https://g/f{i}", (await store.TryLoadAsync("t", "me@example.com", $"folder{i}"))!.DeltaLink);
    }

    [Fact]
    public async Task Overlapping_gmail_saves_for_inbox_and_sent_both_survive_and_the_inbox_keeps_its_key()
    {
        var file = Path.Combine(_dir, "gmail.json");

        await Task.WhenAll(
            new GmailSyncStateStore(file).SaveAsync(new GmailSyncState("me@example.com", "100", DateTimeOffset.UtcNow)),
            new GmailSyncStateStore(file).SaveAsync(new GmailSyncState("me@example.com", "200", DateTimeOffset.UtcNow, Folder: "SENT")));

        var store = new GmailSyncStateStore(file);
        Assert.Equal("100", (await store.TryLoadAsync("me@example.com"))!.HistoryId);
        Assert.Equal("200", (await store.TryLoadAsync("me@example.com", "SENT"))!.HistoryId);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.True(document.RootElement.TryGetProperty("me@example.com", out _));
    }

    [Fact]
    public async Task The_lock_file_is_gone_once_a_save_is_done()
    {
        var file = Path.Combine(_dir, "graph.json");
        await new GraphSyncStateStore(file).SaveAsync(new GraphSyncState("t", "me@example.com", "https://g/d", DateTimeOffset.UtcNow, "inbox"));

        Assert.Equal(["graph.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_damaged_state_file_is_kept_aside_and_read_as_empty_and_the_next_save_works()
    {
        var file = Path.Combine(_dir, "graph.json");
        const string damaged = "{ this is not json";
        await File.WriteAllTextAsync(file, damaged);
        var store = new GraphSyncStateStore(file);

        Assert.Null(await store.TryLoadAsync("t", "me@example.com", "inbox"));

        var kept = Assert.Single(Directory.GetFiles(_dir, "graph.json.corrupt-*"));
        Assert.Equal(damaged, await File.ReadAllTextAsync(kept));

        await store.SaveAsync(new GraphSyncState("t", "me@example.com", "https://g/d", DateTimeOffset.UtcNow, "inbox"));
        Assert.Equal("https://g/d", (await store.TryLoadAsync("t", "me@example.com", "inbox"))!.DeltaLink);
    }

    [Fact]
    public async Task A_state_file_that_cannot_be_read_is_neither_moved_nor_overwritten()
    {
        var file = Path.Combine(_dir, "graph.json");
        await new GraphSyncStateStore(file).SaveAsync(new GraphSyncState("t", "me@example.com", "https://g/keep", DateTimeOffset.UtcNow, "inbox"));
        var before = await File.ReadAllTextAsync(file);

        // Another process holds the file exclusively, so it cannot be read.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => new GraphSyncStateStore(file).TryLoadAsync("t", "me@example.com", "inbox"));
            await Assert.ThrowsAsync<IOException>(() => new GraphSyncStateStore(file).SaveAsync(
                new GraphSyncState("t", "me@example.com", "https://g/new", DateTimeOffset.UtcNow, "sentitems")));
        }

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }
}
