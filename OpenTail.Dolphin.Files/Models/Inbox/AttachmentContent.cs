namespace OpenTail.Dolphin.Files.Models;

public sealed record AttachmentContent(
    string Name,
    string ContentType,
    byte[] Bytes);
