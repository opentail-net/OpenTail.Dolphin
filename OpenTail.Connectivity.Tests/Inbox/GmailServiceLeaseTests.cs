using OpenTail.Connectivity.Inbox.Gmail;

namespace OpenTail.Connectivity.Tests.Inbox;

/// <summary>A Google API service built for one operation is disposed with it; one the caller supplied is not the adapter's to dispose.</summary>
public sealed class GmailServiceLeaseTests
{
    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_service_the_adapter_built_is_disposed_with_its_lease()
    {
        var probe = new Probe();
        using (var lease = new ServiceLease<Probe>(probe, owned: true))
            Assert.Same(probe, lease.Service);

        Assert.True(probe.Disposed);
    }

    [Fact]
    public void A_service_the_caller_supplied_is_not_disposed_by_the_adapter()
    {
        var probe = new Probe();
        using (var lease = new ServiceLease<Probe>(probe, owned: false))
            Assert.Same(probe, lease.Service);

        Assert.False(probe.Disposed);
    }
}
