// Translated from behavioral/events/check_sns_target_publishes_the_compact_envelope_and_a_plain_text_template_keeps_its_quotes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.sns-target-publishes-the-compact-envelope-and-a-plain-text-template-keeps-its-quotes</summary>
[Category("AwsEvaluationSuite")]
public sealed class SnsTargetPublishesTheCompactEnvelopeAndAPlainTextTemplateKeepsItsQuotes
{
    private const string Check = "sns-target-publishes-the-compact-envelope-and-a-plain-text-template-keeps-its-quotes";
    private const double DeliveryTimeoutS = 180.0;

    // json.dumps(env, separators=(",", ":")) escapes only quotes, backslashes and control characters in ASCII text.
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Two SNS targets on one topic publish one notification each: the raw target's Message is the
    /// compact nine-key event envelope, and a plain-text template keeps its double quotes; neither
    /// notification carries a Subject or MessageAttributes.
    /// </summary>
    [Test]
    [DisplayName("events.sns-target-publishes-the-compact-envelope-and-a-plain-text-template-keeps-its-quotes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeSnsTargets, "a rule's SNS topic targets publish nothing (only SQS targets are delivered); AWS publishes one notification per target")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var topicArn = await CreateTopic(ctx);
        var subUrl = await SubscribeQueue(ctx, topicArn);

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.sns.");
        var rule = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "raw", Arn = topicArn },
                new Target
                {
                    Id = "text",
                    Arn = topicArn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.m" },
                        InputTemplate = "\"order <m> placed\"",
                    },
                },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {put.FailedEntries?.Count} failed entries");

        var resource = $"arn:aws:s3:::{FidelityContext.UniqueName("sim2real-res-")}";
        var notes = new List<JsonNode?>();
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);

        string? Pair()
        {
            foreach (var (eid, marker) in sent)
            {
                var hasRaw = notes.Any(n => RawId(n) == eid);
                var hasText = notes.Any(n => (string?)n!["Message"] == $"\"order {marker} placed\"");
                if (hasRaw && hasText)
                {
                    return eid;
                }
            }

            return null;
        }

        var started = FidelityTime.StartNew();
        var nextPut = TimeSpan.Zero;
        while (started.Elapsed < TimeSpan.FromSeconds(DeliveryTimeoutS) && Pair() is null)
        {
            if (started.Elapsed >= nextPut)
            {
                // A just-created rule can drop events until routing is live; re-publish.
                var m = FidelityContext.UniqueName("m-");
                var resp = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries =
                    [
                        new PutEventsRequestEntry
                        {
                            EventBusName = bus,
                            Source = source,
                            DetailType = "order.placed",
                            Detail = $$"""{"m": "{{m}}", "qty": 2}""",
                            Resources = [resource],
                        },
                    ],
                });
                Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}'}}"))}");
                sent[(resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId] = m;
                nextPut = started.Elapsed + TimeSpan.FromSeconds(20.0);
            }

            notes.AddRange(await Receive(ctx, subUrl));
        }

        var eventId = Pair();
        Ensure(
            eventId is not null,
            $"no event produced both notifications within {DeliveryTimeoutS:0.0}s; {notes.Count} notification(s) received");
        var marker = sent[eventId];
        var raw = notes.First(n => RawId(n) == eventId)!;
        var text = notes.First(n => (string?)n!["Message"] == $"\"order {marker} placed\"")!;

        var rawMessage = (string)raw["Message"]!;
        var env = JsonNode.Parse(rawMessage)!.AsObject();
        Ensure(
            rawMessage == env.ToJsonString(CompactJson),
            $"the raw target's Message is not compact JSON: '{rawMessage[..Math.Min(200, rawMessage.Length)]}'");
        string[] nineKeys = ["version", "id", "detail-type", "source", "account", "time", "region", "resources", "detail"];
        Ensure(
            env.Select(kv => kv.Key).Order(StringComparer.Ordinal).SequenceEqual(nineKeys.Order(StringComparer.Ordinal)),
            $"the published envelope's keys are [{string.Join(", ", env.Select(kv => $"'{kv.Key}'"))}]");
        Ensure(
            (string?)env["source"] == source && (string?)env["detail-type"] == "order.placed" &&
            JsonNode.DeepEquals(env["resources"], new JsonArray(resource)) &&
            JsonNode.DeepEquals(env["detail"], JsonNode.Parse($$"""{"m": "{{marker}}", "qty": 2}""")) &&
            (string?)env["account"] == ctx.Account && (string?)env["region"] == ctx.Region,
            $"the published envelope is not the event: {env.ToJsonString()}");
        foreach (var (label, note) in new[] { ("raw", raw), ("text", text) })
        {
            Ensure(
                (string?)note["Type"] == "Notification" && (string?)note["TopicArn"] == topicArn,
                $"the {label} notification is a '{note["Type"]}' from topic match {((string?)note["TopicArn"] == topicArn ? "True" : "False")}");
            var obj = note.AsObject();
            Ensure(
                !obj.ContainsKey("Subject") && !obj.ContainsKey("MessageAttributes"),
                $"the {label} notification carries Subject={obj["Subject"]?.ToJsonString() ?? "None"} " +
                $"MessageAttributes={obj["MessageAttributes"]?.ToJsonString() ?? "None"}");
        }

        Ensure((string?)raw["MessageId"] != (string?)text["MessageId"], "the two targets' notifications share a MessageId");
    }

    private static string? RawId(JsonNode? note)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse((string)note!["Message"]!);
        }
        catch (JsonException)
        {
            return null;
        }

        return body is JsonObject o ? (string?)o["id"] : null;
    }

    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-topic-"),
            Tags = [new Amazon.SimpleNotificationService.Model.Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn,
            AttributeName = "Policy",
            AttributeValue = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sns:Publish", "Resource": "{{{{arn}}}}", "Condition": {"StringEquals": {"aws:SourceAccount": "{{{{ctx.Account}}}}"}}}]}""",
        });
        return arn;
    }

    private static async Task<string> SubscribeQueue(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var sns = ctx.Sns;
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
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "sns.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{arn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{topicArn}}}}"}}}]}""",
            },
        });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = arn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
        return url;
    }

    private static async Task<string> CreateBus(FidelityContext ctx, AmazonEventBridgeClient events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return bus;
    }

    private static async Task<string> CreateRule(FidelityContext ctx, AmazonEventBridgeClient events, string bus, string pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern });

        ctx.OnCleanup(async () =>
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets
                ?? throw new KeyNotFoundException("'Targets'")).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus });
        });
        return name;
    }

    private static async Task<List<JsonNode?>> Receive(FidelityContext ctx, string url)
    {
        var sqs = ctx.Sqs;
        var output = new List<JsonNode?>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
        foreach (var msg in resp.Messages ?? [])
        {
            output.Add(JsonNode.Parse(msg.Body));
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        }

        return output;
    }
}
