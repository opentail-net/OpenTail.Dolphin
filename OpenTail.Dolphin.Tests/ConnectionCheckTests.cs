using System.Net;
using OpenTail.Dolphin.Drive.Google;
using OpenTail.Dolphin.Files.Contracts;

namespace OpenTail.Dolphin.Tests;

/// <summary>
/// A1 (ingestion-ideas section 6): the connection check of the Google Drive connector. One read-only request, with the token a run uses;
/// a refused sign-in is a configuration failure, a throttling answer is rate-limited, and a single file the sign-in cannot read is a failure
/// that is not a configuration failure.
/// </summary>
public sealed class ConnectionCheckTests
{
    private const string ReadOnlyScope = """{"scope":"https://www.googleapis.com/auth/drive.readonly"}""";

    /// <summary>Answers the token-info request as read-only, and the account request with the given status and body.</summary>
    private sealed class AboutAnswers(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            var url = request.RequestUri!.ToString();
            if (url.Contains("oauth2.googleapis.com/tokeninfo", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReadOnlyScope) });
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static GoogleDriveConnector Connector(HttpMessageHandler handler) =>
        new(_ => Task.FromResult("tok"), new GoogleDriveTarget.SharedDrive("sd1"), "acme", null, new HttpClient(handler), null);

    [Fact]
    public async Task A_valid_sign_in_is_ok_and_the_check_only_reads()
    {
        var handler = new AboutAnswers(HttpStatusCode.OK, """{"user":{"emailAddress":"ann@corp.example"}}""");

        var result = await Connector(handler).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Ok, result.Status);
        Assert.All(handler.Methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public async Task A_refused_token_is_a_configuration_failure()
    {
        var result = await Connector(new AboutAnswers(HttpStatusCode.Unauthorized)).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.ConfigurationFailure, result.Status);
    }

    [Fact]
    public async Task A_403_that_names_insufficient_permissions_is_a_configuration_failure()
    {
        var result = await Connector(new AboutAnswers(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}""")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.ConfigurationFailure, result.Status);
    }

    [Fact]
    public async Task A_403_about_one_file_is_a_failure_and_not_a_configuration_failure()
    {
        var result = await Connector(new AboutAnswers(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"forbidden"}]}}""")).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task A_throttling_answer_is_rate_limited_and_never_a_configuration_failure()
    {
        var result = await Connector(new AboutAnswers((HttpStatusCode)429)).CheckConnectionAsync();

        Assert.Equal(ConnectionCheckStatus.RateLimited, result.Status);
    }
}
