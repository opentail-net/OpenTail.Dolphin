namespace OpenTail.Dolphin.Files.Models;

public sealed record CalendarInfo(
    string CalendarId,
    string Name,
    bool IsDefault,
    string TenantContext);
