namespace OpenTail.Dolphin.Files.Models;

public sealed record AttachmentMetadata(
    string Name,
    string? AttachmentId,
    string ContentType,
    long SizeBytes);
