// Translated from behavioral/events/check_oauth_connection_non_https_authorization_endpoint_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.oauth-connection-non-https-authorization-endpoint-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OauthConnectionNonHttpsAuthorizationEndpointRejected
{
    /// <summary>
    /// CreateConnection refuses an OAuth connection with a plain-http AuthorizationEndpoint and
    /// creates nothing under that name, while the same request with an https endpoint is accepted
    /// and is the only connection listed under the prefix.
    /// </summary>
    [Test]
    [DisplayName("events.oauth-connection-non-https-authorization-endpoint-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var prefix = FidelityContext.UniqueName("sim2real-behav-conn-");
        var insecureName = $"{prefix}-http";
        const string insecureUrl = "http://sim2real.invalid/oauth/token";
        ctx.OnCleanup(() => DeleteIfPresent(events, insecureName));
        var (code, message) = await Error(() => events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = insecureName,
            AuthorizationType = ConnectionAuthorizationType.OAUTH_CLIENT_CREDENTIALS,
            AuthParameters = OAuth(insecureUrl),
        }));
        var expected =
            "Failed to create the connection(s). Parameter AuthorizationEndpoint is not valid. Reason: " +
            $"Endpoint '{insecureUrl}' is invalid, please provide a valid HTTPS endpoint URL.";
        Ensure(
            (code, message) == ("ValidationException", expected),
            $"an http AuthorizationEndpoint must be refused, got '{code}' '{message}'");

        var gone = await Error(() => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = insecureName }));
        Ensure(
            gone == ("ResourceNotFoundException", $"Failed to describe the connection(s). Connection '{insecureName}' does not exist."),
            $"the refused connection must not exist; DescribeConnection gave {gone}");

        var secureName = $"{prefix}-https";
        var created = await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = secureName,
            AuthorizationType = ConnectionAuthorizationType.OAUTH_CLIENT_CREDENTIALS,
            AuthParameters = OAuth("https://sim2real.invalid/oauth/token"),
        });
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = secureName }));
        Ensure(
            created.ConnectionArn.Split(":connection/")[1].Split('/')[0] == secureName,
            "positive control: the https connection must be created under its own name");

        var listed = ((await events.ListConnectionsAsync(new ListConnectionsRequest { NamePrefix = prefix })).Connections ?? [])
            .Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        Ensure(
            listed.SequenceEqual([secureName]),
            $"only the https connection may exist under the run prefix, got [{string.Join(", ", listed)}]");
    }

    private static CreateConnectionAuthRequestParameters OAuth(string endpoint) => new()
    {
        OAuthParameters = new CreateConnectionOAuthRequestParameters
        {
            ClientParameters = new CreateConnectionOAuthClientRequestParameters { ClientID = "sim2real-client", ClientSecret = "sim2real-secret" },
            AuthorizationEndpoint = endpoint,
            HttpMethod = ConnectionOAuthHttpMethod.POST,
        },
    };

    /// <summary>Teardown that deletes the refused connection only if it was wrongly created.</summary>
    private static async Task DeleteIfPresent(IAmazonEventBridge events, string name)
    {
        var error = await FidelityContext.CaptureError(() => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name }));
        if (error is not null)
        {
            if (error.Code == "ResourceNotFoundException")
            {
                return;
            }

            throw error.Exception;
        }

        await events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name });
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }
}
