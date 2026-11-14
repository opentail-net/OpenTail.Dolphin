namespace OpenTail.Dolphin.Files.Contracts;

public sealed record TextExtractionResult(
    bool Success,
    string Text,
    string? ErrorMessage = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool NotText = false,
    IReadOnlyList<TextExtractionWarning>? Warnings = null)
{
    // NotText: the failure means the file has no text to read (a binary file, or an image with no OCR), not that reading went
    // wrong. The pipeline counts it as skipped rather than failed.
    // Warnings (B4): what the extraction library noticed while reading, passed through; null when there was none to pass.
}

/// <summary>A warning from the extraction (batch 11, B4). <paramref name="Code"/> is one of <see cref="TextExtractionWarningCodes"/> or the
/// library's own code, passed through unchanged.</summary>
public sealed record TextExtractionWarning(string Code, string Source, string? Message = null);

public static class TextExtractionWarningCodes
{
    /// <summary>The content does not match the file's type (for example a PDF named .docx).</summary>
    public const string ContentTypeMismatch = "content-type-mismatch";
}

public interface ITextExtractor
{
    TextExtractionResult Extract(string fileName, byte[] content);
}
