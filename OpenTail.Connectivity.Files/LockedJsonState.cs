using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OpenTail.Connectivity.Files;

/// <summary>
/// A JSON file of keyed entries, read and changed under one lock, so two saves that overlap cannot erase each other. The lock is
/// in-process (a semaphore per file) and across processes (an exclusive lock file), as <see cref="FileSyncCursorStore"/> does.
/// <para>
/// A file that exists but cannot be read (an I/O error) is never overwritten: the call throws. A file whose JSON cannot be parsed is
/// moved aside to <c>&lt;file&gt;.corrupt-&lt;UTC timestamp&gt;</c> and read as empty, so its content is kept for inspection and sync
/// goes on with a full run for the entries it held.
/// </para>
/// </summary>
public static class LockedJsonState
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The entries of the file, read under the lock. A missing file has no entries.</summary>
    public static async Task<Dictionary<string, TValue>> ReadAsync<TValue>(string path, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var lockFile = await AcquireFileLockAsync(full, ct).ConfigureAwait(false);
            return await ReadUnderLockAsync<TValue>(full, options, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Replaces one entry, under the lock: reads the file, sets <paramref name="key"/>, and writes the file atomically.</summary>
    public static async Task UpdateAsync<TValue>(string path, string key, TValue value, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var lockFile = await AcquireFileLockAsync(full, ct).ConfigureAwait(false);
            var entries = await ReadUnderLockAsync<TValue>(full, options, ct).ConfigureAwait(false);
            entries[key] = value;
            await AtomicFile.WriteAllTextAsync(full, JsonSerializer.Serialize(entries, options), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Dictionary<string, TValue>> ReadUnderLockAsync<TValue>(string full, JsonSerializerOptions? options, CancellationToken ct)
    {
        if (!File.Exists(full))
            return new Dictionary<string, TValue>(StringComparer.Ordinal);

        // An I/O error propagates here, so the file is neither treated as empty nor overwritten.
        var text = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        Dictionary<string, TValue>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, TValue>>(text, options);
        }
        catch (JsonException)
        {
            entries = null;
        }
        if (entries is not null)
            return entries;

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        File.Move(full, $"{full}.corrupt-{stamp}-{Guid.NewGuid():N}");
        return new Dictionary<string, TValue>(StringComparer.Ordinal);
    }

    private static async Task<FileStream> AcquireFileLockAsync(string full, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // The lock file exists only while it is held: it is deleted when released, so the folder holds just the data files.
                return new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (elapsed.Elapsed < LockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Timed out waiting for the state lock for '{full}'.", ex);
            }
        }
    }
}
