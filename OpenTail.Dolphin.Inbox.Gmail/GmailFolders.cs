namespace OpenTail.Dolphin.Inbox.Gmail;

/// <summary>
/// The Gmail folders an inbox fetch can read: the system labels, by their ids. Other folders need a lookup of label names to ids
/// (custom labels, nested names), which is not built, so they are refused rather than read as something else.
/// </summary>
public static class GmailFolders
{
    public const string Inbox = "INBOX";

    private static readonly string[] Supported = [Inbox, "SENT", "DRAFT", "SPAM", "TRASH"];

    /// <summary>The label id for a folder name, matched case-insensitively ("Sent" is SENT). Throws <see cref="ArgumentException"/> for any other name.</summary>
    public static string LabelFor(string folder)
    {
        var label = folder.ToUpperInvariant();
        if (Array.IndexOf(Supported, label) >= 0)
            return label;
        throw new ArgumentException(
            $"Gmail folder '{folder}' is not supported. Use one of {string.Join(", ", Supported)}; other labels need a label lookup, which is not built.",
            nameof(folder));
    }
}
