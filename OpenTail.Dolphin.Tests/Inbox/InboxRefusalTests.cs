using System.Net;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using OpenTail.Dolphin.Files.Contracts;
using OpenTail.Dolphin.Inbox.Gmail;
using OpenTail.Dolphin.Inbox.Graph;

namespace OpenTail.Dolphin.Tests.Inbox;

/// <summary>
/// Batch 14 (A2 on the inbox adapters): a refused sign-in stops a read as a configuration failure, and an item-level or quota refusal does
/// not. Each adapter is tested against a fake provider that refuses every request with one status and one reason.
/// </summary>
public sealed class InboxRefusalTests
{
    /// <summary>Answers every request with the given status and Google's error shape.</summary>
    private sealed class Refusing(HttpStatusCode status, string reason) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = $"{{\"error\":{{\"code\":{(int)status},\"message\":\"refused\",\"errors\":[{{\"domain\":\"global\",\"reason\":\"{reason}\",\"message\":\"refused\"}}]}}}}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private static GmailInboxAdapter Gmail(HttpMessageHandler handler) =>
        new(GmailSettings.Create("id", "secret", "refresh", "me@example.com"), "t")
        {
            ServiceForTests = () => new GmailService(new BaseClientService.Initializer
            {
                HttpClientFactory = new Factory(handler), GZipEnabled = false, ApplicationName = "test",
            }),
        };

    private static async Task ReadAsync(GmailInboxAdapter adapter)
    {
        await foreach (var _ in adapter.FetchRecentAsync(new InboxFetchOptions("INBOX", 5), TestContext.Current.CancellationToken)) { }
    }

    /// <summary>Answers every request with the given status and Graph's error shape, with the given error code.</summary>
    private sealed class RefusingGraph(HttpStatusCode status, string code) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"refused\"}}}}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static GraphInboxAdapter Graph(HttpMessageHandler handler) =>
        new(GraphSettings.Create("t", "c", "s", "me@example.com"), "tenant", null, InboxSyncCommit.AfterEnumeration,
            new Microsoft.Graph.GraphServiceClient(new HttpClient(handler), new Microsoft.Kiota.Abstractions.Authentication.AnonymousAuthenticationProvider()));

    [Fact]
    public async Task Graph_401_stops_the_read_as_a_configuration_failure()
    {
        await Assert.ThrowsAsync<SourceConfigurationException>(() => ReadAsync(Graph(new RefusingGraph(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken"))));
    }

    /// <summary>The limit, recorded as a test: on this path the SDK gives a plain ApiException without the body, so a 403 that names the token is
    /// not identified here. It stays an ordinary failure until the SDK surfaces its code (report: not verified end to end).</summary>
    [Fact]
    public async Task Graph_403_that_names_the_token_stays_ordinary_on_the_delta_path_where_the_SDK_does_not_surface_its_code()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(Graph(new RefusingGraph(HttpStatusCode.Forbidden, "Authorization_RequestDenied"))));

        Assert.IsNotType<SourceConfigurationException>(ex);
    }

    [Fact]
    public void A_Graph_ODataError_403_that_names_the_token_is_the_sign_in_and_one_that_names_an_item_is_not()
    {
        var names = new Microsoft.Graph.Models.ODataErrors.ODataError
        {
            ResponseStatusCode = 403,
            Error = new Microsoft.Graph.Models.ODataErrors.MainError { Code = "Authorization_RequestDenied" },
        };
        var item = new Microsoft.Graph.Models.ODataErrors.ODataError
        {
            ResponseStatusCode = 403,
            Error = new Microsoft.Graph.Models.ODataErrors.MainError { Code = "accessDenied" },
        };

        Assert.IsType<SourceConfigurationException>(GraphInboxAdapter.Refusal(names));
        Assert.Null(GraphInboxAdapter.Refusal(item));
        Assert.Null(GraphInboxAdapter.Refusal(new Microsoft.Kiota.Abstractions.ApiException("forbidden") { ResponseStatusCode = 403 }));
    }

    [Fact]
    public async Task Graph_403_on_one_folder_is_not_a_configuration_failure()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(Graph(new RefusingGraph(HttpStatusCode.Forbidden, "accessDenied"))));

        Assert.IsNotType<SourceConfigurationException>(ex);
    }

    [Fact]
    public async Task Graph_throttling_is_not_a_configuration_failure()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(Graph(new RefusingGraph((HttpStatusCode)429, "TooManyRequests"))));

        Assert.IsNotType<SourceConfigurationException>(ex);
    }

    private static async Task ReadAsync(GraphInboxAdapter adapter)
    {
        await foreach (var _ in adapter.FetchRecentAsync(new InboxFetchOptions("INBOX", 5), TestContext.Current.CancellationToken)) { }
    }

    [Fact]
    public async Task Gmail_401_stops_the_read_as_a_configuration_failure()
    {
        var handler = new Refusing(HttpStatusCode.Unauthorized, "authError");

        await Assert.ThrowsAsync<SourceConfigurationException>(() => ReadAsync(Gmail(handler)));
    }

    [Fact]
    public async Task Gmail_403_naming_insufficient_permissions_is_a_configuration_failure()
    {
        var handler = new Refusing(HttpStatusCode.Forbidden, "insufficientPermissions");

        await Assert.ThrowsAsync<SourceConfigurationException>(() => ReadAsync(Gmail(handler)));
    }

    [Fact]
    public async Task Gmail_403_for_one_read_is_not_a_configuration_failure()
    {
        var handler = new Refusing(HttpStatusCode.Forbidden, "forbidden");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(Gmail(handler)));

        Assert.IsNotType<SourceConfigurationException>(ex);
    }

    [Fact]
    public async Task Gmail_quota_is_not_a_configuration_failure()
    {
        var handler = new Refusing(HttpStatusCode.Forbidden, "userRateLimitExceeded");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(Gmail(handler)));

        Assert.IsNotType<SourceConfigurationException>(ex);
    }
}
