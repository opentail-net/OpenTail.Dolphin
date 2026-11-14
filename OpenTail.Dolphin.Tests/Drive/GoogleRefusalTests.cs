using System.Net;
using OpenTail.Dolphin.Drive.Google;
using OpenTail.Dolphin.Files;
using OpenTail.Dolphin.Files.Contracts;

namespace OpenTail.Dolphin.Tests.Drive;

/// <summary>
/// 11b on the Google Drive connector. A 401 gets one more attempt with the token the callback gives now before it stops the run. A 403
/// stops the run only when it names insufficient permissions; a 403 on one file is an ordinary failure, and a quota 403 is waited out.
/// The connection check follows the same rules, and its messages are redacted even when the failure is in the token-info step.
/// </summary>
public sealed class GoogleRefusalTests
{
    private const string Meta = """{"mimeType":"text/plain","driveId":"sd1","ownedByMe":true,"parents":[],"name":"f1.txt"}""";
    private const string ReadOnlyScope = """{"scope":"https://www.googleapis.com/auth/drive.readonly"}""";

    // ---- fetch: the rules of a run

    [Fact]
    public async Task A_401_on_the_content_twice_is_a_configuration_failure()
    {
        // tok-0 is the metadata read's; the two content attempts each get a fresh token from the callback.
        var routes = new Routes(_ => Status(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<SourceConfigurationException>(() => Connector(routes, new Tokens("tok-0", "tok-1", "tok-2")).FetchAsync(Item()));

        Assert.Equal(new[] { "Bearer tok-1", "Bearer tok-2" }, routes.Authorizations);
    }

    [Fact]
    public async Task A_401_on_the_content_then_a_fresh_token_succeeds()
    {
        var calls = 0;
        var routes = new Routes(_ => calls++ == 0 ? Status(HttpStatusCode.Unauthorized) : Body("fresh token body"));

        var bytes = await Connector(routes, new Tokens("tok-0", "tok-1", "tok-2")).FetchAsync(Item());

        Assert.Equal("fresh token body", Encoding.UTF8.GetString(bytes));
        Assert.Equal(new[] { "Bearer tok-1", "Bearer tok-2" }, routes.Authorizations);
    }

    [Fact]
    public async Task A_403_that_names_insufficient_permissions_is_a_configuration_failure()
    {
        var routes = new Routes(_ => Status(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}"""));

        await Assert.ThrowsAsync<SourceConfigurationException>(() => Connector(routes, new Tokens()).FetchAsync(Item()));
    }

    [Fact]
    public async Task A_403_on_one_file_is_an_ordinary_failure_and_not_a_configuration_failure()
    {
        var routes = new Routes(_ => Status(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"forbidden"}]}}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => Connector(routes, new Tokens()).FetchAsync(Item()));
    }

    [Fact]
    public async Task A_quota_403_is_waited_out_and_is_not_a_configuration_failure()
    {
        var calls = 0;
        var routes = new Routes(_ => calls++ == 0
            ? Status(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"userRateLimitExceeded"}]}}""")
            : Body("after the quota"));

        var bytes = await Connector(routes, new Tokens(), delay: (_, _) => Task.CompletedTask).FetchAsync(Item());

        Assert.Equal("after the quota", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Throttling_past_the_run_ceiling_is_an_ordinary_failure_and_not_a_configuration_failure()
    {
        var routes = new Routes(_ => new HttpResponseMessage((HttpStatusCode)429));
        var options = new GoogleDriveOptions(MaxThrottleWait: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Connector(routes, new Tokens(), options, delay: (_, _) => Task.CompletedTask).FetchAsync(Item()));

        Assert.Contains("rate-limiting", ex.Message, StringComparison.Ordinal);
    }

    // ---- connection check

    [Fact]
    public async Task A_401_in_the_check_asks_for_a_fresh_token_and_then_passes()
    {
        var calls = 0;
        var tokens = new Tokens("tok-1", "tok-2");
        var routes = new Routes(_ => calls++ == 0
            ? Status(HttpStatusCode.Unauthorized)
            : Status(HttpStatusCode.OK, """{"user":{"emailAddress":"ann@corp.example"}}"""));

        var result = await Connector(routes, tokens).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Ok, result.Status);
        Assert.Equal(2, tokens.Asked);
    }

    [Fact]
    public async Task A_401_that_survives_a_fresh_token_fails_the_check_as_a_configuration_failure()
    {
        var result = await Connector(new Routes(_ => Status(HttpStatusCode.Unauthorized)), new Tokens("tok-1", "tok-2")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.ConfigurationFailure, result.Status);
    }

    [Fact]
    public async Task The_check_message_never_carries_the_token_when_the_read_fails()
    {
        var routes = new Routes(_ => throw new HttpRequestException("refused for Bearer bearer-secret-7f3a"));

        var result = await Connector(routes, new Tokens("bearer-secret-7f3a")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Failed, result.Status);
        Assert.DoesNotContain("bearer-secret-7f3a", result.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_check_message_never_carries_the_token_when_the_token_info_step_fails()
    {
        // The token-info request puts the token in its URL: a failure there must be redacted too.
        var routes = new Routes(_ => Status(HttpStatusCode.OK), tokenInfo: _ => throw new HttpRequestException("token-info refused access_token=bearer-secret-7f3a"));

        var result = await Connector(routes, new Tokens("bearer-secret-7f3a")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Failed, result.Status);
        Assert.DoesNotContain("bearer-secret-7f3a", result.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Message, StringComparison.Ordinal);
    }

    // ---- helpers

    private static GoogleDriveConnector Connector(HttpMessageHandler handler, Tokens tokens, GoogleDriveOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(tokens.Next, new GoogleDriveTarget.SharedDrive("sd1"), "acme", options, new HttpClient(handler), delay);

    private static SourceItem Item() => new("f1", "f1.txt", "txt", 5, DateTimeOffset.UnixEpoch, "acme");

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Body(string text)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    /// <summary>Answers the token-info request (read-only by default) and the metadata read, and passes every other request to the given answer.</summary>
    private sealed class Routes(
        Func<HttpRequestMessage, HttpResponseMessage> answer,
        Func<HttpRequestMessage, HttpResponseMessage>? tokenInfo = null) : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("oauth2.googleapis.com/tokeninfo", StringComparison.Ordinal))
                return Task.FromResult((tokenInfo ?? (_ => Status(HttpStatusCode.OK, ReadOnlyScope)))(request));
            if (url.Contains("fields=mimeType", StringComparison.Ordinal))
                return Task.FromResult(Status(HttpStatusCode.OK, Meta));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>Hands out the given tokens in order (the last one again after that), and counts the asks.</summary>
    private sealed class Tokens(params string[] values)
    {
        private readonly string[] _values = values.Length == 0 ? ["tok-1"] : values;

        public int Asked { get; private set; }

        public Task<string> Next(CancellationToken cancellationToken)
        {
            var token = _values[Math.Min(Asked, _values.Length - 1)];
            Asked++;
            return Task.FromResult(token);
        }
    }
}
