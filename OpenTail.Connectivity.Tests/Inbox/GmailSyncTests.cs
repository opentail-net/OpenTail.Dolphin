using System.Net;
using System.Text.RegularExpressions;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using OpenTail.Connectivity.Inbox.Gmail;

namespace OpenTail.Connectivity.Tests.Inbox;

/// <summary>Gmail sync against a fake mailbox: an expired history id never skips mail, a transient error never moves the state, and each
/// folder reads only its own label.</summary>
public sealed class GmailSyncTests : IDisposable
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), "gmail-sync-" + Guid.NewGuid().ToString("N") + ".json");
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (File.Exists(_stateFile)) File.Delete(_stateFile);
    }

    /// <summary>Messages with internal dates and labels (INBOX unless set); history answers 404 (expired), 503 (down) or the records of
    /// <see cref="Added"/> at the current id.</summary>
    private sealed class Mailbox : HttpMessageHandler
    {
        public List<(string Id, DateTimeOffset At)> Messages { get; } = [];
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        public List<string> Added { get; } = [];
        public HttpStatusCode HistoryStatus { get; set; } = HttpStatusCode.OK;
        public string CurrentHistoryId { get; set; } = "500";
        public List<string> Calls { get; } = [];
        public List<string> LabelRequests { get; } = [];

        private string LabelOf(string id) => Labels.TryGetValue(id, out var label) ? label : "INBOX";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            Calls.Add(path.Split('/').Last());
            if (path.EndsWith("/profile"))
                return Json($$"""{"emailAddress":"me@example.com","historyId":"{{CurrentHistoryId}}"}""");
            if (path.EndsWith("/history"))
            {
                if (HistoryStatus != HttpStatusCode.OK)
                    return Json("{\"error\":{\"code\":" + (int)HistoryStatus + ",\"message\":\"no\"}}", HistoryStatus);
                var records = string.Join(",", Added.Select((id, i) =>
                    "{\"id\":\"" + (i + 1) + "\",\"messagesAdded\":[{\"message\":{\"id\":\"" + id + "\"}}]}"));
                return Json("{\"historyId\":\"" + CurrentHistoryId + "\"" + (Added.Count == 0 ? "" : ",\"history\":[" + records + "]") + "}");
            }
            if (path.EndsWith("/messages"))
            {
                var q = query["q"] ?? "";
                long after = Regex.Match(q, @"after:(\d+)") is { Success: true } a ? long.Parse(a.Groups[1].Value) : long.MinValue;
                long before = Regex.Match(q, @"before:(\d+)") is { Success: true } b ? long.Parse(b.Groups[1].Value) : long.MaxValue;
                var wanted = query.GetValues("labelIds") ?? [];
                LabelRequests.AddRange(wanted);
                var matching = Messages.Where(m => m.At.ToUnixTimeSeconds() > after && m.At.ToUnixTimeSeconds() < before)
                    .Where(m => wanted.All(label => LabelOf(m.Id) == label))
                    .OrderByDescending(m => m.At).ToList();
                var offset = int.TryParse(query["pageToken"], out var o) ? o : 0;
                var size = int.Parse(query["maxResults"] ?? "100");
                var page = matching.Skip(offset).Take(size).Select(m => $$"""{"id":"{{m.Id}}"}""");
                var next = offset + size < matching.Count ? $$""","nextPageToken":"{{offset + size}}" """ : "";
                return Json("{\"messages\":[" + string.Join(",", page) + "]" + next + "}");
            }
            var id = path.Split('/').Last();
            var msg = Messages.Single(m => m.Id == id);
            var label = LabelOf(id);
            return Json($$$"""
                {"id":"{{{id}}}","labelIds":["{{{label}}}"],"internalDate":"{{{msg.At.ToUnixTimeMilliseconds()}}}",
                 "payload":{"mimeType":"text/plain","headers":[{"name":"Message-ID","value":"<{{{id}}}@example.com>"},{"name":"Subject","value":"{{{id}}}"}],
                 "body":{"data":"aGk"}
                 }
                }
                """);
        }

        private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private GmailInboxAdapter Adapter(Mailbox mailbox) =>
        new(GmailSettings.Create("id", "secret", "refresh", "me@example.com"), "t", new GmailSyncStateStore(_stateFile),
            commit: InboxSyncCommit.AfterEnumeration)
        {
            ServiceForTests = () => new GmailService(new BaseClientService.Initializer
            {
                HttpClientFactory = new Factory(mailbox), GZipEnabled = false, ApplicationName = "test",
            }),
        };

    private async Task<List<string>> RunAsync(Mailbox mailbox, int max, string folder = "INBOX")
    {
        var subjects = new List<string>();
        await foreach (var r in Adapter(mailbox).FetchRecentAsync(new InboxFetchOptions(folder, max), TestContext.Current.CancellationToken))
            subjects.Add(r.Subject);
        return subjects;
    }

    private Task SaveAsync(GmailSyncState state) => new GmailSyncStateStore(_stateFile).SaveAsync(state, TestContext.Current.CancellationToken);
    private Task<GmailSyncState?> LoadAsync() => new GmailSyncStateStore(_stateFile).TryLoadAsync("me@example.com", TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_expired_history_id_with_more_mail_than_MaxMessages_reads_all_of_it_over_later_runs()
    {
        var mailbox = new Mailbox { HistoryStatus = HttpStatusCode.NotFound };
        for (var i = 1; i <= 5; i++) mailbox.Messages.Add(("m" + i, T0.AddHours(i)));
        await SaveAsync(new GmailSyncState("me@example.com", "100", T0, CoveredUntil: T0));

        var seen = new List<string>(await RunAsync(mailbox, max: 2));
        Assert.Equal(["m5", "m4"], seen);
        var state = await LoadAsync();
        Assert.Equal("500", state!.HistoryId); // new mail now comes through the history again
        Assert.NotNull(state.Backfill);

        mailbox.HistoryStatus = HttpStatusCode.OK;
        for (var run = 0; run < 5 && (await LoadAsync())!.Backfill is not null; run++)
            seen.AddRange(await RunAsync(mailbox, max: 2));

        Assert.Equal(["m1", "m2", "m3", "m4", "m5"], seen.Distinct().Order());
        Assert.Null((await LoadAsync())!.Backfill);
    }

    [Fact]
    public async Task A_backfill_with_one_message_a_run_still_moves_on()
    {
        var mailbox = new Mailbox { HistoryStatus = HttpStatusCode.NotFound };
        for (var i = 1; i <= 3; i++) mailbox.Messages.Add(("m" + i, T0.AddHours(i)));
        await SaveAsync(new GmailSyncState("me@example.com", "100", T0, CoveredUntil: T0));

        var seen = new List<string>(await RunAsync(mailbox, max: 1));
        mailbox.HistoryStatus = HttpStatusCode.OK;
        for (var run = 0; run < 6 && (await LoadAsync())!.Backfill is not null; run++)
            seen.AddRange(await RunAsync(mailbox, max: 1));

        Assert.Equal(["m1", "m2", "m3"], seen.Distinct().Order());
    }

    [Fact]
    public async Task A_transient_history_error_fails_the_run_and_keeps_the_state()
    {
        var mailbox = new Mailbox { HistoryStatus = HttpStatusCode.ServiceUnavailable };
        mailbox.Messages.Add(("m1", T0.AddHours(1)));
        await SaveAsync(new GmailSyncState("me@example.com", "100", T0, CoveredUntil: T0));

        await Assert.ThrowsAsync<Google.GoogleApiException>(() => RunAsync(mailbox, max: 10));

        var state = await LoadAsync();
        Assert.Equal("100", state!.HistoryId);
        Assert.Null(state.Backfill);
        Assert.DoesNotContain("messages", mailbox.Calls); // no full listing on a transient error
    }

    [Fact]
    public async Task A_first_run_reads_the_profile_history_id_before_listing()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(("m1", T0.AddHours(1)));

        await RunAsync(mailbox, max: 10);

        Assert.True(mailbox.Calls.IndexOf("profile") < mailbox.Calls.IndexOf("messages"));
        Assert.Equal("500", (await LoadAsync())!.HistoryId);
    }

    [Fact]
    public async Task Sent_lists_only_sent_mail_and_the_inbox_is_not_read_for_it()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(("in1", T0.AddHours(1)));
        mailbox.Messages.Add(("out1", T0.AddHours(2)));
        mailbox.Labels["out1"] = "SENT";

        Assert.Equal(["out1"], await RunAsync(mailbox, max: 10, folder: "Sent"));
        Assert.Equal(["SENT"], mailbox.LabelRequests);
    }

    [Fact]
    public async Task History_for_sent_keeps_only_sent_mail()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(("in1", T0.AddHours(1)));
        mailbox.Messages.Add(("out1", T0.AddHours(2)));
        mailbox.Labels["out1"] = "SENT";
        mailbox.Added.AddRange(["in1", "out1"]);
        await new GmailSyncStateStore(_stateFile).SaveAsync(
            new GmailSyncState("me@example.com", "100", T0, CoveredUntil: T0, Folder: "SENT"), TestContext.Current.CancellationToken);

        Assert.Equal(["out1"], await RunAsync(mailbox, max: 10, folder: "Sent"));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_system_label_is_refused_before_any_request()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(("m1", T0.AddHours(1)));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(mailbox, max: 10, folder: "Projects/Alpha"));

        Assert.Contains("not supported", error.Message, StringComparison.Ordinal);
        Assert.Empty(mailbox.Calls);
    }

    [Fact]
    public async Task Each_folder_keeps_its_own_state_and_the_inbox_state_is_left_alone()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(("in1", T0.AddHours(1)));
        mailbox.Labels["out1"] = "SENT";
        mailbox.Messages.Add(("out1", T0.AddHours(2)));

        await RunAsync(mailbox, max: 10);
        await RunAsync(mailbox, max: 10, folder: "Sent");

        var store = new GmailSyncStateStore(_stateFile);
        Assert.Equal("500", (await store.TryLoadAsync("me@example.com", TestContext.Current.CancellationToken))!.HistoryId);
        Assert.Equal("500", (await store.TryLoadAsync("me@example.com", "SENT", TestContext.Current.CancellationToken))!.HistoryId);
    }
}
