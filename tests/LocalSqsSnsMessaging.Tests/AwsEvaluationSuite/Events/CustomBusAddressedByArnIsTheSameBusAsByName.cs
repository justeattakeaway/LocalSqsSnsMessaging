// Translated from behavioral/events/check_custom_bus_addressed_by_arn_is_the_same_bus_as_by_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.custom-bus-addressed-by-arn-is-the-same-bus-as-by-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class CustomBusAddressedByArnIsTheSameBusAsByName
{
    private const string Check = "custom-bus-addressed-by-arn-is-the-same-bus-as-by-name";
    private const double DeliveryTimeoutS = 90.0;

    /// <summary>
    /// A custom bus addressed by ARN is the same bus as by name: DescribeEventBus(Name=&lt;arn&gt;)
    /// returns it, a rule created by ARN is listed by name, and an event put by ARN arrives with the
    /// same envelope as one put by name. IaC tools commonly pass the bus ARN rather than its name.
    /// </summary>
    [Test]
    [DisplayName("events.custom-bus-addressed-by-arn-is-the-same-bus-as-by-name")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var byArn = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busArn });
        Ensure(
            (byArn.Name, byArn.Arn) == (bus, busArn),
            $"DescribeEventBus by ARN must return the named bus, got ('{byArn.Name}', '{byArn.Arn}')");

        var source = FidelityContext.UniqueName("sim2real.byarn.");
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = busArn, EventPattern = $$"""{"source": ["{{source}}"]}""" })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(
            ruleArn == $"arn:aws:events:{ctx.Region}:{ctx.Account}:rule/{bus}/{rule}",
            $"a rule created on the bus ARN must carry the bus NAME in its ARN, got '{ruleArn}'");
        var byName = ((await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = rule })).Rules ?? []).Select(r => r.Name).ToList();
        Ensure(byName.SequenceEqual([rule]), $"the rule created via the bus ARN must be listed under the bus name, got {PyList(byName)}");

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-q-"), Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = $$$$"""{"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "events.amazonaws.com"}, "Action": "sqs:SendMessage", "Resource": "{{{{queueArn}}}}", "Condition": {"ArnEquals": {"aws:SourceArn": "{{{{ruleArn}}}}"}}}]}""",
            },
        });
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = busArn, Targets = [new Target { Id = "sim2real-q", Arn = queueArn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["sim2real-q"] }));
        var targets = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? []).Select(t => t.Arn).ToList();
        Ensure(targets.SequenceEqual([queueArn]), $"the target added via the bus ARN must be listed under the name, got {PyList(targets)}");

        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (spelling, address) in new[] { ("arn", busArn), ("name", bus) })
        {
            var resp = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        EventBusName = address, Source = source, DetailType = "sim2real-byarn",
                        Detail = $$"""{"spelling": "{{spelling}}"}""",
                    },
                ],
            });
            Ensure(resp.FailedEntryCount == 0, $"PutEvents by {spelling} was refused: {resp.Entries}");
            sent[resp.Entries[0].EventId] = spelling;
        }

        var received = await Receive(sqs, url, sent.Keys.ToHashSet(StringComparer.Ordinal));
        Ensure(
            received.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(sent.Keys),
            "events put by ARN and by name must both be delivered; got " +
            PyList(received.Values.Select(b => (string?)b["detail"]?["spelling"] ?? "None").Order(StringComparer.Ordinal)));
        foreach (var (eventId, body) in received)
        {
            Ensure(
                JsonNode.DeepEquals(body["detail"], new JsonObject { ["spelling"] = sent[eventId] }),
                $"detail mismatch for the {sent[eventId]} event");
            Ensure(
                ((string?)body["account"], (string?)body["region"], (string?)body["source"]) == (ctx.Account, ctx.Region, source),
                $"the {sent[eventId]}-addressed event's envelope must name this account, region and source");
        }
    }

    private static async Task<Dictionary<string, JsonNode>> Receive(IAmazonSQS sqs, string url, HashSet<string> ids)
    {
        var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(DeliveryTimeoutS) && !ids.SetEquals(found.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var m in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(m.Body)!;
                var id = (string?)body["id"];
                if (id is not null && ids.Contains(id))
                {
                    found[id] = body;
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return found;
    }

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}
