// Translated from behavioral/events/check_api_destination_api_key_connection_adds_its_key_and_parameters_and_its_values_win_over_the_targets.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Web;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-api-key-connection-adds-its-key-and-parameters-and-its-values-win-over-the-targets</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationApiKeyConnectionAddsItsKeyAndParametersAndItsValuesWinOverTheTargets
{
    private const string Check = "api-destination-api-key-connection-adds-its-key-and-parameters-and-its-values-win-over-the-targets";
    private const double DeliveryTimeoutS = 180.0;

    /// <summary>
    /// An API_KEY connection's API destination sends the key header and merges the connection's
    /// and target's header, query and body parameters (connection wins on clashes), with JSON
    /// content-type and the Amazon/EventBridge/ApiDestinations user agent.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-api-key-connection-adds-its-key-and-parameters-and-its-values-win-over-the-targets")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var keyValue = CaptureFunction.TokenUrlSafe();
        var (baseUrl, captureUrl) = await CaptureFunction.HttpsEndpoint(ctx, Check, "x-sim2real-key", keyValue);
        var (_, connArn) = await CreateConnection(ctx, events, ConnectionAuthorizationType.API_KEY, new CreateConnectionAuthRequestParameters
        {
            ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-sim2real-key", ApiKeyValue = keyValue },
            InvocationHttpParameters = new ConnectionHttpParameters
            {
                HeaderParameters =
                [
                    new ConnectionHeaderParameter { Key = "x-conn-h", Value = "conn", IsValueSecret = false },
                    new ConnectionHeaderParameter { Key = "x-both", Value = "from-conn", IsValueSecret = false },
                ],
                QueryStringParameters = [new ConnectionQueryStringParameter { Key = "cq", Value = "from-conn", IsValueSecret = false }],
                BodyParameters = [new ConnectionBodyParameter { Key = "conn_body", Value = "b1", IsValueSecret = false }],
            },
        });
        var destArn = await CreateDestination(ctx, events, connArn, baseUrl + "/orders/*");
        var roleArn = Role(ctx);

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.apidest.");
        var (rule, _) = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target
                {
                    Id = "api", Arn = destArn, RoleArn = roleArn,
                    HttpParameters = new HttpParameters
                    {
                        PathParameterValues = ["o-42"],
                        HeaderParameters = new() { ["x-target-h"] = "tgt", ["x-both"] = "from-target" },
                        QueryStringParameters = new() { ["tq"] = "from-target", ["cq"] = "from-target" },
                    },
                },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        var (req, eventId) = await Deliver(ctx, events, bus, source, captureUrl);
        Ensure(
            (req.Method, req.Path) == ("POST", "/orders/o-42"),
            $"the request was '{req.Method}' to '{req.Path}'; expected POST to /orders/o-42");
        var query = ParseQsl(req.Query).Order().ToList();
        Ensure(
            query.SequenceEqual([("cq", "from-conn"), ("tq", "from-target")]),
            $"the query string was '{req.Query}'");
        var h = req.Headers;
        Ensure(
            h.GetValueOrDefault("x-sim2real-key") == CaptureFunction.Sha256(keyValue),
            $"the API key header is {(h.ContainsKey("x-sim2real-key") ? "a different value" : "absent")}");
        Ensure(
            (h.GetValueOrDefault("x-conn-h"), h.GetValueOrDefault("x-target-h"), h.GetValueOrDefault("x-both")) == ("conn", "tgt", "from-conn"),
            $"connection/target headers arrived as x-conn-h='{h.GetValueOrDefault("x-conn-h")}' " +
            $"x-target-h='{h.GetValueOrDefault("x-target-h")}' x-both='{h.GetValueOrDefault("x-both")}'");
        Ensure(
            (h.GetValueOrDefault("content-type"), h.GetValueOrDefault("user-agent")) == ("application/json; charset=utf-8", "Amazon/EventBridge/ApiDestinations"),
            $"content-type '{h.GetValueOrDefault("content-type")}', user-agent '{h.GetValueOrDefault("user-agent")}'");

        var body = JsonNode.Parse(req.Body)!.AsObject();
        var connBody = (string?)body["conn_body"];
        body.Remove("conn_body");
        var keys = body.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            connBody == "b1",
            $"the connection's body parameter is missing from the JSON body; keys [{string.Join(", ", keys)}]");
        Ensure(keys.SequenceEqual(["account", "detail", "detail-type", "id", "region", "resources", "source", "time", "version"]), $"the body carries keys [{string.Join(", ", keys)}]");
        Ensure(
            (string?)body["id"] == eventId && (string?)body["source"] == source && JsonNode.DeepEquals(body["detail"], new JsonObject { ["n"] = 1 }),
            $"the body is not the delivered event: {body.ToJsonString()}");
    }

    /// <summary>Publish fresh events until one reaches the endpoint; return (request, event id).</summary>
    private static async Task<(CapturedRequest Request, string EventId)> Deliver(FidelityContext ctx, IAmazonEventBridge events, string bus, string source, string captureUrl)
    {
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var got = new List<CapturedRequest>();
        var clock = FidelityTime.StartNew();
        var nextPut = 0.0;
        while (clock.Elapsed.TotalSeconds < DeliveryTimeoutS)
        {
            if (clock.Elapsed.TotalSeconds >= nextPut)
            {
                // A just-created rule, role or destination can drop events until delivery is live.
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "api", Detail = """{"n": 1}""" }],
                });
                Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {Entries(resp)}");
                sent.Add(resp.Entries[0].EventId);
                nextPut = clock.Elapsed.TotalSeconds + 20.0;
            }

            got.AddRange(await Receive(ctx, captureUrl));
            foreach (var req in got)
            {
                if (req.BodyId is { } eid && sent.Contains(eid))
                {
                    return (req, eid);
                }
            }
        }

        throw Fail(
            $"none of {sent.Count} event(s) reached the endpoint within {DeliveryTimeoutS}s; " +
            $"{got.Count} other request(s) captured");
    }

    /// <summary><c>urllib.parse.parse_qsl</c>.</summary>
    private static List<(string, string)> ParseQsl(string query)
    {
        var parsed = HttpUtility.ParseQueryString(query);
        return [.. parsed.AllKeys.SelectMany(key => (parsed.GetValues(key) ?? []).Select(value => (key ?? "", value)))];
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
