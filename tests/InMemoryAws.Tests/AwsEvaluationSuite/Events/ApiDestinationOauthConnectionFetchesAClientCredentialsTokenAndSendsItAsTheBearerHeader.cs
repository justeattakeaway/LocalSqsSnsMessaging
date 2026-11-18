// Translated from behavioral/events/check_api_destination_oauth_connection_fetches_a_client_credentials_token_and_sends_it_as_the_bearer_header.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Web;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-oauth-connection-fetches-a-client-credentials-token-and-sends-it-as-the-bearer-header</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationOauthConnectionFetchesAClientCredentialsTokenAndSendsItAsTheBearerHeader
{
    private const string Check = "api-destination-oauth-connection-fetches-a-client-credentials-token-and-sends-it-as-the-bearer-header";
    private const double DeliveryTimeoutS = 180.0;

    /// <summary>
    /// An OAUTH_CLIENT_CREDENTIALS connection POSTs its OAuthHttpParameters and a form body of
    /// exactly client_id, client_secret and scope to the token endpoint, becomes AUTHORIZED, and
    /// the event request carries `Authorization: Bearer &lt;issued token&gt;` with the envelope as body.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-oauth-connection-fetches-a-client-credentials-token-and-sends-it-as-the-bearer-header")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var token = CaptureFunction.TokenUrlSafe();
        var (clientId, secret) = ("sim2real-client", CaptureFunction.TokenUrlSafe());
        var (baseUrl, captureUrl) = await CaptureFunction.OAuthHttpsEndpoint(ctx, Check, token, secret);
        var (_, connArn) = await CreateConnection(ctx, events, ConnectionAuthorizationType.OAUTH_CLIENT_CREDENTIALS, new CreateConnectionAuthRequestParameters
        {
            OAuthParameters = new CreateConnectionOAuthRequestParameters
            {
                ClientParameters = new CreateConnectionOAuthClientRequestParameters { ClientID = clientId, ClientSecret = secret },
                AuthorizationEndpoint = baseUrl + "/token",
                HttpMethod = ConnectionOAuthHttpMethod.POST,
                OAuthHttpParameters = new ConnectionHttpParameters
                {
                    BodyParameters = [new ConnectionBodyParameter { Key = "scope", Value = "orders/write", IsValueSecret = false }],
                    HeaderParameters = [new ConnectionHeaderParameter { Key = "x-oauth-h", Value = "oh", IsValueSecret = false }],
                    QueryStringParameters = [new ConnectionQueryStringParameter { Key = "aq", Value = "a1", IsValueSecret = false }],
                },
            },
        });
        var destArn = await CreateDestination(ctx, events, connArn, baseUrl + "/api");
        var roleArn = Role(ctx);

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.apidest.");
        var (rule, _) = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule, EventBusName = bus, Targets = [new Target { Id = "api", Arn = destArn, RoleArn = roleArn }],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        var reqs = new List<CapturedRequest>();
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var clock = FidelityTime.StartNew();
        var nextPut = 0.0;
        while (clock.Elapsed.TotalSeconds < DeliveryTimeoutS && !reqs.Any(r => r.Path == "/api" && r.BodyId is { } id && sent.Contains(id)))
        {
            if (clock.Elapsed.TotalSeconds >= nextPut)
            {
                // A just-created rule, role or destination can drop events until delivery is live.
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "oauth", Detail = """{"n": 1}""" }],
                });
                Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {Entries(resp)}");
                sent.Add(resp.Entries[0].EventId);
                nextPut = clock.Elapsed.TotalSeconds + 20.0;
            }

            reqs.AddRange(await Receive(ctx, captureUrl));
        }

        var tokenReqs = reqs.Where(r => r.Path == "/token").ToList();
        Ensure(tokenReqs.Count > 0, "the authorization endpoint never received a token request");
        var tr = tokenReqs[0];
        Ensure(
            (tr.Method, tr.Query) == ("POST", "aq=a1"),
            $"the token request was '{tr.Method}' with query '{tr.Query}'");
        Ensure(
            (tr.Headers.GetValueOrDefault("x-oauth-h"), tr.Headers.GetValueOrDefault("content-type")) == ("oh", "application/x-www-form-urlencoded"),
            $"the token request carried x-oauth-h='{tr.Headers.GetValueOrDefault("x-oauth-h")}', " +
            $"content-type='{tr.Headers.GetValueOrDefault("content-type")}'");
        var parsed = HttpUtility.ParseQueryString(tr.Body);
        var form = parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k] ?? "", StringComparer.Ordinal);
        var fields = form.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(
            fields.SequenceEqual(["client_id", "client_secret", "scope"]),
            $"the token request's form fields are [{string.Join(", ", fields)}]");
        Ensure(
            (form["client_id"], form["client_secret"] == CaptureFunction.Sha256(secret), form["scope"]) == (clientId, true, "orders/write"),
            $"the token request sent client_id='{form["client_id"]}', the secret " +
            $"{(form["client_secret"] == CaptureFunction.Sha256(secret) ? "unchanged" : "altered")}, scope='{form["scope"]}'");

        var api = reqs.Where(r => r.Path == "/api" && r.BodyId is { } id && sent.Contains(id)).ToList();
        Ensure(
            api.Count > 0,
            $"none of {sent.Count} event(s) reached the API within {DeliveryTimeoutS}s; " +
            $"{reqs.Count} request(s) captured");
        var req = api[0];
        Ensure(
            req.Headers.GetValueOrDefault("authorization") == CaptureFunction.Sha256($"Bearer {token}"),
            "the API request's Authorization header is " +
            (req.Headers.ContainsKey("authorization") ? "not Bearer <issued token>" : "absent"));
        Ensure(
            !(req.Body.Contains(secret, StringComparison.Ordinal) || req.Headers.Values.Any(v => v.Contains(secret, StringComparison.Ordinal)) || req.Query.Contains(secret, StringComparison.Ordinal)),
            "the client secret was sent to the API endpoint");
        var body = JsonNode.Parse(req.Body)!.AsObject();
        Ensure(
            (string?)body["source"] == source && (string?)body["detail-type"] == "oauth" && JsonNode.DeepEquals(body["detail"], new JsonObject { ["n"] = 1 }),
            $"the API request body is not the event: {body.ToJsonString()}");
    }

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, string prefix, bool eventsMaySend = false)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName(prefix),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        if (eventsMaySend)
        {
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = url,
                Attributes = new()
                {
                    ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{arn}}}}", "Condition": {"StringEquals": {"aws:SourceAccount": "{{{{ctx.Account}}}}"}}}]}""",
                },
            });
        }

        return (url, arn);
    }

    /// <summary>
    /// IAM is not emulated: the role's ARN comes from <see cref="FidelityContext.RoleArn"/>, and its
    /// trust and access policies (and their cleanups) are dropped.
    /// </summary>
    private static string Role(FidelityContext ctx) => ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-role-"));

    private static async Task<(string Name, string Arn)> CreateConnection(FidelityContext ctx, IAmazonEventBridge events, ConnectionAuthorizationType authType, CreateConnectionAuthRequestParameters auth)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-conn-");
        var arn = (await events.CreateConnectionAsync(new CreateConnectionRequest { Name = name, AuthorizationType = authType, AuthParameters = auth })).ConnectionArn;
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        var clock = FidelityTime.StartNew();
        string? state;
        while ((state = (await events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name })).ConnectionState?.Value) != "AUTHORIZED")
        {
            Ensure(clock.Elapsed.TotalSeconds < 90, $"the connection is '{state}', never AUTHORIZED");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        return (name, arn);
    }

    private static async Task<string> CreateDestination(FidelityContext ctx, IAmazonEventBridge events, string connArn, string endpoint, string method = "POST", int rate = 300)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-dest-");
        var arn = (await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = name,
            ConnectionArn = connArn,
            InvocationEndpoint = endpoint,
            HttpMethod = ApiDestinationHttpMethod.FindValue(method),
            InvocationRateLimitPerSecond = rate,
        })).ApiDestinationArn;
        ctx.OnCleanup(() => events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = name }));
        return arn;
    }

    private static async Task<string> CreateBus(FidelityContext ctx, IAmazonEventBridge events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return bus;
    }

    private static async Task<(string Name, string Arn)> CreateRule(FidelityContext ctx, IAmazonEventBridge events, string bus, string pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        var arn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern })).RuleArn;

        async Task Delete()
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets ?? [])
                .Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus });
        }

        ctx.OnCleanup(Delete);
        return (name, arn);
    }

    private static string FailedEntries(PutTargetsResponse put) =>
        "[" + string.Join(", ", (put.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}")) + "]";

    private static string Entries(PutEventsResponse resp) =>
        "[" + string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}")) + "]";

    /// <summary>Receives (and deletes) the requests the capture function recorded.</summary>
    private static async Task<List<CapturedRequest>> Receive(FidelityContext ctx, string url, int waitTimeSeconds = 5)
    {
        var sqs = ctx.Sqs;
        var output = new List<CapturedRequest>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = waitTimeSeconds, MessageAttributeNames = ["All"] });
        foreach (var msg in resp.Messages ?? [])
        {
            output.Add(CapturedRequest.Parse(msg.Body));
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        }

        return output;
    }
}
