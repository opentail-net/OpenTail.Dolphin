using OpenTail.Dolphin.Inbox.Imap;

namespace OpenTail.Dolphin.Tests.Inbox;

/// <summary>Which IMAP account a message id or a state entry belongs to. The standard ports keep the ids they always had.</summary>
public sealed class ImapAccountIdentityTests
{
    private static ImapSettings Account(int port, string user = "u", string host = "imap.example.com") => new(host, port, true, user, "p");

    [Fact]
    public void Ids_on_the_standard_ports_are_what_they_were_before_the_port_was_part_of_them()
    {
        Assert.Equal("imap-uid:u@imap.example.com/INBOX/12345/7", ImapMessageLookup.StableId(Account(993), "INBOX", 12345, 7));
        Assert.Equal("imap-uid:u@imap.example.com/INBOX/12345/7", ImapMessageLookup.StableId(Account(143), "INBOX", 12345, 7));
    }

    [Fact]
    public void A_non_standard_port_is_part_of_the_id_so_two_servers_on_one_host_do_not_share_one()
    {
        var id = ImapMessageLookup.StableId(Account(1993), "INBOX", 12345, 7);

        Assert.Equal("imap-uid:u@imap.example.com:1993/INBOX/12345/7", id);
        Assert.NotEqual(ImapMessageLookup.StableId(Account(993), "INBOX", 12345, 7), id);
    }

    [Theory]
    [InlineData(993)]
    [InlineData(1993)]
    public void TryParseStableId_reads_back_what_StableId_wrote(int port)
    {
        var id = ImapMessageLookup.StableId(Account(port), "Projects/Alpha", 12345, 7);

        Assert.True(ImapMessageLookup.TryParseStableId(id, Account(port), out var folder, out var uidValidity, out var uid));
        Assert.Equal("Projects/Alpha", folder);
        Assert.Equal(12345u, uidValidity);
        Assert.Equal(7u, uid);
    }

    [Fact]
    public void An_id_from_another_port_on_the_same_host_is_not_read_as_this_account()
    {
        var id = ImapMessageLookup.StableId(Account(1993), "INBOX", 12345, 7);

        Assert.False(ImapMessageLookup.TryParseStableId(id, Account(993), out _, out _, out _));
    }

    [Fact]
    public void The_state_account_always_includes_the_port()
    {
        Assert.Equal("u@imap.example.com:993", ImapMessageLookup.Account(Account(993)));
        Assert.NotEqual(ImapMessageLookup.Account(Account(993)), ImapMessageLookup.Account(Account(1993)));
    }
}
