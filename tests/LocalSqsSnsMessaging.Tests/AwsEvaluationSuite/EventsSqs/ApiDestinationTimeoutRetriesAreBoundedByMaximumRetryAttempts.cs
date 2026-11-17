// Translated from behavioral/events__sqs/check_api_destination_timeout_retries_are_bounded_by_maximum_retry_attempts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.api-destination-timeout-retries-are-bounded-by-maximum-retry-attempts</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationTimeoutRetriesAreBoundedByMaximumRetryAttempts
{
    private const int Retries = 3;
    private const double EndpointTimeoutS = 5.0; // documented EventBridge client execution timeout per API-destination call
    private const int MaxEventAgeS = 86400; // the documented maximum, so event AGE never terminates either twin
    private const double AuthorizedTimeoutS = 120.0;
    private const double ActiveTimeoutS = 90.0;
    private const double LivenessTimeoutS = 150.0;
    private const double RetryDlqTimeoutS = 150.0;
    private const double ExtraCountWindowS = 20.0;
    private const double PromptDlqS = 180.0;
    private const string UnroutableEndpoint = "https://192.0.2.1/sim2real-hook";
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>The check's <c>time.monotonic()</c>: seconds on the check's clock.</summary>
    private static double Monotonic() => FidelityTime.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// Twin API-destination rules differing only in MaximumRetryAttempts dead-letter the same event,
    /// the retrying twin at least one 5 s endpoint timeout later per extra retry (a lower bound, since
    /// backoff with jitter only adds), each DLQ message naming its own rule.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.api-destination-timeout-retries-are-bounded-by-maximum-retry-attempts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = XName("src");

        var (bus, _) = await CreateBus(ctx);

        var connName = FidelityContext.UniqueName("sim2real-xsvc-conn-");
        var connArn = (await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "sim2real-not-a-secret" },
            },
        })).ConnectionArn;
        ctx.OnCleanup(() => DeleteConnection(ctx, connName));
        var authorized = await PollUntil(
            () => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = connName }),
            d => d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED",
            timeoutS: AuthorizedTimeoutS,
            intervalS: 2.0,
            description: $"connection {connName} leaves CREATING");
        Ensure(
            authorized.ConnectionState?.Value == "AUTHORIZED",
            "the connection must reach AUTHORIZED before the destination can be invoked, got " +
            $"'{authorized.ConnectionState?.Value}' StateReason='{authorized.StateReason}'");

        var destName = FidelityContext.UniqueName("sim2real-xsvc-apidest-");
        var destArn = (await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = destName,
            ConnectionArn = connArn,
            InvocationEndpoint = UnroutableEndpoint,
            HttpMethod = ApiDestinationHttpMethod.POST,
            InvocationRateLimitPerSecond = 300,
        })).ApiDestinationArn;
        ctx.OnCleanup(() => DeleteApiDestination(ctx, destName));
        var destState = await PollUntil(
            () => events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName }),
            d => d.ApiDestinationState?.Value is "ACTIVE" or "INACTIVE",
            timeoutS: ActiveTimeoutS,
            intervalS: 2.0,
            description: $"api destination {destName} settles");
        Ensure(
            destState.ApiDestinationState?.Value == "ACTIVE",
            "the api destination must be ACTIVE for EventBridge to attempt it, got " +
            $"'{destState.ApiDestinationState?.Value}'");

        var roleArn = CreateEventsRole(ctx);

        var dlqZero = await CreateQueue(ctx);
        var dlqRetry = await CreateQueue(ctx);
        var pattern = $$"""{"source": ["{{source}}"]}""";

        var ruleZero = await PutRuleWithApiDestination(ctx, bus, pattern, destArn, roleArn, dlqZero, retries: 0);
        var ruleRetry = await PutRuleWithApiDestination(ctx, bus, pattern, destArn, roleArn, dlqRetry, retries: Retries);

        // --- both twins live, and the measured marker, in one loop: emit a fresh marker until the
        // ZERO-retry rule dead-letters it, which is the earliest possible signal that the wiring works.
        var (marker, zeroCapture, sentAt) = await EmitUntilDeadLettered(ctx, bus, source, dlqZero.Url);
        var elapsedZero = zeroCapture.At - sentAt;
        var zeroDupes = await CollectMarker(ctx, dlqZero.Url, marker, timeoutS: ExtraCountWindowS);
        Ensure(
            zeroDupes.Count == 0,
            $"the zero-retry DLQ received {zeroDupes.Count + 1} copies of marker {marker} — an event that " +
            "has been dead-lettered is terminal and must arrive exactly once (H4)");
        Ensure(
            elapsedZero <= PromptDlqS,
            $"with MaximumRetryAttempts=0 the event must reach the DLQ promptly: took {elapsedZero:0.0}s, " +
            $"expected no more than {PromptDlqS}s; a slow zero-retry leg would make the separation " +
            "assertion below meaningless");

        // --- the twin: the SAME event, on the SAME destination, with _RETRIES extra attempts. ---
        var (retryCapture, _, retryAt) = await AwaitMarker(ctx, dlqRetry.Url, marker, timeoutS: RetryDlqTimeoutS);
        var elapsedRetry = retryAt - sentAt;
        var retryDupes = await CollectMarker(ctx, dlqRetry.Url, marker, timeoutS: ExtraCountWindowS);
        Ensure(
            retryDupes.Count == 0,
            $"the {Retries}-retry DLQ received {retryDupes.Count + 1} copies of marker {marker} — one " +
            "dead-lettered event must produce exactly one DLQ message (H4)");

        // --- the measurement: _RETRIES extra attempts cost at least _RETRIES endpoint timeouts. ---
        const double floor = Retries * EndpointTimeoutS;
        Ensure(
            elapsedRetry - elapsedZero >= floor,
            $"MaximumRetryAttempts={Retries} did not cost any measurable extra time: the same event " +
            $"reached the zero-retry DLQ after {elapsedZero:0.0}s and the {Retries}-retry DLQ after " +
            $"{elapsedRetry:0.0}s, a separation of {elapsedRetry - elapsedZero:0.0}s where at least " +
            $"{floor:0}s ({Retries} x the documented {EndpointTimeoutS}s endpoint timeout) is " +
            "required. A target that ignores RetryPolicy dead-letters both at the same moment (H1, H2)");

        // --- both DLQ envelopes are the original event, tagged with their OWN rule. ---
        foreach (var (label, captured, rule) in new[] { ("zero-retry", zeroCapture, ruleZero), ($"{Retries}-retry", retryCapture, ruleRetry) })
        {
            var body = captured.Body;
            var attrs = captured.Attrs;
            Ensure(body is JsonObject, $"the {label} DLQ body must be a JSON event envelope, got {body?.ToJsonString()}");
            Ensure(
                (string?)body!["detail"]?["marker"] == marker,
                $"the {label} DLQ body must be the original event: expected detail.marker='{marker}', got " +
                $"{Truncate(body.ToJsonString(), 200)}");
            Ensure(
                (string?)body["source"] == source,
                $"the {label} DLQ body must keep the original source '{source}', got '{(string?)body["source"]}'");
            Ensure(
                attrs.GetValueOrDefault("RULE_ARN")?.StringValue == rule.Arn,
                $"the {label} DLQ message must name its OWN rule: expected RULE_ARN='{rule.Arn}', got " +
                $"'{attrs.GetValueOrDefault("RULE_ARN")?.StringValue}' — without this the two arrivals cannot be attributed to the " +
                "two different retry policies");
            Ensure(
                attrs.GetValueOrDefault("TARGET_ARN")?.StringValue == destArn,
                $"the {label} DLQ message must name the api destination as the target: expected " +
                $"'{destArn}', got '{attrs.GetValueOrDefault("TARGET_ARN")?.StringValue}'");
            Ensure(
                !string.IsNullOrEmpty(attrs.GetValueOrDefault("ERROR_CODE")?.StringValue),
                $"the {label} DLQ message must carry a non-empty ERROR_CODE; attributes were [{string.Join(", ", attrs.Keys)}] (H3)");
        }

        var zeroCode = zeroCapture.Attrs.GetValueOrDefault("ERROR_CODE")?.StringValue;
        var retryCode = retryCapture.Attrs.GetValueOrDefault("ERROR_CODE")?.StringValue;
        Ensure(
            zeroCode == retryCode,
            "both twins failed the same way — an unreachable endpoint — so the DLQ ERROR_CODE must " +
            $"match: zero-retry reported '{zeroCode}', {Retries}-retry reported '{retryCode}' (H3)");

        // --- second observable: neither DLQ has anything left in it. ---
        foreach (var (label, queue) in new[] { ("zero-retry", dlqZero), ($"{Retries}-retry", dlqRetry) })
        {
            var depth = (await WaitCountersZero(ctx, sqs, queue.Url, ["ApproximateNumberOfMessages"]))["ApproximateNumberOfMessages"];
            Ensure(
                depth == "0",
                $"the {label} DLQ still reports ApproximateNumberOfMessages='{depth}' after both " +
                "arrivals were consumed, expected '0'");
        }
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private sealed record Rule(string Name, string Arn);

    private sealed record Captured(JsonNode? Body, Dictionary<string, SqsMessageAttributeValue> Attrs, double At);

    private static List<Tag> XsvcTagsKv() =>
        [new Tag { Key = ManagedTagKey, Value = "true" }, new Tag { Key = "sim2real:prefix", Value = XsvcPrefix.TrimEnd('-') }];

    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    private static string Truncate(string value, int length) => value[..Math.Min(length, value.Length)];

    /// <summary>Run a teardown call; an error other than ResourceNotFoundException is reported as a leak.</summary>
    private static async Task BestEffort(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not {what}: {error.Code}", error.Exception);
        }
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

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// Read the queue's *names* counters until each is "0" or cap_s ends; return the last read.
    ///
    /// SQS documents these counters as eventually consistent: they "may not achieve consistency until
    /// at least 1 minute after the producers stop sending messages" (GetQueueAttributes).
    /// </summary>
    private static async Task<Dictionary<string, string>> WaitCountersZero(FidelityContext ctx, Amazon.SQS.IAmazonSQS sqs, string queueUrl, List<string> names, double capS = 90.0, double intervalS = 3.0)
    {
        var start = FidelityTime.StartNew();
        var budgetS = BudgetS(ctx, capS);
        var stop = Math.Min(capS, budgetS);
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = names })).Attributes;
            if (names.All(n => attrs.GetValueOrDefault(n) == "0"))
            {
                return attrs;
            }

            if (start.Elapsed.TotalSeconds >= stop)
            {
                if (budgetS < capS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:0.0}s, before " +
                        $"the {capS}s counter wait ended; not graded");
                }

                return attrs;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    private static async Task<(string Name, string Arn)> CreateBus(FidelityContext ctx)
    {
        var events = ctx.Events;
        var name = XName("bus");
        var arn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => BestEffort(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }), $"delete event bus {name}"));
        return (name, arn);
    }

    private static Task DeleteConnection(FidelityContext ctx, string name) =>
        BestEffort(() => ctx.Events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }), $"delete connection {name}");

    private static Task DeleteApiDestination(FidelityContext ctx, string name) =>
        BestEffort(() => ctx.Events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = name }), $"delete api destination {name}");

    /// <summary>
    /// IAM is not emulated: the role's ARN comes from <see cref="FidelityContext.RoleArn"/>, and its
    /// trust and InvokeApiDestination policies (and their cleanups) are dropped.
    /// </summary>
    private static string CreateEventsRole(FidelityContext ctx)
    {
        var name = XName("role");
        return ctx.RoleArn(name[..Math.Min(64, name.Length)]);
    }

    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var name = XName("dlq");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Tags = new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') },
        })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>
    /// A rule whose only target is the API destination, with an explicit RetryPolicy and DLQ.
    /// MaximumEventAgeInSeconds is pinned to the maximum so event age never ends delivery.
    /// </summary>
    private static async Task<Rule> PutRuleWithApiDestination(FidelityContext ctx, string bus, string pattern, string destArn, string roleArn, Queue dlq, int retries)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var arn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern, Tags = XsvcTagsKv() })).RuleArn;

        async Task Teardown()
        {
            var ev = ctx.Events;
            await BestEffort(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ["t1"] }), $"remove target t1 from rule {name}");
            await BestEffort(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }), $"delete rule {name} on bus {bus}");
        }

        ctx.OnCleanup(Teardown);
        await AllowEventsSend(ctx, dlq, arn);
        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = name,
            EventBusName = bus,
            Targets =
            [
                new Target
                {
                    Id = "t1",
                    Arn = destArn,
                    RoleArn = roleArn,
                    RetryPolicy = new RetryPolicy { MaximumRetryAttempts = retries, MaximumEventAgeInSeconds = MaxEventAgeS },
                    DeadLetterConfig = new DeadLetterConfig { Arn = dlq.Arn },
                },
            ],
        });
        return new Rule(name, arn);
    }

    private static Task AllowEventsSend(FidelityContext ctx, Queue queue, string ruleArn) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Sid": "AllowEventsSend", "Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{queue.Arn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{ruleArn}}}}"}}}]}""",
            },
        });

    private static JsonNode? Body(Message msg)
    {
        try
        {
            return JsonNode.Parse(msg.Body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(msg.Body);
        }
    }

    /// <summary>Long-poll for up to window_s and return {body, attrs, at} for every message seen, deleting it.</summary>
    private static async Task<List<Captured>> Receive(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var output = new List<Captured>();
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < windowS)
        {
            var wait = (int)Math.Min(5, Math.Max(1, windowS - clock.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                output.Add(new Captured(Body(msg), msg.MessageAttributes ?? [], Monotonic()));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }

            if (output.Count > 0)
            {
                break;
            }
        }

        return output;
    }

    private static string? MarkerOf(Captured captured) =>
        captured.Body is JsonObject body && body["detail"] is JsonObject detail ? (string?)detail["marker"] : null;

    /// <summary>
    /// Poll until the message carrying `marker` arrives; raises on timeout.
    /// Returns (captured, seconds waited, monotonic arrival time).
    /// </summary>
    private static async Task<(Captured Captured, double Waited, double At)> AwaitMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var started = Monotonic();
        var strangers = new List<string?>();
        var deadline = started + timeoutS;
        while (Monotonic() < deadline)
        {
            foreach (var captured in await Receive(ctx, queueUrl, windowS: Math.Min(8.0, Math.Max(1.0, deadline - Monotonic()))))
            {
                if (MarkerOf(captured) == marker)
                {
                    return (captured, Monotonic() - started, captured.At);
                }

                strangers.Add(MarkerOf(captured));
            }
        }

        throw Fail(
            $"no dead-lettered message carrying detail.marker='{marker}' arrived on {queueUrl} within " +
            $"{timeoutS}s (markers seen meanwhile: [{string.Join(", ", strangers)}])");
    }

    /// <summary>Every FURTHER copy of `marker` seen over a fixed window. Used as the exactly-once count.</summary>
    private static async Task<List<Captured>> CollectMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var output = new List<Captured>();
        var deadline = Monotonic() + timeoutS;
        while (Monotonic() < deadline)
        {
            foreach (var captured in await Receive(ctx, queueUrl, windowS: Math.Min(8.0, Math.Max(1.0, deadline - Monotonic()))))
            {
                if (MarkerOf(captured) == marker)
                {
                    output.Add(captured);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// PutEvents with a fresh marker until one is dead-lettered on `dlq_url`; a new rule drops
    /// events until routing is live. Returns (marker, capture, monotonic send time of that attempt).
    /// </summary>
    private static async Task<(string Marker, Captured Capture, double SentAt)> EmitUntilDeadLettered(FidelityContext ctx, string bus, string source, string dlqUrl)
    {
        var events = ctx.Events;
        var deadline = Monotonic() + LivenessTimeoutS;
        var attempts = 0;
        while (Monotonic() < deadline)
        {
            attempts++;
            var marker = Guid.NewGuid().ToString("N");
            var sentAt = Monotonic();
            var resp = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        Source = source,
                        DetailType = "Undeliverable",
                        Detail = $$"""{"marker": "{{marker}}", "attempt": {{attempts}}}""",
                        EventBusName = bus,
                    },
                ],
            });
            Ensure(
                resp.FailedEntryCount == 0,
                $"PutEvents on bus {bus} must be accepted while establishing liveness, got FailedEntryCount={resp.FailedEntryCount}");
            foreach (var captured in await Receive(ctx, dlqUrl, windowS: 25.0))
            {
                if (MarkerOf(captured) == marker)
                {
                    return (marker, captured, sentAt);
                }
            }
        }

        throw Fail(
            $"no event emitted on bus {bus} was dead-lettered on {dlqUrl} after {attempts} PutEvents " +
            $"attempts over {LivenessTimeoutS}s — the zero-retry positive control could not be " +
            "established, so the retry-separation measurement is unprovable");
    }
}
