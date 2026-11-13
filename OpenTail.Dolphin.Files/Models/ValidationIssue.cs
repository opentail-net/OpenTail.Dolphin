namespace OpenTail.Dolphin.Files.Models;

public enum ValidationSeverity { Warning, Error }

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? SourceRef = null);
