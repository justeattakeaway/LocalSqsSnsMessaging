// Translated from behavioral/events/check_api_destination_rate_limit_defers_a_burst_it_cannot_absorb_and_still_delivers_every_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-rate-limit-defers-a-burst-it-cannot-absorb-and-still-delivers-every-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationRateLimitDefersABurstItCannotAbsorbAndStillDeliversEveryEvent
{
    private const string Check = "api-destination-rate-limit-defers-a-burst-it-cannot-absorb-and-still-delivers-every-event";
    private const double DeliveryTimeoutS = 600.0;
    private const int EventCount = 60;
    private const double MinSpreadS = 10.0;

    /// <summary>
    /// 60 events burst at an API destination limited to 1 invocation/s are each delivered once, none
    /// dead-lettered, and arrive spread over at least 10 s (well below the strict 59 s, which tolerates
    /// the initial burst AWS admits).
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-rate-limit-defers-a-burst-it-cannot-absorb-and-still-delivers-every-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var keyValue = CaptureFunction.TokenUrlSafe();
        var (baseUrl, captureUrl) = await CaptureFunction.HttpsEndpoint(ctx, Check, "x-sim2real-key", keyValue);
        var (dlqUrl, dlqArn) = await CreateQueue(ctx, "sim2real-behav-dlq-", eventsMaySend: true);
        var (_, connArn) = await CreateConnection(ctx, events, ConnectionAuthorizationType.API_KEY, new CreateConnectionAuthRequestParameters
        {
            ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-sim2real-key", ApiKeyValue = keyValue },
        });
        var destArn = await CreateDestination(ctx, events, connArn, $"{baseUrl}/limited", rate: 1);
        var gotRate = (await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destArn.Split('/')[1] })).InvocationRateLimitPerSecond;
        Ensure(gotRate == 1, $"the destination's rate limit reads back as {gotRate}");
        var roleArn = Role(ctx);

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.apidest.");
        var (rule, _) = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [new Target { Id = "api", Arn = destArn, RoleArn = roleArn, DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn } }],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        // Liveness: one event must reach the endpoint before the burst is measured.
        await WaitLive(ctx, events, bus, source, captureUrl);

        var ids = new List<string>();
        for (var batch = 0; batch < EventCount / 10; batch++)
        {
            var b = batch;
            var resp = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [.. Enumerable.Range(0, 10).Select(i => new PutEventsRequestEntry
                {
                    EventBusName = bus, Source = source, DetailType = "burst", Detail = $$"""{"i": {{b * 10 + i}}}""",
                })],
            });
            Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected entries: {Entries(resp)}");
            ids.AddRange(resp.Entries.Select(e => e.EventId));
        }

        var want = ids.ToHashSet(StringComparer.Ordinal);
        var arrivals = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var dead = new List<JsonNode?>();
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < DeliveryTimeoutS && !want.IsSubsetOf(arrivals.Keys))
        {
            foreach (var req in await Receive(ctx, captureUrl))
            {
                if (req.BodyId is { } eid && want.Contains(eid))
                {
                    if (!arrivals.TryGetValue(eid, out var times))
                    {
                        arrivals[eid] = times = [];
                    }

                    times.Add(req.T);
                }
            }

            dead.AddRange((await ReceiveDead(ctx, dlqUrl)).Select(d => d.Body).Where(b => (string?)b?["id"] is { } id && want.Contains(id)));
            Ensure(dead.Count == 0, $"{dead.Count} rate-limited event(s) were dead-lettered instead of deferred");
        }

        var missing = want.Except(arrivals.Keys).ToList();
        Ensure(
            missing.Count == 0,
            $"{missing.Count} of {EventCount} events never reached the endpoint within {DeliveryTimeoutS}s");
        var firsts = arrivals.Values.Select(ts => ts.Min()).Order().ToList();
        var spread = firsts[^1] - firsts[0];
        Ensure(
            spread >= MinSpreadS,
            $"all {EventCount} events reached a 1-per-second destination within {spread:0.00}s of each " +
            "other; the rate limit was not applied");
    }

    private static async Task WaitLive(FidelityContext ctx, IAmazonEventBridge events, string bus, string source, string captureUrl)
    {
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var clock = FidelityTime.StartNew();
        var nextPut = 0.0;
        while (clock.Elapsed.TotalSeconds < 180.0)
        {
            if (clock.Elapsed.TotalSeconds >= nextPut)
            {
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "live", Detail = "{}" }],
                });
                sent.Add(resp.Entries[0].EventId);
                nextPut = clock.Elapsed.TotalSeconds + 20.0;
            }

            if ((await Receive(ctx, captureUrl)).Any(r => r.BodyId is { } id && sent.Contains(id)))
            {
                return;
            }
        }

        throw Fail($"none of {sent.Count} liveness event(s) reached the endpoint within 180s");
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

    /// <summary>A dead letter: its body (the event) and its string message attributes.</summary>
    private sealed record DeadLetter(JsonNode? Body, Dictionary<string, string?> Attrs);

    /// <summary>Receives (and deletes) the dead letters on a queue, with their attributes.</summary>
    private static async Task<List<DeadLetter>> ReceiveDead(FidelityContext ctx, string url, int waitTimeSeconds = 5)
    {
        var sqs = ctx.Sqs;
        var output = new List<DeadLetter>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = waitTimeSeconds, MessageAttributeNames = ["All"] });
        foreach (var msg in resp.Messages ?? [])
        {
            output.Add(new DeadLetter(
                JsonNode.Parse(msg.Body),
                (msg.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.StringValue, StringComparer.Ordinal)));
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        }

        return output;
    }
}
