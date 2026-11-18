// Translated from behavioral/events/check_put_events_endpoint_id_delivers_to_primary_and_replicates_to_secondary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-endpoint-id-delivers-to-primary-and-replicates-to-secondary</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsEndpointIdDeliversToPrimaryAndReplicatesToSecondary
{
    private const string Check = "put-events-endpoint-id-delivers-to-primary-and-replicates-to-secondary";

    /// <summary>
    /// PutEvents with EndpointId delivers to the primary bus's rule target; with replication
    /// enabled a second copy reaches the secondary bus's rule target, carrying the origin Region
    /// (not the Region it was delivered to) in `region` and the endpoint's own ARN in `resources`.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-global-endpoints.html
    /// </summary>
    [Test]
    [DisplayName("events.put-events-endpoint-id-delivers-to-primary-and-replicates-to-secondary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var home = ctx.Region;
        // A global endpoint's FailoverConfig requires a real, distinct secondary Region; the
        // literal here is "any Region other than the check's own", not a pin to a specific one.
        var away = home != "us-west-2" ? "us-west-2" : "us-east-1";
        var eventsAway = ctx.EventsClient(away);

        var (roleArn, primaryBusArn, secondaryBusArn) = CreateReplicationRole(ctx, home, away);
        var healthCheckArn = CreateCloudWatchMetricHealthCheck();

        var name = FidelityContext.UniqueName("sim2real-behav-gep-");
        // IAM propagation: CreateEndpoint validates the replication role's trust policy at call time,
        // and a role just created can briefly fail that check before it is visible everywhere. The
        // bus assumes no role, so the retry never triggers.
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
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.ENABLED },
            RoleArn = roleArn,
        });
        ctx.OnCleanup(Teardown(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = name }), "ResourceNotFoundException"));
        var final = await PollUntil(
            ctx,
            () => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name }),
            r => r.State == EndpointState.ACTIVE,
            capS: 180.0,
            description: $"endpoint {name} ACTIVE");
        var (endpointArn, endpointId) = (final.Arn, final.EndpointId);

        var source = FidelityContext.UniqueName("sim2real.geprep.");
        const string detailType = "sim2real-global-endpoint-replication";
        var (homeQueueUrl, homeQueueArn) = await CreateQueue(ctx, home);
        await PutRuleWithTarget(ctx, events, home, source, homeQueueUrl, homeQueueArn);
        var (awayQueueUrl, awayQueueArn) = await CreateQueue(ctx, away);
        await PutRuleWithTarget(ctx, eventsAway, away, source, awayQueueUrl, awayQueueArn);

        var marker = FidelityContext.UniqueName("m-");
        var putResp = await events.PutEventsAsync(new PutEventsRequest
        {
            EndpointId = endpointId,
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = primaryBusArn, Source = source, DetailType = detailType,
                    Detail = $$"""{"marker": "{{marker}}"}""",
                },
            ],
        });
        Ensure(putResp.FailedEntryCount == 0, $"PutEvents rejected the entry: {putResp.Entries?[0].ErrorCode} {putResp.Entries?[0].ErrorMessage}");

        var homeEnv = await PollForMarker(ctx, home, homeQueueUrl, marker, capS: 90.0);
        Ensure(
            (string?)homeEnv["source"] == source && (string?)homeEnv["detail-type"] == detailType,
            $"primary delivery does not carry the original event: {homeEnv.ToJsonString()}");
        Ensure(
            (string?)homeEnv["region"] == home,
            $"the primary copy's region should be '{home}', got '{(string?)homeEnv["region"]}'");
        Ensure(
            JsonNode.DeepEquals(homeEnv["resources"], new JsonArray(endpointArn)),
            "PutEvents through a global endpoint stamps the endpoint's own ARN into `resources`, " +
            $"got {homeEnv["resources"]?.ToJsonString()}, expected ['{endpointArn}']");

        var awayEnv = await PollForMarker(ctx, away, awayQueueUrl, marker, capS: 240.0);
        Ensure(
            (string?)awayEnv["source"] == source && (string?)awayEnv["detail-type"] == detailType,
            $"replicated event does not carry the original event: {awayEnv.ToJsonString()}");
        Ensure(
            (string?)awayEnv["account"] == ctx.Account,
            $"replicated event account '{(string?)awayEnv["account"]}', expected '{ctx.Account}'");
        Ensure(
            (string?)awayEnv["region"] == home,
            "a replicated event's `region` names the Region it was replicated FROM (the primary), " +
            $"got '{(string?)awayEnv["region"]}', expected '{home}'");
        Ensure(
            JsonNode.DeepEquals(awayEnv["resources"], new JsonArray(endpointArn)),
            "a replicated event's `resources` carries the global endpoint's own ARN, got " +
            $"{awayEnv["resources"]?.ToJsonString()}, expected ['{endpointArn}']");
    }

    /// <summary>
    /// Route 53 and CloudWatch are not emulated: the Python check backs the health check with a
    /// CloudWatch metric alarm it never moves out of OK, so the endpoint stays on its primary. The
    /// health check's ARN comes from <see cref="FidelityContext.HealthCheckArn"/>, and the alarm,
    /// the health check, their tags and cleanups are dropped.
    /// </summary>
    private static string CreateCloudWatchMetricHealthCheck() => FidelityContext.HealthCheckArn();

    /// <summary>
    /// IAM is not emulated: the role's ARN comes from <see cref="FidelityContext.RoleArn"/>, and its
    /// trust and replication policies (and their cleanups) are dropped.
    /// </summary>
    private static (string RoleArn, string PrimaryBusArn, string SecondaryBusArn) CreateReplicationRole(FidelityContext ctx, string home, string away)
    {
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-gerole-"));
        var partition = roleArn.Split(':')[1];
        var primaryBusArn = $"arn:{partition}:events:{home}:{ctx.Account}:event-bus/default";
        var secondaryBusArn = $"arn:{partition}:events:{away}:{ctx.Account}:event-bus/default";
        return (roleArn, primaryBusArn, secondaryBusArn);
    }

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, string region)
    {
        var sqs = ctx.Client(region);
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-geq-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(url), "AWS.SimpleQueueService.NonExistentQueue"));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    private static async Task<string> PutRuleWithTarget(FidelityContext ctx, IAmazonEventBridge events, string region, string source, string queueUrl, string queueArn)
    {
        var ruleName = FidelityContext.UniqueName("sim2real-behav-gerule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
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
        await ctx.Client(region).SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{queueArn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{ruleArn}}}}"}}}]}""",
            },
        });
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sim2real-target", Arn = queueArn }] });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets in {region} failed: {resp.FailedEntries?.Count} failed entries");
        return ruleArn;
    }

    /// <summary>Receive and delete until the event carrying marker arrives; return its envelope.</summary>
    private static async Task<JsonObject> PollForMarker(FidelityContext ctx, string region, string queueUrl, string marker, double capS)
    {
        var sqs = ctx.Client(region);
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
