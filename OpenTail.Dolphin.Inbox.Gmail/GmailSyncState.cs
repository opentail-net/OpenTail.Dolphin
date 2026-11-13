using System.Text.Json;
using OpenTail.Dolphin.Files;

namespace OpenTail.Dolphin.Inbox.Gmail;

/// <param name="CoveredUntil">All mail up to this time is known read (null in state saved by earlier versions).</param>
/// <param name="Backfill">A full listing an expired history id started, not finished yet.</param>
/// <param name="Folder">The Gmail label the state belongs to: INBOX, SENT, DRAFT, SPAM or TRASH. Mailbox history ids are shared by all labels, so each label keeps its own state.</param>
public sealed record GmailSyncState(
    string UserEmail,
    string? HistoryId,
    DateTimeOffset LastFetchAt,
    DateTimeOffset? CoveredUntil = null,
    GmailBackfill? Backfill = null,
    string Folder = GmailFolders.Inbox)
{
    /// <summary>The INBOX keeps the key it always had (the address alone), so its state is not read again. Other labels add the label.</summary>
    internal string Key => Folder == GmailFolders.Inbox ? UserEmail : UserEmail + "|" + Folder;
}

/// <summary>Mail between <paramref name="After"/> and <paramref name="Before"/> (Unix seconds; no upper bound when null) still to be
/// read. Listing is newest first, so each run reads the newest part and lowers <paramref name="Before"/>.</summary>
public sealed record GmailBackfill(long After, long? Before)
{
    internal string Query => Before is { } before ? $"after:{After} before:{before}" : $"after:{After}";
}

public sealed class GmailSyncStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;

    public GmailSyncStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>The state of the INBOX of <paramref name="userEmail"/>.</summary>
    public Task<GmailSyncState?> TryLoadAsync(string userEmail, CancellationToken ct = default) => TryLoadAsync(userEmail, GmailFolders.Inbox, ct);

    public async Task<GmailSyncState?> TryLoadAsync(string userEmail, string folder, CancellationToken ct = default)
    {
        var key = new GmailSyncState(userEmail, null, default, Folder: folder).Key;
        var entries = await LockedJsonState.ReadAsync<GmailSyncState>(_filePath, Json, ct);
        return entries.GetValueOrDefault(key);
    }

    public Task SaveAsync(GmailSyncState state, CancellationToken ct = default) =>
        LockedJsonState.UpdateAsync(_filePath, state.Key, state, Json, ct);
}
