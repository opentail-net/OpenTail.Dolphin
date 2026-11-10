using System.Text.Json;
using OpenTail.Connectivity.Files;

namespace OpenTail.Connectivity.Inbox.Imap;

/// <param name="Account">The account the state belongs to (see <see cref="ImapMessageLookup.Account"/>). Part of the key: UIDs are only meaningful for one account's folder.</param>
public sealed record ImapSyncState(
    string Folder,
    uint UidValidity,
    uint HighestSeenUid,
    DateTimeOffset UpdatedAt,
    string Account)
{
    internal string Key => Account + "|" + Folder;
}

/// <summary>Highest seen UIDs per account and folder, in one file. Each save changes one entry under a lock (see <see cref="LockedJsonState"/>).</summary>
public sealed class ImapSyncStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;

    public ImapSyncStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<ImapSyncState?> TryLoadAsync(string account, string folder, CancellationToken ct = default)
    {
        var key = new ImapSyncState(folder, 0, 0, default, account).Key;
        var entries = await LockedJsonState.ReadAsync<ImapSyncState>(_filePath, Json, ct);
        return entries.GetValueOrDefault(key);
    }

    public Task SaveAsync(ImapSyncState state, CancellationToken ct = default) =>
        LockedJsonState.UpdateAsync(_filePath, state.Key, state, Json, ct);
}
