using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>Values of kinds that are never stored (private keys, social security numbers, card numbers, provider credentials, tokens).
/// An always-write signal such as "decision" or "incident" must not let them through.</summary>
public class MemoryNeverStoredValuesTests
{
    private static MemoryNormalizedEnvelope Envelope(string content, string? summary = null) => new(
        NormalizedId: Guid.NewGuid().ToString("N"),
        SourceType: MemoryIngestionSourceType.GitHub,
        SourceSystem: "github",
        EventKind: MemoryIngestionEventKind.PullRequest,
        ExternalId: "pr-" + Guid.NewGuid().ToString("N")[..8],
        CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
        ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
        Content: content,
        Summary: summary);

    private static MemoryIngestionDecision Evaluate(string content, string? summary = null) =>
        new DefaultMemoryIngestionPolicy().Evaluate(
            Envelope(content, summary),
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope>(),
            dedupeToRecordId: new Dictionary<string, string>());

    [Fact]
    public void Mixed_content_with_decision_approved_and_incident_signals_is_refused_when_it_holds_a_social_security_number_or_private_key()
    {
        var decision = Evaluate(
            "Decision approved. Incident resolved.\n\nCustomer SSN: 123-45-6789\nPrivate key: [private key material]");

        Assert.Equal(MemoryIngestionDecisionKind.Reject, decision.Kind);
        Assert.Null(decision.SanitizedContent);
    }

    [Theory]
    [InlineData("Decision approved. Customer SSN: 123-45-6789")]
    [InlineData("Decision approved. Card 4111 1111 1111 1111 was charged.")]
    [InlineData("Decision approved.\n-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\n-----END RSA PRIVATE KEY-----")]
    [InlineData("Decision approved. Private key: MIIEpAIBAAKCAQEA")]
    [InlineData("Incident resolved. AWS key AKIAIOSFODNN7EXAMPLE leaked.")]
    [InlineData("Decision approved. GitHub token ghp_abcdefghijklmnopqrstuvwxyz0123456789 pasted.")]
    [InlineData("Decision approved. Session eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    public void A_never_stored_value_is_refused_whatever_the_signals_say(string content)
    {
        Assert.Equal(MemoryIngestionDecisionKind.Reject, Evaluate(content).Kind);
    }

    [Fact]
    public void A_never_stored_value_in_the_summary_is_refused_too()
    {
        Assert.Equal(MemoryIngestionDecisionKind.Reject, Evaluate("Decision approved.", summary: "Customer SSN 123-45-6789").Kind);
    }

    [Theory]
    [InlineData("Decision approved. Order 4111 1111 1111 1112 shipped.")] // fails the Luhn check, so it is not a card number
    [InlineData("Decision approved. Ticket SSN-000-12-3456 raised.")] // 000 is never an area number
    [InlineData("Decision approved. Call 555-123-4567 today.")] // not the 3-2-4 shape
    [InlineData("Decision approved. Build 20260514 passed.")]
    public void Lookalikes_that_are_not_never_stored_values_are_not_refused(string content)
    {
        Assert.NotEqual(MemoryIngestionDecisionKind.Reject, Evaluate(content).Kind);
    }

    [Fact]
    public void Scrubbing_still_applies_to_an_assignment_with_an_always_write_signal()
    {
        var decision = Evaluate("Decision approved; password=hunter2; incident resolved.");

        Assert.Equal(MemoryIngestionDecisionKind.ScrubAndAccept, decision.Kind);
        Assert.DoesNotContain("hunter2", decision.SanitizedContent!, StringComparison.OrdinalIgnoreCase);
    }
}
