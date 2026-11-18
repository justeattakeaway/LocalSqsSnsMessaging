// Translated from behavioral/events/check_put_events_endpoint_id_with_unrelated_event_bus_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-endpoint-id-with-unrelated-event-bus-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsEndpointIdWithUnrelatedEventBusNameRejected
{
    private const string Check = "put-events-endpoint-id-with-unrelated-event-bus-name-rejected";

    /// <summary>
    /// PutEvents through a real global endpoint, naming an EventBusName that is neither of the
    /// endpoint's two configured buses, fails that one entry with InvalidArgument; the same
    /// endpoint still delivers normally once the call names one of its own buses.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_PutEvents.html
    /// </summary>
    [Test]
    [DisplayName("events.put-events-endpoint-id-with-unrelated-event-bus-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var (queueUrl, queueArn) = await CreateQueue(ctx);
        var partition = queueArn.Split(':')[1];
        var (endpointId, primaryBusArn) = await CreateEndpoint(ctx, partition);

        var unrelatedBusArn = $"arn:{partition}:events:{ctx.Region}:{ctx.Account}:event-bus/{FidelityContext.UniqueName("sim2real-noep-unrelated-")}";
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            EndpointId = endpointId,
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = unrelatedBusArn, Source = "sim2real.bogus", DetailType = "x",
                    Detail = """{"a": 1}""",
                },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 1,
            "expected PutEvents with an EventBusName outside the endpoint's own buses to fail " +
            $"that entry, got {Entries(resp)}");
        var entry = resp.Entries[0];
        Ensure(entry.ErrorCode == "InvalidArgument", $"unexpected error code: {Entry(entry)}");
        Ensure(
            entry.ErrorMessage ==
            "Parameter EventBusName is not valid. Reason: The endpoint is not associated with " +
            $"the event bus {unrelatedBusArn}.",
            $"unexpected error message: {Entry(entry)}");

        // positive control: the same call shape, naming one of the endpoint's own buses, still
        // succeeds and actually delivers -- the negative above did not disturb the real endpoint.
        var source = await PutRuleWithTarget(ctx, events, queueUrl, queueArn);
        var marker = FidelityContext.UniqueName("m-");
        resp = await events.PutEventsAsync(new PutEventsRequest
        {
            EndpointId = endpointId,
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = primaryBusArn, Source = source, DetailType = "x",
                    Detail = $$"""{"marker": "{{marker}}"}""",
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {Entries(resp)}");
        await PollForMarker(ctx, queueUrl, marker, capS: 90.0);
    }

    private static string Entry(PutEventsResultEntry e) =>
        $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}";

    private static string Entries(PutEventsResponse resp) => $"[{string.Join(", ", (resp.Entries ?? []).Select(Entry))}]";

    /// <summary>
    /// A cheap, always-healthy global endpoint: a CALCULATED health check with no children is
    /// always healthy, so no CloudWatch alarm is needed for a check that never fails over.
    /// </summary>
    /// <remarks>
    /// Route 53 is not emulated: the health check's ARN comes from
    /// <see cref="FidelityContext.HealthCheckArn"/>, and its creation, tags and cleanup are dropped.
    /// </remarks>
    private static async Task<(string EndpointId, string PrimaryBusArn)> CreateEndpoint(FidelityContext ctx, string partition)
    {
        var events = ctx.Events;
        var home = ctx.Region;
        var away = home != "us-west-2" ? "us-west-2" : "us-east-1";
        var healthCheckArn = FidelityContext.HealthCheckArn();
        var primaryBusArn = $"arn:{partition}:events:{home}:{ctx.Account}:event-bus/default";
        var secondaryBusArn = $"arn:{partition}:events:{away}:{ctx.Account}:event-bus/default";
        var name = FidelityContext.UniqueName("sim2real-behav-noep-");
        await events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = name,
            RoutingConfig = new RoutingConfig
            {
                FailoverConfig = new FailoverConfig
                {
                    Primary = new Primary { HealthCheck = healthCheckArn },
                    Secondary = new Secondary { Route = away },
                },
            },
            EventBuses = [new EndpointEventBus { EventBusArn = primaryBusArn }, new EndpointEventBus { EventBusArn = secondaryBusArn }],
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
        });
        ctx.OnCleanup(Teardown(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = name }), "ResourceNotFoundException"));
        var final = await PollUntil(
            ctx,
            () => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name }),
            r => r.State == EndpointState.ACTIVE,
            capS: 180.0,
            description: $"endpoint {name} ACTIVE");
        return (final.EndpointId, primaryBusArn);
    }

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-noepq-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(url), "AWS.SimpleQueueService.NonExistentQueue"));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    private static async Task<string> PutRuleWithTarget(FidelityContext ctx, IAmazonEventBridge events, string queueUrl, string queueArn)
    {
        var ruleName = FidelityContext.UniqueName("sim2real-behav-noeprule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{ruleName}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;

        async Task DeleteRule()
        {
            var error = await FidelityContext.CaptureError(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sim2real-target"] }));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName });
        }

        ctx.OnCleanup(Teardown(DeleteRule, "ResourceNotFoundException"));
        await ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{queueArn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{ruleArn}}}}"}}}]}""",
            },
        });
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sim2real-target", Arn = queueArn }] });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets failed: {resp.FailedEntries?.Count} failed entries");
        return ruleName;
    }

    /// <summary>Receive and delete until the event carrying marker arrives; return its envelope.</summary>
    private static async Task<JsonObject> PollForMarker(FidelityContext ctx, string queueUrl, string marker, double capS)
    {
        var sqs = ctx.Sqs;
        var timeoutS = BudgetS(ctx, capS);
        var clock = FidelityTime.StartNew();
        double remaining;
        while ((remaining = timeoutS - clock.Elapsed.TotalSeconds) > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = Math.Max(1, Math.Min(5, (int)remaining)),
            });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (body["detail"] is JsonObject detail && (string?)detail["marker"] == marker)
                {
                    return body;
                }
            }
        }

        throw Expired(ctx, $"an event carrying marker '{marker}' reaching {queueUrl}", timeoutS, "none seen");
    }

    /// <summary>Poll probe() until predicate(result), under a deadline capped by the run budget.</summary>
    private static async Task<T> PollUntil<T>(FidelityContext ctx, Func<Task<T>> probe, Func<T, bool> predicate, double capS, string description, double intervalS = 2.0)
    {
        var timeoutS = BudgetS(ctx, capS);
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

        throw Expired(ctx, description, timeoutS, $"last result: {last}");
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>The error for a capped wait that ran out: TimeoutError when the run budget ended it.</summary>
    private static Exception Expired(FidelityContext ctx, string description, double timeoutS, string detail) =>
        ctx.TimeLeftSeconds <= 6.0
            ? new TimeoutException($"{description}: the run budget ended first; not graded")
            : Fail($"{description} not met within {timeoutS:0}s; {detail}");

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
