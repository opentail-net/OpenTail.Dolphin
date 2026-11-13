namespace OpenTail.Dolphin.Tests.Inbox;

public sealed class ImapSyncStateTests
{
    private const string Account = "u1@imap.example.com:993";

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"imap-sync-{Guid.NewGuid()}.json");

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var filePath = TempFile();
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", UidValidity: 12345, HighestSeenUid: 999, DateTimeOffset.UtcNow, Account));

            var loaded = await store.TryLoadAsync(Account, "INBOX");
            Assert.NotNull(loaded);
            Assert.Equal("INBOX", loaded.Folder);
            Assert.Equal(12345u, loaded.UidValidity);
            Assert.Equal(999u, loaded.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task TryLoad_ReturnsNull_WhenFileAbsent()
    {
        var store = new ImapSyncStateStore(TempFile());
        Assert.Null(await store.TryLoadAsync(Account, "INBOX"));
    }

    [Fact]
    public async Task TryLoad_ReturnsNull_ForUnknownFolder()
    {
        var filePath = TempFile();
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 10, DateTimeOffset.UtcNow, Account));

            Assert.Null(await store.TryLoadAsync(Account, "Sent"));
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task Save_MultipleFolders_IndependentlyRetrievable()
    {
        var filePath = TempFile();
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 100, DateTimeOffset.UtcNow, Account));
            await store.SaveAsync(new ImapSyncState("Sent", 2, 50, DateTimeOffset.UtcNow, Account));

            Assert.Equal(100u, (await store.TryLoadAsync(Account, "INBOX"))?.HighestSeenUid);
            Assert.Equal(50u, (await store.TryLoadAsync(Account, "Sent"))?.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task Save_OverwritesPreviousState()
    {
        var filePath = TempFile();
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 100, DateTimeOffset.UtcNow, Account));
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 200, DateTimeOffset.UtcNow, Account));

            Assert.Equal(200u, (await store.TryLoadAsync(Account, "INBOX"))?.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task Two_accounts_with_the_same_folder_name_keep_separate_state()
    {
        var filePath = TempFile();
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 100, DateTimeOffset.UtcNow, "a@imap.example.com:993"));
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 7, DateTimeOffset.UtcNow, "b@imap.example.com:993"));

            Assert.Equal(100u, (await store.TryLoadAsync("a@imap.example.com:993", "INBOX"))?.HighestSeenUid);
            Assert.Equal(7u, (await store.TryLoadAsync("b@imap.example.com:993", "INBOX"))?.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task An_entry_saved_under_the_old_folder_only_key_is_not_read()
    {
        var filePath = TempFile();
        try
        {
            // The layout before accounts were part of the key: one entry per folder name.
            await File.WriteAllTextAsync(filePath,
                """{ "INBOX": { "Folder": "INBOX", "UidValidity": 1, "HighestSeenUid": 100, "UpdatedAt": "2026-10-01T00:00:00Z" } }""");

            Assert.Null(await new ImapSyncStateStore(filePath).TryLoadAsync(Account, "INBOX"));
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task Parallel_saves_from_several_store_instances_all_survive()
    {
        var filePath = TempFile();
        try
        {
            var saves = Enumerable.Range(0, 40).Select(i =>
            {
                var store = new ImapSyncStateStore(filePath); // a separate instance per save, as separate runs would have
                return store.SaveAsync(new ImapSyncState($"Folder{i}", 1, (uint)i + 1, DateTimeOffset.UtcNow, Account));
            });
            await Task.WhenAll(saves);

            var store2 = new ImapSyncStateStore(filePath);
            for (var i = 0; i < 40; i++)
                Assert.Equal((uint)i + 1, (await store2.TryLoadAsync(Account, $"Folder{i}"))?.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }
}
