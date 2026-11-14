// Translated from behavioral/events/check_cross_region_bus_target_delivers_to_the_other_regions_rules_with_the_source_region_and_a_new_event_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.cross-region-bus-target-delivers-to-the-other-regions-rules-with-the-source-region-and-a-new-event-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class CrossRegionBusTargetDeliversToTheOtherRegionsRulesWithTheSourceRegionAndANewEventId
{
    private const string Check = "cross-region-bus-target-delivers-to-the-other-regions-rules-with-the-source-region-and-a-new-event-id";
    private const double DeliveryTimeoutS = 240.0;

    /// <summary>
    /// A rule targeting a custom bus in a second region forwards each matched event to that bus's
    /// rule and SQS target with the original source, detail-type, resources and detail, the ORIGIN
    /// region and this account, but a new EventId rather than the one PutEvents returned.
    /// </summary>
    [Test]
    [DisplayName("events.cross-region-bus-target-delivers-to-the-other-regions-rules-with-the-source-region-and-a-new-event-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Region;
        var away = SecondRegion(ctx);
        var evHome = ctx.Events;
        var evAway = ctx.EventsClient(away);

        var (dstBus, dstArn) = await CreateBus(ctx, evAway);
        Ensure(
            dstArn.Split(':')[3] == away,
            $"the bus created through the {away} endpoint has an ARN in region '{dstArn.Split(':')[3]}'");
        var (queueUrl, queueArn) = await CreateQueue(ctx, away);
        var dstRule = await CreateRule(ctx, evAway, dstBus, """{"source": [{"prefix": "sim2real.xregion."}]}""");
        var put = await evAway.PutTargetsAsync(new PutTargetsRequest { Rule = dstRule, EventBusName = dstBus, Targets = [new Target { Id = "q", Arn = queueArn }] });
        Ensure(put.FailedEntryCount == 0, $"PutTargets in {away} failed: {FailedEntries(put)}");

        var roleArn = ForwardingRole(ctx);
        var (srcBus, _) = await CreateBus(ctx, evHome);
        var source = FidelityContext.UniqueName("sim2real.xregion.");
        var srcRule = await CreateRule(ctx, evHome, srcBus, $$"""{"source": ["{{source}}"]}""");
        put = await evHome.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = srcRule,
            EventBusName = srcBus,
            Targets = [new Target { Id = "away-bus", Arn = dstArn, RoleArn = roleArn }],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets in {home} failed: {FailedEntries(put)}");

        var resource = $"arn:aws:s3:::{FidelityContext.UniqueName("sim2real-res-")}";
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        var got = new List<JsonNode>();
        var sqs = ctx.Client(away);
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(DeliveryTimeoutS);
        var nextPut = TimeSpan.Zero;
        bool Mine(JsonNode g) => (string?)g["detail"]?["m"] is { } m && sent.ContainsValue(m);
        while (clock.Elapsed < deadline && !got.Any(Mine))
        {
            if (clock.Elapsed >= nextPut)
            {
                // A just-created rule or role can drop events until delivery is live; re-publish.
                var marker = FidelityContext.UniqueName("m-");
                var resp = await evHome.PutEventsAsync(new PutEventsRequest
                {
                    Entries =
                    [
                        new PutEventsRequestEntry
                        {
                            EventBusName = srcBus, Source = source, DetailType = "forwarded",
                            Detail = $$"""{"m": "{{marker}}", "n": 1}""", Resources = [resource],
                        },
                    ],
                });
                Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {resp.Entries}");
                sent[resp.Entries[0].EventId] = marker;
                nextPut = clock.Elapsed + TimeSpan.FromSeconds(20.0);
            }

            var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in received.Messages ?? [])
            {
                got.Add(JsonNode.Parse(msg.Body)!);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        var mine = got.Where(Mine).ToList();
        Ensure(
            mine.Count > 0,
            $"no forwarded event reached the {away} queue within {DeliveryTimeoutS:0.0}s; " +
            $"{sent.Count} event(s) sent from {home}, {got.Count} other message(s)");
        var env = mine[0].AsObject();
        var envMarker = (string?)env["detail"]!["m"];
        var originId = sent.First(kv => kv.Value == envMarker).Key;
        var keys = env.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            keys.SequenceEqual(["account", "detail", "detail-type", "id", "region", "resources", "source", "time", "version"]),
            $"the forwarded envelope has keys [{string.Join(", ", keys.Select(k => $"'{k}'"))}]");
        var resourcesMatch = JsonNode.DeepEquals(env["resources"], new JsonArray(resource));
        Ensure(
            (string?)env["source"] == source && (string?)env["detail-type"] == "forwarded" && resourcesMatch && (string?)env["account"] == ctx.Account,
            "the forwarded envelope does not carry the original event: source match " +
            $"{PyBool((string?)env["source"] == source)}, detail-type '{(string?)env["detail-type"]}', resources match " +
            $"{PyBool(resourcesMatch)}, account match {PyBool((string?)env["account"] == ctx.Account)}");
        Ensure(
            (string?)env["region"] == home,
            $"the forwarded event's region must stay the origin region '{home}'; got '{(string?)env["region"]}'");
        Ensure(
            (string?)env["id"] != originId,
            "the event delivered in the second region carries the origin region's EventId; " +
            "cross-region forwarding assigns a new one");
        Ensure(!sent.ContainsKey((string?)env["id"] ?? ""), "the delivered id is one of the origin region's EventIds");
    }

    private static string PyBool(bool b) => b ? "True" : "False";

    private static string FailedEntries(PutTargetsResponse put) =>
        "[" + string.Join(", ", (put.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}")) + "]";

    /// <summary>
    /// A region that is NOT ctx.region and IS enabled for this account, derived at runtime; the
    /// same geography is preferred, and the choice is deterministic. The Python reads
    /// ec2.describe_regions(); for us-west-2 it resolves to us-east-1 (the Python run's stub and
    /// real AWS agree).
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

    private static async Task<string> CreateRule(FidelityContext ctx, IAmazonEventBridge events, string bus, string pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern });

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

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, string region)
    {
        var sqs = ctx.Client(region);
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
    private static string ForwardingRole(FidelityContext ctx) => ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-role-"));
}
