// Translated from behavioral/events/check_oauth_connection_with_unreachable_endpoint_deauthorizes_and_its_destination_is_inactive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.oauth-connection-with-unreachable-endpoint-deauthorizes-and-its-destination-is-inactive</summary>
[Category("AwsEvaluationSuite")]
public sealed class OauthConnectionWithUnreachableEndpointDeauthorizesAndItsDestinationIsInactive
{
    private const double SettleTimeoutS = 180.0;
    private const string UnreachableTokenUrl = "https://sim2real.invalid/oauth/token";
    private const string HookUrl = "https://sim2real.invalid/hook";

    /// <summary>
    /// An OAuth connection whose token endpoint is unreachable goes DEAUTHORIZED and its API
    /// destination INACTIVE, while a BASIC connection needs no token exchange and its destination is
    /// ACTIVE. The OAuth outcome comes from EventBridge's own token request, after CreateConnection.
    /// </summary>
    [Test]
    [DisplayName("events.oauth-connection-with-unreachable-endpoint-deauthorizes-and-its-destination-is-inactive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var oauthName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var oauth = await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = oauthName,
            AuthorizationType = ConnectionAuthorizationType.OAUTH_CLIENT_CREDENTIALS,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                OAuthParameters = new CreateConnectionOAuthRequestParameters
                {
                    ClientParameters = new CreateConnectionOAuthClientRequestParameters { ClientID = "sim2real-client", ClientSecret = "sim2real-secret" },
                    AuthorizationEndpoint = UnreachableTokenUrl,
                    HttpMethod = ConnectionOAuthHttpMethod.POST,
                },
            },
        });
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = oauthName }));
        Ensure(
            oauth.ConnectionState?.Value == "AUTHORIZING",
            $"an OAuth connection starts by AUTHORIZING (it must fetch a token), got '{oauth.ConnectionState?.Value}'");

        var basicName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var basic = await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = basicName,
            AuthorizationType = ConnectionAuthorizationType.BASIC,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                BasicAuthParameters = new CreateConnectionBasicAuthRequestParameters { Username = "sim2real", Password = "sim2real-pw" },
            },
        });
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = basicName }));
        var basicState = await Poll(() => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = basicName }),
            d => d.ConnectionState?.Value is not ("CREATING" or "AUTHORIZING"), "BASIC connection settles");
        Ensure(
            basicState.ConnectionState?.Value == "AUTHORIZED",
            $"positive control: a BASIC connection must be AUTHORIZED, got '{basicState.ConnectionState?.Value}' " +
            $"('{basicState.StateReason}')");

        var oauthState = await Poll(() => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = oauthName }),
            d => d.ConnectionState?.Value is not ("CREATING" or "AUTHORIZING"), "OAuth connection settles");
        Ensure(
            oauthState.ConnectionState?.Value == "DEAUTHORIZED",
            "an OAuth connection whose token endpoint is unreachable must end DEAUTHORIZED, got " +
            $"'{oauthState.ConnectionState?.Value}' ('{oauthState.StateReason}')");
        Ensure(
            oauthState.StateReason == "Could not connect to the specified endpoint.",
            $"StateReason was '{oauthState.StateReason}'");

        var goodDest = FidelityContext.UniqueName("sim2real-behav-dest-");
        var good = await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = goodDest, ConnectionArn = basic.ConnectionArn, InvocationEndpoint = HookUrl, HttpMethod = ApiDestinationHttpMethod.POST,
        });
        ctx.OnCleanup(() => events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = goodDest }));
        var badDest = FidelityContext.UniqueName("sim2real-behav-dest-");
        var bad = await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = badDest, ConnectionArn = oauth.ConnectionArn, InvocationEndpoint = HookUrl, HttpMethod = ApiDestinationHttpMethod.POST,
        });
        ctx.OnCleanup(() => events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = badDest }));

        var goodState = await Poll(async () => (await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = goodDest })).ApiDestinationState?.Value,
            s => s is not null, "destination on BASIC reports a state");
        Ensure(
            goodState == "ACTIVE",
            $"an API destination on an AUTHORIZED connection must be ACTIVE, got '{goodState}' " +
            $"(create answered '{good.ApiDestinationState?.Value}')");
        var badState = (await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = badDest })).ApiDestinationState?.Value;
        Ensure(
            badState == "INACTIVE",
            $"an API destination on a DEAUTHORIZED connection must be INACTIVE, got '{badState}' " +
            $"(create answered '{bad.ApiDestinationState?.Value}')");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, string what)
    {
        var clock = FidelityTime.StartNew();
        T? value = default;
        while (clock.Elapsed.TotalSeconds < SettleTimeoutS)
        {
            value = await read();
            if (done(value))
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"timed out after {SettleTimeoutS}s waiting until {what}; last read {value}");
    }
}
