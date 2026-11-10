// Moved from opentail's OpenTail.Core.Memory (MemoryIngestionPolicy.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public enum MemoryIngestionDecisionKind
{
    Accept = 0,
    Duplicate,
    ReplayUpdate,
    ScrubAndAccept,
    Reject,
}

public sealed record MemoryIngestionDecision(
    MemoryIngestionDecisionKind Kind,
    string? DedupeKey = null,
    string? Reason = null,
    string? ExistingRecordId = null,
    string? SanitizedContent = null,
    string? SanitizedSummary = null);

public sealed record MemoryIngestionFingerprint(
    string ExternalIdentity,
    string ContentIdentity,
    string DedupeKey);

public interface IMemoryIngestionPolicy
{
    MemoryIngestionFingerprint CreateFingerprint(MemoryNormalizedEnvelope envelope);

    MemoryIngestionDecision Evaluate(
        MemoryNormalizedEnvelope envelope,
        IReadOnlyDictionary<string, MemoryNormalizedEnvelope> byDedupeKey,
        IReadOnlyDictionary<string, string> dedupeToRecordId);
}

public sealed class DefaultMemoryIngestionPolicy : IMemoryIngestionPolicy
{
    private static readonly string[] AlwaysWriteSignals =
    [
        "decision",
        "approved",
        "approval",
        "resolved",
        "incident",
        "outage",
        "postmortem",
        "root cause",
        "task outcome",
        "completed",
        "repo fact",
        "repository fact",
        "cross-tool",
        "cross tool",
        "linked",
        "pull request",
        "merged",
    ];

    private static readonly string[] SelectiveWriteSignals =
    [
        "discussion",
        "chat",
        "thread",
        "diagnostic",
        "trace",
        "experiment",
        "temporary",
        "investigate",
        "hypothesis",
        "follow-up",
        "follow up",
    ];

    private static readonly string[] NeverWriteSignals =
    [
        "password",
        "token",
        "api key",
        "secret",
        "private key",
        "credential",
        "ssn",
    ];

    private static readonly Regex SensitiveAssignmentRegex = new(
        @"\b(password|token|api[_-]?key|secret|credential)\s*[:=]\s*[^\s;]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BearerTokenRegex = new(
        @"\bbearer\s+[a-z0-9\-\._~\+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Values of kinds that are never stored. A match refuses the whole memory: scrubbing cannot be relied on to remove every
    // occurrence of such a value, so no signal (not even an always-write one) overrides the refusal.
    private static readonly Regex PrivateKeyBlockRegex = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LabelledPrivateKeyRegex = new(
        @"\bprivate[ _-]?key\s*[:=]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SocialSecurityNumberRegex = new(
        @"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex CardNumberCandidateRegex = new(
        @"\b(?:\d[ -]?){12,18}\d\b",
        RegexOptions.Compiled);

    private static readonly Regex ProviderCredentialRegex = new(
        @"\b(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z\-_]{35})\b",
        RegexOptions.Compiled);

    private static readonly Regex JsonWebTokenRegex = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled);

    private static readonly string[] HighNoiseSignals =
    [
        "lorem ipsum",
        "heartbeat ok",
        "retrying",
        "debug loop",
        "noise",
        "transient",
    ];

    public MemoryIngestionFingerprint CreateFingerprint(MemoryNormalizedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var externalIdentity = BuildExternalIdentity(envelope);
        var contentIdentity = BuildContentIdentity(envelope);
        var dedupeKey = string.IsNullOrWhiteSpace(envelope.DedupeKey)
            ? externalIdentity
            : envelope.DedupeKey!;

        return new MemoryIngestionFingerprint(externalIdentity, contentIdentity, dedupeKey);
    }

    public MemoryIngestionDecision Evaluate(
        MemoryNormalizedEnvelope envelope,
        IReadOnlyDictionary<string, MemoryNormalizedEnvelope> byDedupeKey,
        IReadOnlyDictionary<string, string> dedupeToRecordId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(byDedupeKey);
        ArgumentNullException.ThrowIfNull(dedupeToRecordId);

        // Never-stored values are refused first, before any signal is read, so no always-write signal can override them.
        if (HoldsNeverStoredValue(envelope.Content ?? string.Empty) || HoldsNeverStoredValue(envelope.Summary ?? string.Empty))
        {
            return new MemoryIngestionDecision(
                MemoryIngestionDecisionKind.Reject,
                DedupeKey: envelope.DedupeKey,
                Reason: "Write policy rejected memory holding a value that is never stored (private key, social security number, card number or provider credential).");
        }

        var writeDecision = EvaluateWritePolicy(envelope);
        if (writeDecision is not null)
            return writeDecision;

        var scrubbed = ScrubSensitiveContent(envelope);
        if (scrubbed.WasScrubbed)
        {
            return new MemoryIngestionDecision(
                MemoryIngestionDecisionKind.ScrubAndAccept,
                DedupeKey: envelope.DedupeKey,
                Reason: "Sensitive material scrubbed before persistence.",
                SanitizedContent: scrubbed.Content,
                SanitizedSummary: scrubbed.Summary);
        }

        var fp = CreateFingerprint(envelope);

        if (byDedupeKey.TryGetValue(fp.DedupeKey, out var existing))
        {
            var existingRecordId = dedupeToRecordId.TryGetValue(fp.DedupeKey, out var id) ? id : null;
            var existingContentIdentity = BuildContentIdentity(existing);

            if (string.Equals(existingContentIdentity, fp.ContentIdentity, StringComparison.OrdinalIgnoreCase))
            {
                return new MemoryIngestionDecision(
                    MemoryIngestionDecisionKind.Duplicate,
                    DedupeKey: fp.DedupeKey,
                    Reason: "Matching dedupe key and content identity.",
                    ExistingRecordId: existingRecordId);
            }

            if (envelope.ObservedAtUtc >= existing.ObservedAtUtc)
            {
                return new MemoryIngestionDecision(
                    MemoryIngestionDecisionKind.ReplayUpdate,
                    DedupeKey: fp.DedupeKey,
                    Reason: "Matching dedupe key with newer or equal observed timestamp.",
                    ExistingRecordId: existingRecordId);
            }

            return new MemoryIngestionDecision(
                MemoryIngestionDecisionKind.Reject,
                DedupeKey: fp.DedupeKey,
                Reason: "Out-of-order replay older than existing observed timestamp.",
                ExistingRecordId: existingRecordId);
        }

        return new MemoryIngestionDecision(
            MemoryIngestionDecisionKind.Accept,
            DedupeKey: fp.DedupeKey,
            Reason: "No existing dedupe key found.");
    }

    private static MemoryIngestionDecision? EvaluateWritePolicy(MemoryNormalizedEnvelope envelope)
    {
        var content = envelope.Content ?? string.Empty;
        var lower = content.ToLowerInvariant();

        if (ContainsAny(lower, NeverWriteSignals) && !ContainsAny(lower, AlwaysWriteSignals))
        {
            return new MemoryIngestionDecision(
                MemoryIngestionDecisionKind.Reject,
                DedupeKey: envelope.DedupeKey,
                Reason: "Write policy rejected memory due to disallowed sensitive content.");
        }

        var isAlways = ContainsAny(lower, AlwaysWriteSignals);
        var isSelective = ContainsAny(lower, SelectiveWriteSignals)
            || (IsSourceSelective(envelope.SourceType) && ContainsAny(lower,
                "chat",
                "thread",
                "diagnostic",
                "trace",
                "experiment",
                "temporary",
                "retrying",
                "debug"));
        var isHighNoise = ContainsAny(lower, HighNoiseSignals);

        if (isHighNoise && !isAlways)
        {
            return new MemoryIngestionDecision(
                MemoryIngestionDecisionKind.Reject,
                DedupeKey: envelope.DedupeKey,
                Reason: "Write policy rejected transient noise.");
        }

        if (isAlways)
            return null;

        if (isSelective && !isAlways && !IsSelectiveWorthPersisting(envelope, lower))
        {
            return new MemoryIngestionDecision(
                MemoryIngestionDecisionKind.Reject,
                DedupeKey: envelope.DedupeKey,
                Reason: "Write policy rejected low-value selective memory.");
        }

        return null;
    }

    /// <summary>True when the text holds a value of a kind that is never stored: a private key, a social security number, a card number
    /// that passes the Luhn check, a provider credential or a JSON Web Token. Pattern-based: it finds the formats it knows, not every secret.</summary>
    internal static bool HoldsNeverStoredValue(string text)
    {
        if (text.Length == 0) return false;
        if (PrivateKeyBlockRegex.IsMatch(text)
            || LabelledPrivateKeyRegex.IsMatch(text)
            || SocialSecurityNumberRegex.IsMatch(text)
            || ProviderCredentialRegex.IsMatch(text)
            || JsonWebTokenRegex.IsMatch(text))
            return true;

        foreach (Match candidate in CardNumberCandidateRegex.Matches(text))
        {
            var digits = new string(candidate.Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
                return true;
        }
        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubled = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubled)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            doubled = !doubled;
        }
        return sum % 10 == 0;
    }

    private static bool IsSourceSelective(MemoryIngestionSourceType sourceType)
        => sourceType is MemoryIngestionSourceType.Slack
            or MemoryIngestionSourceType.ToolExecution
            or MemoryIngestionSourceType.Diagnostic;

    private static bool IsSelectiveWorthPersisting(MemoryNormalizedEnvelope envelope, string lowerContent)
    {
        if (envelope.SourceType == MemoryIngestionSourceType.GitHub
            && envelope.EventKind is MemoryIngestionEventKind.PullRequest or MemoryIngestionEventKind.Review)
            return true;

        if (envelope.SourceType == MemoryIngestionSourceType.UserNote)
            return true;

        if (envelope.SourceType == MemoryIngestionSourceType.WorkflowRun
            && ContainsAny(lowerContent, "failed", "passed", "deployment", "rollout"))
            return true;

        if (envelope.SourceType == MemoryIngestionSourceType.LocalDocs
            && ContainsAny(lowerContent, "architecture", "contract", "policy", "baseline"))
            return true;

        if (envelope.SourceType == MemoryIngestionSourceType.ToolExecution
            && ContainsAny(lowerContent, "result", "output", "completed", "error", "failed"))
            return true;

        if (envelope.SourceType == MemoryIngestionSourceType.Diagnostic
            && ContainsAny(lowerContent, "incident", "outage", "failure", "regression"))
            return true;

        return lowerContent.Length >= 80
            && (envelope.Labels?.Count > 0
                || envelope.LinkedExternalIds?.Count > 0
                || envelope.Mentions?.Count > 0);
    }

    private static bool ContainsAny(string lowerContent, IEnumerable<string> terms)
        => terms.Any(lowerContent.Contains);

    private static bool ContainsAny(string lowerContent, params string[] terms)
        => terms.Any(lowerContent.Contains);

    private static (bool WasScrubbed, string Content, string? Summary) ScrubSensitiveContent(MemoryNormalizedEnvelope envelope)
    {
        var content = envelope.Content ?? string.Empty;
        var summary = envelope.Summary;

        var scrubbedContent = SensitiveAssignmentRegex.Replace(content, "$1=[REDACTED]");
        scrubbedContent = BearerTokenRegex.Replace(scrubbedContent, "bearer [REDACTED]");

        string? scrubbedSummary = summary;
        if (!string.IsNullOrWhiteSpace(scrubbedSummary))
        {
            scrubbedSummary = SensitiveAssignmentRegex.Replace(scrubbedSummary!, "$1=[REDACTED]");
            scrubbedSummary = BearerTokenRegex.Replace(scrubbedSummary, "bearer [REDACTED]");
        }

        var scrubbed = !string.Equals(content, scrubbedContent, StringComparison.Ordinal)
            || !string.Equals(summary, scrubbedSummary, StringComparison.Ordinal);

        return (scrubbed, scrubbedContent, scrubbedSummary);
    }

    private static string BuildExternalIdentity(MemoryNormalizedEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.SourceSystem))
            throw new InvalidOperationException("Normalized envelope requires SourceSystem.");

        if (string.IsNullOrWhiteSpace(envelope.ExternalId))
            throw new InvalidOperationException("Normalized envelope requires ExternalId.");

        var source = envelope.SourceSystem.Trim();
        var external = envelope.ExternalId.Trim();
        return $"{source}:{external}";
    }

    private static string BuildContentIdentity(MemoryNormalizedEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(envelope.ContentHash))
            return envelope.ContentHash!;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Content ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}
