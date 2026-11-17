// Translated from behavioral/events/check_describe_connection_redacts_secret_auth_material.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-connection-redacts-secret-auth-material</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeConnectionRedactsSecretAuthMaterial
{
    private const double AuthorizedTimeoutS = 120.0;
    private const double TerminalTimeoutS = 120.0;
    private const string UnroutableOAuthEndpoint = "https://192.0.2.1/oauth2/token";
    private const string Prefix = "sim2real-behav-";

    private static readonly JsonSerializerOptions DumpOptions = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };

    /// <summary>
    /// DescribeConnection and ListConnections return every API_KEY/BASIC/OAuth identifier and
    /// none of the secrets (API key value, password, client secret, secret header value).
    /// </summary>
    [Test]
    [DisplayName("events.describe-connection-redacts-secret-auth-material")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;

        // Per-run uuid4 values: a secret hit cannot be coincidence, and identifiers are a real control.
        var ids = new[] { "apikeyname", "username", "clientid", "hdrkey", "qskey" }.ToDictionary(k => k, _ => Guid.NewGuid().ToString("N"));
        var secrets = new[] { "apikeyvalue", "password", "clientsecret", "hdrsecret" }.ToDictionary(k => k, _ => Guid.NewGuid().ToString("N"));
        var plainQsValue = Guid.NewGuid().ToString("N");
        ConnectionHttpParameters InvocationHttp() => new()
        {
            HeaderParameters = [new ConnectionHeaderParameter { Key = $"x-sim2real-{ids["hdrkey"]}", Value = secrets["hdrsecret"], IsValueSecret = true }],
            QueryStringParameters = [new ConnectionQueryStringParameter { Key = $"q{ids["qskey"]}", Value = plainQsValue, IsValueSecret = false }],
        };

        // API_KEY is the only authorization type that needs no network egress to authorize.
        var apiKeyConn = await CreateConnection(ctx, ConnectionAuthorizationType.API_KEY, new CreateConnectionAuthRequestParameters
        {
            ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters
            {
                ApiKeyName = $"x-api-key-{ids["apikeyname"]}",
                ApiKeyValue = secrets["apikeyvalue"],
            },
            InvocationHttpParameters = InvocationHttp(),
        });
        var authorized = await PollUntil(
            () => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = apiKeyConn }),
            d => d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED",
            timeoutS: AuthorizedTimeoutS,
            intervalS: 2.0,
            description: $"API_KEY connection {apiKeyConn} leaves CREATING (H4)");
        Ensure(
            authorized.ConnectionState?.Value == "AUTHORIZED",
            "an API_KEY connection authorizes with no upstream call; expected " +
            $"ConnectionState=AUTHORIZED, got '{authorized.ConnectionState?.Value}' " +
            $"StateReason='{authorized.StateReason}'");
        var secretArn = authorized.SecretArn;
        var expectedFragment = $":secretsmanager:{ctx.Region}:{account}:secret:";
        Ensure(
            !string.IsNullOrEmpty(secretArn) && secretArn.Contains(expectedFragment, StringComparison.Ordinal),
            "the connection must store its credential in a Secrets Manager secret in this account and " +
            $"region: expected an ARN containing '{expectedFragment}', got SecretArn='{secretArn}' (H5)");
        AssertIdentifiersPresent(
            authorized,
            mustContain: [$"x-api-key-{ids["apikeyname"]}", $"x-sim2real-{ids["hdrkey"]}", $"q{ids["qskey"]}"],
            label: $"DescribeConnection({apiKeyConn})");
        AssertNoSecrets(authorized, secrets, label: $"DescribeConnection({apiKeyConn})");
        var apiKeyParams = authorized.AuthParameters ?? new ConnectionAuthResponseParameters();
        Ensure(
            apiKeyParams.ApiKeyAuthParameters?.ApiKeyName == $"x-api-key-{ids["apikeyname"]}",
            "ApiKeyAuthParameters.ApiKeyName must round-trip exactly; expected " +
            $"'x-api-key-{ids["apikeyname"]}', got '{apiKeyParams.ApiKeyAuthParameters?.ApiKeyName}'");
        Ensure(
            !Dump(apiKeyParams).Contains("ApiKeyValue", StringComparison.Ordinal),
            $"the response must not even carry an ApiKeyValue member, got {Dump(apiKeyParams)}");
        var headerParams = apiKeyParams.InvocationHttpParameters?.HeaderParameters ?? [];
        Ensure(
            headerParams.Count == 1,
            $"exactly the one header parameter that was set must come back, got {Dump(headerParams)}");
        Ensure(
            headerParams[0].IsValueSecret == true,
            $"IsValueSecret must round-trip as True for the secret header, got {Dump(headerParams[0])}");

        var basicConn = await CreateConnection(ctx, ConnectionAuthorizationType.BASIC, new CreateConnectionAuthRequestParameters
        {
            BasicAuthParameters = new CreateConnectionBasicAuthRequestParameters { Username = $"u-{ids["username"]}", Password = secrets["password"] },
        });
        var basic = await PollUntil(
            () => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = basicConn }),
            d => d.ConnectionState?.Value != "CREATING",
            timeoutS: TerminalTimeoutS,
            intervalS: 2.0,
            description: $"BASIC connection {basicConn} leaves CREATING");
        AssertIdentifiersPresent(basic, mustContain: [$"u-{ids["username"]}"], label: $"DescribeConnection({basicConn})");
        AssertNoSecrets(basic, secrets, label: $"DescribeConnection({basicConn})");
        Ensure(
            !Dump(basic.AuthParameters).Contains("Password", StringComparison.Ordinal),
            $"the response must not carry a Password member at all, got {Dump(basic.AuthParameters)}");

        var oauthConn = await CreateConnection(ctx, ConnectionAuthorizationType.OAUTH_CLIENT_CREDENTIALS, new CreateConnectionAuthRequestParameters
        {
            OAuthParameters = new CreateConnectionOAuthRequestParameters
            {
                ClientParameters = new CreateConnectionOAuthClientRequestParameters { ClientID = $"c-{ids["clientid"]}", ClientSecret = secrets["clientsecret"] },
                AuthorizationEndpoint = UnroutableOAuthEndpoint,
                HttpMethod = ConnectionOAuthHttpMethod.POST,
            },
        });
        var oauth = await PollUntil(
            () => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = oauthConn }),
            d => d.ConnectionState?.Value != "CREATING",
            timeoutS: TerminalTimeoutS,
            intervalS: 3.0,
            description: $"OAUTH connection {oauthConn} leaves CREATING (H6)");
        AssertIdentifiersPresent(oauth, mustContain: [$"c-{ids["clientid"]}", UnroutableOAuthEndpoint], label: $"DescribeConnection({oauthConn})");
        AssertNoSecrets(oauth, secrets, label: $"DescribeConnection({oauthConn})");
        Ensure(
            !Dump(oauth.AuthParameters).Contains("ClientSecret", StringComparison.Ordinal),
            $"the response must not carry a ClientSecret member at all, got {Dump(oauth.AuthParameters)}");

        // The list surface must not leak either.
        foreach (var name in new[] { apiKeyConn, basicConn, oauthConn })
        {
            var page = await events.ListConnectionsAsync(new ListConnectionsRequest { NamePrefix = name });
            var names = (page.Connections ?? []).Select(c => c.Name).ToList();
            Ensure(
                names.SequenceEqual([name]),
                $"ListConnections(NamePrefix='{name}') must return exactly that connection, got [{string.Join(", ", names)}]");
            AssertNoSecrets(page, secrets, label: $"ListConnections({name})");
        }
    }

    /// <summary><c>json.dumps(payload, default=str)</c>: the response as JSON.</summary>
    private static string Dump(object? payload) => JsonSerializer.Serialize(payload, DumpOptions);

    /// <summary>Create a connection, register its teardown, and return its name.</summary>
    private static async Task<string> CreateConnection(FidelityContext ctx, ConnectionAuthorizationType authorizationType, CreateConnectionAuthRequestParameters authParameters)
    {
        var events = ctx.Events;
        var name = FidelityContext.UniqueName($"{Prefix}conn-");
        await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = name,
            Description = "sim2real describe-connection-redacts-secret-auth-material",
            AuthorizationType = authorizationType,
            AuthParameters = authParameters,
        });
        ctx.OnCleanup(() => DeleteConnectionQuietly(ctx, name));
        return name;
    }

    /// <summary>Delete a connection, tolerating one already gone.</summary>
    private static async Task DeleteConnectionQuietly(FidelityContext ctx, string name)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete connection {name}: {error}", error.Exception);
        }
    }

    /// <summary>No secret nonce may appear ANYWHERE in the serialised response.</summary>
    private static void AssertNoSecrets(object payload, Dictionary<string, string> secrets, string label)
    {
        var blob = Dump(payload);
        var leaked = secrets.Where(kv => blob.Contains(kv.Value, StringComparison.Ordinal)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            leaked.Count == 0,
            $"{label} leaked secret material [{string.Join(", ", leaked)}] — a read-only EventBridge API returned the " +
            $"credential half of the connection's AuthParameters (H1). Response was {blob[..Math.Min(400, blob.Length)]}");
    }

    /// <summary>Positive control: the response really is about THIS connection and carries its identifiers.</summary>
    private static void AssertIdentifiersPresent(object payload, string[] mustContain, string label)
    {
        var blob = Dump(payload);
        var missing = mustContain.Where(needle => !blob.Contains(needle, StringComparison.Ordinal)).ToList();
        Ensure(
            missing.Count == 0,
            $"{label} did not return the non-secret identifiers [{string.Join(", ", missing)}] — without them the redaction " +
            "assertions would also pass against a target that returns an empty AuthParameters (H2). " +
            $"Response was {blob[..Math.Min(400, blob.Length)]}");
    }

    /// <summary>Poll until predicate holds; RAISE on timeout (never return the last value).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description)
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {last}");
    }
}
