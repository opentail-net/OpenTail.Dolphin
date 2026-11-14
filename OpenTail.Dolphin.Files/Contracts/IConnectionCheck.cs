namespace OpenTail.Dolphin.Files.Contracts;

/// <summary>What a connection check found (A1). It uses the kinds of A2: a refused sign-in is a configuration failure, a throttling answer is
/// rate-limited and never a configuration failure, and anything else is a failed check with the reason.</summary>
public enum ConnectionCheckStatus { Ok, ConfigurationFailure, RateLimited, Failed }

public sealed record ConnectionCheckResult(ConnectionCheckStatus Status, string Message);

/// <summary>
/// Checks that the connector's credential works: one read-only request, through the same client and token a run uses, answered at once (no
/// retry loop). It never writes to the source. Orca's use of the check (--lint and startup) is in Orca's repository.
/// </summary>
public interface IConnectionCheck
{
    Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default);
}
