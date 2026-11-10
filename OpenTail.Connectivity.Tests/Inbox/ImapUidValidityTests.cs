using OpenTail.Connectivity.Inbox.Imap;

namespace OpenTail.Connectivity.Tests.Inbox;

/// <summary>A changed UIDVALIDITY means the mailbox was renumbered: the saved UIDs name other messages, so a run must not continue above them.</summary>
public sealed class ImapUidValidityTests
{
    private const string Account = "u@imap.example.com:993";

    private static ImapSyncState Saved(uint uidValidity, uint highest) =>
        new("INBOX", uidValidity, highest, DateTimeOffset.UtcNow, Account);

    [Fact]
    public void A_renumbered_mailbox_does_not_start_from_the_old_highest_uid()
    {
        var saved = Saved(uidValidity: 100, highest: 5000);

        Assert.False(ImapInboxAdapter.IsIncremental(saved, uidValidity: 200));
        Assert.Equal(0u, ImapInboxAdapter.StartingHighestSeenUid(saved, uidValidity: 200));
    }

    [Fact]
    public void Messages_numbered_below_the_old_highest_uid_are_picked_up_after_a_renumbering()
    {
        // The mailbox was renumbered under UIDVALIDITY 200: its new messages are UIDs 1 to 100, all below the old highest UID 5000.
        var saved = Saved(uidValidity: 100, highest: 5000);
        var highest = ImapInboxAdapter.StartingHighestSeenUid(saved, uidValidity: 200);
        foreach (var uid in Enumerable.Range(1, 100))
            highest = Math.Max(highest, (uint)uid);
        var afterRun = Saved(uidValidity: 200, highest);

        // The next run searches from the highest UID seen plus one: 101, not 5001, so nothing in 1 to 100 is skipped.
        Assert.True(ImapInboxAdapter.IsIncremental(afterRun, uidValidity: 200));
        Assert.Equal(101u, afterRun.HighestSeenUid + 1);
    }

    [Fact]
    public void An_unchanged_uidvalidity_continues_from_the_saved_uid()
    {
        var saved = Saved(uidValidity: 100, highest: 5000);

        Assert.True(ImapInboxAdapter.IsIncremental(saved, uidValidity: 100));
        Assert.Equal(5000u, ImapInboxAdapter.StartingHighestSeenUid(saved, uidValidity: 100));
    }

    [Fact]
    public void A_saved_state_with_no_highest_uid_is_not_incremental()
    {
        var saved = Saved(uidValidity: 100, highest: 0);

        Assert.False(ImapInboxAdapter.IsIncremental(saved, uidValidity: 100));
        Assert.Equal(0u, ImapInboxAdapter.StartingHighestSeenUid(saved, uidValidity: 100));
    }
}
