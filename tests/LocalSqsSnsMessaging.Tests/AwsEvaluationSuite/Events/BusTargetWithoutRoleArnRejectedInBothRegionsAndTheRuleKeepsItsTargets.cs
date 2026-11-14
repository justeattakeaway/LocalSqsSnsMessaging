// Translated from behavioral/events/check_bus_target_without_role_arn_rejected_in_both_regions_and_the_rule_keeps_its_targets.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.bus-target-without-role-arn-rejected-in-both-regions-and-the-rule-keeps-its-targets</summary>
[Category("AwsEvaluationSuite")]
public sealed class BusTargetWithoutRoleArnRejectedInBothRegionsAndTheRuleKeepsItsTargets
{
    private const string Check = "bus-target-without-role-arn-rejected-in-both-regions-and-the-rule-keeps-its-targets";
    private const double DeliveryTimeoutS = 150.0;

    /// <summary>
    /// PutTargets naming a custom bus (this or a second region) without RoleArn is refused with
    /// "RoleArn is required for target &lt;bus arn&gt;." while the valid SQS target in the same request is
    /// kept and still delivers; the same bus target with a RoleArn is accepted.
    /// </summary>
    [Test]
    [DisplayName("events.bus-target-without-role-arn-rejected-in-both-regions-and-the-rule-keeps-its-targets")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var away = SecondRegion(ctx);
        var evAway = ctx.EventsClient(away);

        var (_, sameArn) = await CreateBus(ctx, events);
        var (_, awayArn) = await CreateBus(ctx, evAway);
        var (queueUrl, queueArn) = await CreateQueue(ctx);
        var (bus, _) = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.busrole.");
        var rule = await CreateRule(ctx, events, bus, new JsonObject { ["source"] = new JsonArray(source) });
        var put = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "q", Arn = queueArn }] });
        Ensure(put.FailedEntryCount == 0, $"PutTargets of the SQS target failed: {FailedEntries(put)}");

        foreach (var (label, targetArn) in new[] { ("same-region", sameArn), ("cross-region", awayArn) })
        {
            var (code, msg) = await Error(() => events.PutTargetsAsync(new PutTargetsRequest
            {
                Rule = rule,
                EventBusName = bus,
                Targets = [new Target { Id = "q", Arn = queueArn }, new Target { Id = "bus", Arn = targetArn }],
            }));
            Ensure(code == "ValidationException", $"the {label} bus target without RoleArn gave '{code}'");
            Ensure(
                msg == $"RoleArn is required for target {targetArn}.",
                $"the {label} refusal message was '{msg}'");
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? [])
                .Select(t => t.Id).Order(StringComparer.Ordinal).ToList();
            Ensure(ids.SequenceEqual(["q"]), $"after the refused {label} PutTargets the rule lists targets {PyList(ids)}");
        }

        // Positive control: the rule's surviving target still delivers.
        await PublishUntilDelivered(ctx, events, bus, source, queueUrl);

        var roleArn = Role(ctx);
        put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "same", Arn = sameArn, RoleArn = roleArn },
                new Target { Id = "away", Arn = awayArn, RoleArn = roleArn },
            ],
        });
        Ensure(
            put.FailedEntryCount == 0,
            $"bus targets with a RoleArn must be accepted: {FailedEntries(put)}");
        var targets = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? [])
            .ToDictionary(t => t.Id, StringComparer.Ordinal);
        var sortedIds = targets.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(sortedIds.SequenceEqual(["away", "q", "same"]), $"the rule lists targets {PyList(sortedIds)}");
        Ensure(
            (targets["same"].RoleArn, targets["away"].RoleArn) == (roleArn, roleArn),
            "the accepted bus targets do not read back with the RoleArn they were given");
    }

    private static string FailedEntries(PutTargetsResponse put) =>
        "[" + string.Join(", ", (put.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}")) + "]";

    private static async Task<(string? Code, string Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("the call succeeded; real AWS refuses it");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>
    /// The Python picks the alphabetically first enabled region sharing ctx.region's prefix from
    /// ec2.describe_regions(); for us-west-2 that is us-east-1 (the Python run's stub and real AWS agree).
    /// </summary>
    private static string SecondRegion(FidelityContext ctx)
    {
        Ensure(ctx.Region == "us-west-2", $"no second region is known besides '{ctx.Region}'");
        return "us-east-1";
    }

    private static async Task<(string Name, string Arn)> CreateBus(FidelityContext ctx, IAmazonEventBridge events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var arn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return (bus, arn);
    }

    private static async Task<string> CreateRule(FidelityContext ctx, IAmazonEventBridge events, string bus, JsonObject pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern.ToJsonString() });

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
        return name;
    }

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-q-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{arn}}}}", "Condition": {"StringEquals": {"aws:SourceAccount": "{{{{ctx.Account}}}}"}}}]}""",
            },
        });
        return (url, arn);
    }

    /// <summary>
    /// IAM is not emulated: the role's ARN comes from <see cref="FidelityContext.RoleArn"/>, and
    /// its trust and PutEvents policies (and their cleanups) are dropped.
    /// </summary>
    private static string Role(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-role-");
        return ctx.RoleArn(name);
    }

    private static async Task PublishUntilDelivered(FidelityContext ctx, IAmazonEventBridge events, string bus, string source, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(DeliveryTimeoutS);
        var nextPut = TimeSpan.Zero;
        while (clock.Elapsed < deadline)
        {
            if (clock.Elapsed >= nextPut)
            {
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "control", Detail = "{}" }],
                });
                sent.Add(resp.Entries[0].EventId);
                nextPut = clock.Elapsed + TimeSpan.FromSeconds(15.0);
            }

            var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in received.Messages ?? [])
            {
                if ((string?)JsonNode.Parse(msg.Body)?["id"] is { } id && sent.Contains(id))
                {
                    return;
                }
            }
        }

        throw Fail(
            $"the rule's SQS target delivered none of {sent.Count} control event(s) within " +
            $"{DeliveryTimeoutS:0.0}s");
    }

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}
