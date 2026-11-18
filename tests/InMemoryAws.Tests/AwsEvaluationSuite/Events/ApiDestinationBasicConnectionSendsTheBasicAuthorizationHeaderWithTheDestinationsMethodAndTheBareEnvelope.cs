// Translated from behavioral/events/check_api_destination_basic_connection_sends_the_basic_authorization_header_with_the_destinations_method_and_the_bare_envelope.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-basic-connection-sends-the-basic-authorization-header-with-the-destinations-method-and-the-bare-envelope</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationBasicConnectionSendsTheBasicAuthorizationHeaderWithTheDestinationsMethodAndTheBareEnvelope
{
    private const string Check = "api-destination-basic-connection-sends-the-basic-authorization-header-with-the-destinations-method-and-the-bare-envelope";
    private const double DeliveryTimeoutS = 180.0;

    /// <summary>
    /// A BASIC connection's API destination calls the endpoint with the destination's method (PUT),
    /// a Basic Authorization header from the credentials, no query string and the bare envelope as
    /// body; an input transformer on a second rule sends the rendered template instead.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-basic-connection-sends-the-basic-authorization-header-with-the-destinations-method-and-the-bare-envelope")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var (user, password) = ("sim2real-user", CaptureFunction.TokenUrlSafe());
        var wantAuth = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
        var (baseUrl, captureUrl) = await CaptureFunction.HttpsEndpoint(ctx, Check, "authorization", wantAuth);
        var (_, connArn) = await CreateConnection(ctx, events, ConnectionAuthorizationType.BASIC, new CreateConnectionAuthRequestParameters
        {
            BasicAuthParameters = new CreateConnectionBasicAuthRequestParameters { Username = user, Password = password },
        });
        var destArn = await CreateDestination(ctx, events, connArn, baseUrl + "/basic", method: "PUT");
        var roleArn = Role(ctx);

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.apidest.");
        var (rule, _) = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"], "detail-type": ["raw"]}""");
        var (shapedRule, _) = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"], "detail-type": ["shaped"]}""");
        foreach (var (name, transformer) in new[]
        {
            (rule, (InputTransformer?)null),
            (shapedRule, new InputTransformer
            {
                InputPathsMap = new() { ["m"] = "$.detail.m", ["id"] = "$.id" },
                InputTemplate = """{"order": <m>, "event": <id>}""",
            }),
        })
        {
            var put = await events.PutTargetsAsync(new PutTargetsRequest
            {
                Rule = name,
                EventBusName = bus,
                Targets = [new Target { Id = "api", Arn = destArn, RoleArn = roleArn, InputTransformer = transformer }],
            });
            Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");
        }

        var marker = FidelityContext.UniqueName("m-");
        var (raw, shaped, ids) = await DeliverPair(ctx, events, bus, source, captureUrl, marker);
        foreach (var (label, req) in new[] { ("raw", raw), ("shaped", shaped) })
        {
            Ensure(
                (req.Method, req.Path, req.Query) == ("PUT", "/basic", ""),
                $"the {label} request was '{req.Method}' to '{req.Path}' query '{req.Query}'");
            Ensure(
                req.Headers.GetValueOrDefault("authorization") == CaptureFunction.Sha256(wantAuth),
                $"the {label} request's Authorization header is " +
                (req.Headers.ContainsKey("authorization") ? "not Basic base64(user:password)" : "absent"));
        }

        var body = JsonNode.Parse(raw.Body)!.AsObject();
        var keys = body.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(keys.SequenceEqual(["account", "detail", "detail-type", "id", "region", "resources", "source", "time", "version"]), $"the raw body carries keys [{string.Join(", ", keys)}]");
        Ensure(
            (string?)body["id"] == ids.Raw && (string?)body["source"] == source && JsonNode.DeepEquals(body["detail"], new JsonObject { ["m"] = marker }),
            $"the raw body is not the delivered event: {body.ToJsonString()}");
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(shaped.Body), new JsonObject { ["order"] = marker, ["event"] = ids.Shaped }),
            $"the transformed body is '{shaped.Body[..Math.Min(300, shaped.Body.Length)]}'");
    }

    /// <summary>Publish a raw/shaped pair until both reach the endpoint.</summary>
    private static async Task<(CapturedRequest Raw, CapturedRequest Shaped, (string Raw, string Shaped) Ids)> DeliverPair(
        FidelityContext ctx, IAmazonEventBridge events, string bus, string source, string captureUrl, string marker)
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        var got = new List<CapturedRequest>();
        var clock = FidelityTime.StartNew();
        var nextPut = 0.0;

        ((CapturedRequest, string)? Raw, (CapturedRequest, string)? Shaped) Find()
        {
            (CapturedRequest, string)? raw = null, shaped = null;
            foreach (var req in got)
            {
                if (req.BodyId is { } id && sent.GetValueOrDefault(id) == "raw")
                {
                    raw = (req, id);
                }

                if (req.BodyString("event") is { } ev && sent.GetValueOrDefault(ev) == "shaped")
                {
                    shaped = (req, ev);
                }
            }

            return (raw, shaped);
        }

        while (clock.Elapsed.TotalSeconds < DeliveryTimeoutS)
        {
            if (clock.Elapsed.TotalSeconds >= nextPut)
            {
                // A just-created rule, role or destination can drop events until delivery is live.
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [.. new[] { "raw", "shaped" }.Select(kind => new PutEventsRequestEntry
                    {
                        EventBusName = bus, Source = source, DetailType = kind, Detail = $$"""{"m": "{{marker}}"}""",
                    })],
                });
                Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected an entry: {Entries(resp)}");
                sent[resp.Entries[0].EventId] = "raw";
                sent[resp.Entries[1].EventId] = "shaped";
                nextPut = clock.Elapsed.TotalSeconds + 20.0;
            }

            got.AddRange(await Receive(ctx, captureUrl));
            var (foundRaw, foundShaped) = Find();
            if (foundRaw is { } r && foundShaped is { } s)
            {
                return (r.Item1, s.Item1, (r.Item2, s.Item2));
            }
        }

        throw Fail(
            $"the raw and transformed events did not both reach the endpoint within " +
            $"{DeliveryTimeoutS}s; {got.Count} request(s) captured");
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
