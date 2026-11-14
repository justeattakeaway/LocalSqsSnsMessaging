// Translated from behavioral/events/check_rule_on_the_envelope_account_and_region_routes_only_this_accounts_events_here.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.rule-on-the-envelope-account-and-region-routes-only-this-accounts-events-here</summary>
[Category("AwsEvaluationSuite")]
public sealed class RuleOnTheEnvelopeAccountAndRegionRoutesOnlyThisAccountsEventsHere
{
    private const string Check = "rule-on-the-envelope-account-and-region-routes-only-this-accounts-events-here";
    private const string DetailType = "sim2real-envelope-route";

    /// <summary>
    /// A rule matching the envelope's account and region delivers each of this account's events with
    /// those fields, while rules matching anything but this account, or anything but this region,
    /// deliver none, graded after a source-only twin receives a fence entry sent last.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-event-patterns.html
    /// </summary>
    [Test]
    [DisplayName("events.rule-on-the-envelope-account-and-region-routes-only-this-accounts-events-here")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = FidelityContext.UniqueName("sim2real.envelope.");
        var patterns = new List<(string Key, string Pattern)>
        {
            ("here", $$"""{"source": ["{{source}}"], "account": ["{{ctx.Account}}"], "region": ["{{ctx.Region}}"]}"""),
            ("other-account", $$"""{"source": ["{{source}}"], "account": [{"anything-but": "{{ctx.Account}}"}]}"""),
            ("other-region", $$"""{"source": ["{{source}}"], "region": [{"anything-but": "{{ctx.Region}}"}]}"""),
            ("twin", $$"""{"source": ["{{source}}"]}"""),
        };
        var queues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, pattern) in patterns)
        {
            var ruleName = FidelityContext.UniqueName($"sim2real-evb-{key}-");
            var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
            {
                Name = ruleName,
                EventPattern = pattern,
                State = RuleState.ENABLED,
                Tags = [new Tag { Key = "sim2real", Value = Check }],
            })).RuleArn;
            ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName })));
            var (url, arn) = await CreateQueueWithEventBridgePolicy(ctx, ruleArn);
            var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sim2real-target", Arn = arn }] });
            Ensure(resp.FailedEntryCount == 0, $"put_targets failed: FailedEntryCount={resp.FailedEntryCount}");
            ctx.OnCleanup(() => IgnoreNotFound(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sim2real-target"] })));
            queues[key] = url;
        }

        // Positive control: re-emitted until the rule under test and the twin both deliver it.
        var warm = await WarmUp(ctx, events, source, [queues["here"], queues["twin"]]);

        List<string> graded = [FidelityContext.UniqueName("m-a-"), FidelityContext.UniqueName("m-b-")];
        var put = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [.. graded.Select(m => new PutEventsRequestEntry { Source = source, DetailType = DetailType, Detail = $$"""{"marker": "{{m}}"}""" })],
        });
        Ensure(put.FailedEntryCount == 0, $"put_events rejected an entry: FailedEntryCount={put.FailedEntryCount}");
        var putEntries = put.Entries ?? throw new KeyNotFoundException("'Entries'");
        if (putEntries.Count != graded.Count)
        {
            // zip(..., strict=True) raises ValueError when the lengths differ.
            throw new InvalidOperationException($"zip() argument 2 is {(putEntries.Count < graded.Count ? "shorter" : "longer")} than argument 1");
        }

        var ids = graded.Zip(putEntries, (m, e) => (m, e.EventId)).ToDictionary(p => p.m, p => p.EventId, StringComparer.Ordinal);
        var fence = FidelityContext.UniqueName("fence-");
        put = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = DetailType, Detail = $$"""{"marker": "{{fence}}"}""" }],
        });
        Ensure(put.FailedEntryCount == 0, $"put_events rejected the fence: FailedEntryCount={put.FailedEntryCount}");

        await Collect(ctx, queues["twin"], [.. graded, fence], timeoutS: 40.0);
        var here = await Collect(ctx, queues["here"], [.. graded, fence], timeoutS: 40.0);
        here.Remove(warm);
        var expectedKeys = new List<string>([.. graded, fence]).Order(StringComparer.Ordinal).ToList();
        Ensure(
            here.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedKeys),
            $"the account-and-region rule delivered {Repr(here.Keys.Order(StringComparer.Ordinal))}");
        foreach (var m in graded)
        {
            var env = here[m];
            var got = ((string?)env?["id"], (string?)env?["account"], (string?)env?["region"], (string?)env?["source"]);
            Ensure(
                got == (ids[m], ctx.Account, ctx.Region, source),
                $"entry {m} arrived as (id, account, region, source) ('{got.Item1}', '{got.Item2}', '{got.Item3}', '{got.Item4}')");
        }

        foreach (var key in new[] { "other-account", "other-region" })
        {
            var stray = await Drain(ctx, queues[key]);
            Ensure(stray.Count == 0, $"the {key} rule delivered {Repr(stray.Keys.Order(StringComparer.Ordinal))}, expected nothing");
        }
    }

    /// <summary>Re-emit one event until each queue in <c>queue_urls</c> received it; return its marker.</summary>
    private static async Task<string> WarmUp(FidelityContext ctx, AmazonEventBridgeClient events, string source, HashSet<string> queueUrls, double timeoutS = 40.0)
    {
        var warm = FidelityContext.UniqueName("warm-");
        var entry = new PutEventsRequestEntry { Source = source, DetailType = DetailType, Detail = $$"""{"marker": "{{warm}}"}""" };
        var sqs = ctx.Sqs;
        var pending = new HashSet<string>(queueUrls, StringComparer.Ordinal);
        var last = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var started = FidelityTime.StartNew();
        while (pending.Count > 0)
        {
            if (started.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail(
                    $"{pending.Count} rule queue(s) never received the warm-up within {timeoutS:0.0}s; " +
                    $"last markers per pending queue [{string.Join(", ", pending.Select(u => last.TryGetValue(u, out var l) ? Repr(l) : "None"))}]");
            }

            var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            Ensure(resp.FailedEntryCount == 0, $"put_events rejected the warm-up: FailedEntryCount={resp.FailedEntryCount}");
            foreach (var url in pending.ToList())
            {
                var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
                foreach (var msg in msgs)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                }

                last[url] = [.. msgs.Select(m => MarkerOf(JsonNode.Parse(m.Body)))];
                if (last[url].Contains(warm))
                {
                    pending.Remove(url);
                }
            }
        }

        return warm;
    }

    private static string? MarkerOf(JsonNode? body) =>
        body?["detail"] is JsonObject detail ? (string?)detail["marker"] : null;

    /// <summary>Drain until every marker in `wanted` has arrived; return marker -> envelope of all drained.</summary>
    private static async Task<Dictionary<string, JsonNode?>> Collect(FidelityContext ctx, string queueUrl, HashSet<string> wanted, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && !wanted.IsSubsetOf(seen.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body);
                seen[MarkerOf(body) ?? ""] = body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(
            wanted.IsSubsetOf(seen.Keys),
            $"queue received {Repr(seen.Keys.Where(m => m.Length > 0).Order(StringComparer.Ordinal))} within {timeoutS:0.0}s, expected {Repr(wanted.Order(StringComparer.Ordinal))}");
        return seen;
    }

    /// <summary>Receive until one empty long poll; return marker -> envelope of every message drained.</summary>
    private static async Task<Dictionary<string, JsonNode?>> Drain(FidelityContext ctx, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        while (true)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            if (msgs.Count == 0)
            {
                return seen;
            }

            foreach (var msg in msgs)
            {
                var body = JsonNode.Parse(msg.Body);
                seen[MarkerOf(body) ?? "None"] = body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }
    }

    private static async Task<(string Url, string Arn)> CreateQueueWithEventBridgePolicy(FidelityContext ctx, string ruleArn)
    {
        var sqs = ctx.Sqs;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-evb-queue-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridgeDelivery",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, queueArn);
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static string Repr(IEnumerable<string?> items) => "[" + string.Join(", ", items.Select(i => i is null ? "None" : $"'{i}'")) + "]";
}
