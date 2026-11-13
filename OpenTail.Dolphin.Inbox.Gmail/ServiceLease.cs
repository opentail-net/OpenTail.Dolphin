namespace OpenTail.Dolphin.Inbox.Gmail;

/// <summary>
/// A Google API service for one operation. A service the adapter built is disposed when the lease is; a service the caller supplied
/// (a test double, or one the caller owns) is left alone.
/// </summary>
internal sealed class ServiceLease<T>(T service, bool owned) : IDisposable where T : class, IDisposable
{
    public T Service { get; } = service;

    public void Dispose()
    {
        if (owned) Service.Dispose();
    }
}
