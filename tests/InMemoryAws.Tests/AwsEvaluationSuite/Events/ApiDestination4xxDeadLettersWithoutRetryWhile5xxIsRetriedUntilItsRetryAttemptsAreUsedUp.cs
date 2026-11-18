// Translated from behavioral/events/check_api_destination_4xx_dead_letters_without_retry_while_5xx_is_retried_until_its_retry_attempts_are_used_up.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-4xx-dead-letters-without-retry-while-5xx-is-retried-until-its-retry-attempts-are-used-up</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestination4xxDeadLettersWithoutRetryWhile5xxIsRetriedUntilItsRetryAttemptsAreUsedUp
{
    private const string Check = "api-destination-4xx-dead-letters-without-retry-while-5xx-is-retried-until-its-retry-attempts-are-used-up";
    private const double DeliveryTimeoutS = 360.0;
    private const int Retries = 1;

    /// <summary>
    /// For one event sent to two API destinations differing only in status, the 400 endpoint's
    /// event is dead-lettered without retry (INVALID_PARAMETER), while the 500 endpoint is retried
    /// until MaximumRetryAttempts (1) is used up and its dead letter records the exhausted retries.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-4xx-dead-letters-without-retry-while-5xx-is-retried-until-its-retry-attempts-are-used-up")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var keyValue = CaptureFunction.TokenUrlSafe();
        var (baseUrl, captureUrl) = await CaptureFunction.HttpsEndpoint(ctx, Check, "x-sim2real-key", keyValue);
        var (dlqUrl, dlqArn) = await CreateQueue(ctx, "sim2real-behav-dlq-", eventsMaySend: true);
        var (_, connArn) = await CreateConnection(ctx, events, ConnectionAuthorizationType.API_KEY, new CreateConnectionAuthRequestParameters
        {
            ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-sim2real-key", ApiKeyValue = keyValue },
        });
        var dests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in new[] { "400", "500" })
        {
            dests[code] = await CreateDestination(ctx, events, connArn, $"{baseUrl}/s{code}");
        }

        var roleArn = Role(ctx);

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.apidest.");
        var (rule, ruleArn) = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [.. dests.Select(kv => new Target
            {
                Id = $"s{kv.Key}", Arn = kv.Value, RoleArn = roleArn,
                DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn },
                RetryPolicy = new RetryPolicy { MaximumRetryAttempts = Retries, MaximumEventAgeInSeconds = 900 },
            })],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        var sent = new HashSet<string>(StringComparer.Ordinal);
        var requests = new List<CapturedRequest>();
        var dead = new List<DeadLetter>();

        string? EventWithBothLetters()
        {
            // Only an event the 400 endpoint actually received is graded: one published before the
            // just-created role was usable is dead-lettered for a different reason.
            foreach (var eid in sent)
            {
                if (!Seen(requests, new HashSet<string>([eid], StringComparer.Ordinal), "/s400"))
                {
                    continue;
                }

                var codes = dead.Where(d => (string?)d.Body?["id"] == eid).Select(d => d.Attrs.GetValueOrDefault("TARGET_ARN")).ToHashSet();
                if (dests.Values.All(codes.Contains))
                {
                    return eid;
                }
            }

            return null;
        }

        var waitS = BudgetS(ctx, DeliveryTimeoutS);
        var clock = FidelityTime.StartNew();
        var nextPut = 0.0;
        while (clock.Elapsed.TotalSeconds < waitS && EventWithBothLetters() is null)
        {
            if (clock.Elapsed.TotalSeconds >= nextPut && !Seen(requests, sent, "/s400"))
            {
                // Re-publish until the endpoint has seen one event: a just-created rule, role or
                // destination can drop events until delivery is live.
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "status", Detail = "{}" }],
                });
                Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {Entries(resp)}");
                sent.Add(resp.Entries[0].EventId);
                nextPut = clock.Elapsed.TotalSeconds + 10.0;
            }

            requests.AddRange(await Receive(ctx, captureUrl, waitTimeSeconds: 1));
            dead.AddRange(await ReceiveDead(ctx, dlqUrl, waitTimeSeconds: 1));
        }

        var eventId = EventWithBothLetters();
        if (eventId is null && BudgetSpent(ctx))
        {
            throw new TimeoutException("an event dead-lettered by both targets: the run budget ended first; not graded");
        }

        Ensure(
            eventId is not null,
            $"no event was dead-lettered by both targets within {waitS:0}s; " +
            $"{sent.Count} sent, {dead.Count} dead letter(s), {requests.Count} request(s)");
        requests.AddRange(await Receive(ctx, captureUrl, waitTimeSeconds: 1));
        var letters = dead.Where(d => (string?)d.Body?["id"] == eventId).ToDictionary(d => d.Attrs["TARGET_ARN"]!, d => d.Attrs, StringComparer.Ordinal);
        var calls = new[] { "/s400", "/s500" }.ToDictionary(p => p, p => requests.Count(r => r.Path == p && r.BodyId == eventId));

        var four = letters[dests["400"]];
        Ensure(
            SameAttrs(four, new()
            {
                ["ERROR_CODE"] = "INVALID_PARAMETER",
                ["ERROR_MESSAGE"] = "ApiDestination returned HTTP status 400 with payload: ",
                ["RULE_ARN"] = ruleArn,
                ["TARGET_ARN"] = dests["400"],
            }),
            $"the 400 target's dead letter is {Shape(four, ruleArn, dests["400"])}");
        Ensure(calls["/s400"] >= 1, "the 400 endpoint never received the event it dead-lettered");

        var five = letters[dests["500"]];
        Ensure(
            SameAttrs(five, new()
            {
                ["ERROR_CODE"] = "UNKNOWN",
                ["ERROR_MESSAGE"] = "ApiDestination returned HTTP status 500 with payload: ",
                ["EXHAUSTED_RETRY_CONDITION"] = "MaximumRetryAttempts",
                ["RETRY_ATTEMPTS"] = Retries.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RULE_ARN"] = ruleArn,
                ["TARGET_ARN"] = dests["500"],
            }),
            $"the 500 target's dead letter is {Shape(five, ruleArn, dests["500"])}");
        Ensure(
            calls["/s500"] >= 2,
            $"a 500 must be retried; the endpoint received the event {calls["/s500"]} time(s)");
    }

    private static bool Seen(List<CapturedRequest> requests, IReadOnlySet<string> sent, string path) =>
        requests.Any(r => r.Path == path && r.BodyId is { } id && sent.Contains(id));

    private static bool SameAttrs(Dictionary<string, string?> got, Dictionary<string, string?> want) =>
        got.Count == want.Count && want.All(kv => got.TryGetValue(kv.Key, out var value) && value == kv.Value);

    /// <summary>The dead-letter attributes with the two ARNs replaced by whether they matched.</summary>
    private static string Shape(Dictionary<string, string?> attrs, string ruleArn, string targetArn)
    {
        var output = new Dictionary<string, string?>(attrs, StringComparer.Ordinal);
        foreach (var (key, want) in new[] { ("RULE_ARN", ruleArn), ("TARGET_ARN", targetArn) })
        {
            if (output.TryGetValue(key, out var value))
            {
                output[key] = value == want ? "True" : "False";
            }
        }

        return "{" + string.Join(", ", output.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

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
