using System.Net;
using Azure.Core;
using OpenTail.Dolphin.Drive.Graph;
using OpenTail.Dolphin.Files;
using OpenTail.Dolphin.Files.Contracts;

namespace OpenTail.Dolphin.Tests.Drive;

/// <summary>
/// 11b on the Graph connector. A 401 gets one fresh token (and, on a download, one fresh link) before it stops the run as a configuration
/// failure. A 403 is a configuration failure only when Graph names the token; a 403 on one item is an ordinary failed fetch. Throttling
/// past the run's ceiling is an ordinary failure. The connection check follows the same rules, and its messages are redacted.
/// </summary>
public sealed class GraphRefusalTests
{
    // ---- fetch: the rules of a run

    [Fact]
    public async Task A_download_refused_twice_with_401_is_a_configuration_failure()
    {
        var graph = new StubHttp(_ => Link());
        var download = new StubHttp(_ => Status(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<SourceConfigurationException>(() => Connector(graph, download).FetchAsync(Item("drv:f1")));

        Assert.Equal(2, download.Requests.Count); // one fresh link after the first refusal, then the stop
        Assert.Contains("twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_403_on_one_download_is_an_ordinary_failed_fetch_and_not_a_configuration_failure()
    {
        var download = new StubHttp(_ => Status(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<HttpRequestException>(() => Connector(new StubHttp(_ => Link()), download).FetchAsync(Item("drv:f1")));

        Assert.Equal(2, download.Requests.Count); // one fresh link, as for an expired URL, then the ordinary failure
    }

    [Fact]
    public async Task A_401_on_a_download_then_a_fresh_link_succeeds()
    {
        var downloads = 0;
        var download = new StubHttp(_ => downloads++ == 0 ? Status(HttpStatusCode.Unauthorized) : Body("fresh link body"));

        var bytes = await Connector(new StubHttp(_ => Link()), download).FetchAsync(Item("drv:f1"));

        Assert.Equal("fresh link body", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task A_401_on_a_Graph_call_gets_one_fresh_token_and_then_succeeds()
    {
        var credential = new TokenSequence("token-1", "token-2");
        var calls = 0;
        var graph = new StubHttp(_ => calls++ == 0 ? Status(HttpStatusCode.Unauthorized) : Link());
        var download = new StubHttp(_ => Body("fresh token body"));

        var bytes = await Connector(graph, download, credential: credential).FetchAsync(Item("drv:f1"));

        Assert.Equal("fresh token body", Encoding.UTF8.GetString(bytes));
        Assert.Equal(new[] { "Bearer token-1", "Bearer token-2" }, graph.Authorizations);
        Assert.Equal(2, credential.Issued.Count);
    }

    [Fact]
    public async Task A_401_that_survives_a_fresh_token_is_a_configuration_failure()
    {
        var credential = new TokenSequence("token-1", "token-2");
        var graph = new StubHttp(_ => Status(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken"}}"""));

        await Assert.ThrowsAsync<SourceConfigurationException>(() => Connector(graph, credential: credential).FetchAsync(Item("drv:f1")));

        Assert.Equal(2, credential.Issued.Count);
        Assert.Equal(2, graph.Requests.Count);
    }

    [Fact]
    public async Task A_403_that_names_the_token_is_a_configuration_failure()
    {
        var graph = new StubHttp(_ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"InvalidAuthenticationToken"}}"""));

        await Assert.ThrowsAsync<SourceConfigurationException>(() => Connector(graph).FetchAsync(Item("drv:f1")));
    }

    [Fact]
    public async Task A_403_accessDenied_on_one_item_is_an_ordinary_failure_and_not_a_configuration_failure()
    {
        var graph = new StubHttp(_ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied"}}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => Connector(graph).FetchAsync(Item("drv:f1")));
    }

    [Fact]
    public async Task Throttling_past_the_run_ceiling_is_an_ordinary_failure_and_not_a_configuration_failure()
    {
        var graph = new StubHttp(_ => new HttpResponseMessage((HttpStatusCode)429));
        var options = new GraphDriveOptions(MaxThrottleWait: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Connector(graph, options: options, delay: (_, _) => Task.CompletedTask).FetchAsync(Item("drv:f1")));

        Assert.Contains("throttling", ex.Message, StringComparison.Ordinal);
    }

    // ---- connection check

    [Fact]
    public async Task A_readable_drive_checks_ok_with_one_read()
    {
        var graph = new StubHttp(_ => Json("""{"id":"drv"}"""));

        var result = await Connector(graph).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Ok, result.Status);
        Assert.Single(graph.Requests);
        Assert.Contains("/drives/drv?$select=id", graph.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_in_the_check_gets_one_fresh_token_and_then_passes()
    {
        var calls = 0;
        var graph = new StubHttp(_ => calls++ == 0 ? Status(HttpStatusCode.Unauthorized) : Json("""{"id":"drv"}"""));

        var result = await Connector(graph, credential: new TokenSequence("token-1", "token-2")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Ok, result.Status);
        Assert.Equal(new[] { "Bearer token-1", "Bearer token-2" }, graph.Authorizations);
    }

    [Fact]
    public async Task A_401_that_survives_a_fresh_token_fails_the_check_as_a_configuration_failure()
    {
        var graph = new StubHttp(_ => Status(HttpStatusCode.Unauthorized));

        var result = await Connector(graph, credential: new TokenSequence("token-1", "token-2")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.ConfigurationFailure, result.Status);
        Assert.Equal(2, graph.Requests.Count);
    }

    [Fact]
    public async Task A_403_that_names_the_token_fails_the_check_as_a_configuration_failure()
    {
        var graph = new StubHttp(_ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}"""));

        var result = await Connector(graph).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.ConfigurationFailure, result.Status);
    }

    [Fact]
    public async Task A_403_accessDenied_fails_the_check_but_is_not_a_configuration_failure()
    {
        var graph = new StubHttp(_ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied"}}"""));

        var result = await Connector(graph).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task A_throttling_answer_in_the_check_is_rate_limited()
    {
        var graph = new StubHttp(_ => new HttpResponseMessage((HttpStatusCode)429));

        var result = await Connector(graph).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task The_check_message_never_carries_the_bearer_token()
    {
        var graph = new StubHttp(_ => throw new HttpRequestException("refused for Bearer bearer-secret-7f3a"));

        var result = await Connector(graph, credential: new TokenSequence("bearer-secret-7f3a")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Failed, result.Status);
        Assert.DoesNotContain("bearer-secret-7f3a", result.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Message, StringComparison.Ordinal);
    }

    // ---- helpers

    private static GraphDriveConnector Connector(StubHttp graph, StubHttp? download = null, GraphDriveOptions? options = null,
        TokenCredential? credential = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(credential ?? new TokenSequence(), new GraphDriveTarget.Drive("drv"), "acme", options,
            new HttpClient(graph), download is null ? null : new HttpClient(download), delay);

    private static SourceItem Item(string id) => new(id, "file.txt", "txt", 5, DateTimeOffset.UnixEpoch, "acme");

    /// <summary>A metadata answer with a download link, as Graph gives for an item.</summary>
    private static HttpResponseMessage Link() => Json("""{ "id": "f1", "@microsoft.graph.downloadUrl": "https://files.example/f1" }""");

    private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Body(string text)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Hands out the given tokens in order (the last one again after that), and records each one issued.</summary>
    private sealed class TokenSequence(params string[] tokens) : TokenCredential
    {
        private readonly string[] _tokens = tokens.Length == 0 ? ["token-1"] : tokens;

        public List<string> Issued { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var token = _tokens[Math.Min(Issued.Count, _tokens.Length - 1)];
            Issued.Add(token);
            return new(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}
