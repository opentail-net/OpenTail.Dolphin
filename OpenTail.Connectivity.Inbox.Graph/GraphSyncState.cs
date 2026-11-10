using System.Text.Json;
using OpenTail.Connectivity.Files;

namespace OpenTail.Connectivity.Inbox.Graph;

/// <param name="TenantId">The Entra tenant the mailbox is read through. Part of the key: the same address in another tenant is another mailbox.</param>
/// <param name="DeltaLink">The link the next run starts from: a delta link, or a next link when the last run stopped early.</param>
/// <param name="Folder">The folder the state belongs to: "inbox" for the inbox, otherwise the folder path the adapter uses.</param>
public sealed record GraphSyncState(
    string TenantId,
    string UserEmail,
    string? DeltaLink,
    DateTimeOffset LastFetchAt,
    string Folder)
{
    internal string Key => $"{TenantId.ToLowerInvariant()}|{UserEmail.ToLowerInvariant()}|{Folder}";
}

/// <summary>Delta links per tenant, mailbox and folder, in one file. Each save changes one entry under a lock (see <see cref="LockedJsonState"/>).</summary>
public sealed class GraphSyncStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;

    public GraphSyncStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<GraphSyncState?> TryLoadAsync(string tenantId, string userEmail, string folder, CancellationToken ct = default)
    {
        var key = new GraphSyncState(tenantId, userEmail, null, default, folder).Key;
        var entries = await LockedJsonState.ReadAsync<GraphSyncState>(_filePath, Json, ct);
        return entries.GetValueOrDefault(key);
    }

    public Task SaveAsync(GraphSyncState state, CancellationToken ct = default) =>
        LockedJsonState.UpdateAsync(_filePath, state.Key, state, Json, ct);
}
