// Translated from behavioral/events/check_concurrent_put_events_unique_event_ids.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.concurrent-put-events-unique-event-ids</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentPutEventsUniqueEventIds
{
    /// <summary>
    /// N concurrent PutEvents calls are all accepted with distinct EventIds, and every one of the N
    /// events arrives at the rule's SQS target: no lost accepts, collisions or dropped deliveries.
    /// </summary>
    [Test]
    [DisplayName("events.concurrent-put-events-unique-event-ids")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.behav.source.");
        const string detailType = "ConcurrencyCheck";
        const int n = 10;

        var ruleName = FidelityContext.UniqueName("sim2real-evb-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{source}}"], "detail-type": ["{{detailType}}"]}""",
            State = "ENABLED",
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        var (queueUrl, queueArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);

        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sim2real-target", Arn = queueArn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sim2real-target"] }));

        // A freshly created rule/target drops events until the path goes live and EventBridge never
        // retries a dropped event, so the path must be proven live before the concurrent burst -- a
        // shortfall measured against a cold path is indistinguishable from warm-up.
        await SettleDeliveryPath(ctx, source, detailType, queueUrl, consecutive: 3);

        Task<PutEventsResponse> Send(string marker) =>
            ctx.Events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        Source = source,
                        DetailType = detailType,
                        Detail = $$"""{"marker": "{{marker}}"}""",
                    },
                ],
            });

        var markers = Enumerable.Range(0, n).Select(i => FidelityContext.UniqueName($"marker-{i}-")).ToList();
        var results = await RunConcurrent(markers.Select(m => (Func<Task<PutEventsResponse>>)(() => Send(m))).ToList());

        var nOk = results.Count(r => r.Exception is null);
        Ensure(
            nOk == n,
            $"expected all {n} PutEvents calls to succeed, got {nOk}; " +
            $"errors=[{string.Join(", ", ConcurrentErrorCodes(results).Select(c => $"'{c}'"))}]");

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (resp, _) in results)
        {
            Ensure((resp!.FailedEntryCount ?? 0) == 0, $"unexpected per-entry failure: {resp}");
            eventIds.Add(resp.Entries[0].EventId);
        }

        Ensure(eventIds.Count == n, $"EventIds must be unique per accepted event, got {{{string.Join(", ", eventIds.Select(e => $"'{e}'"))}}}");

        // PutEvents' response is not proof of delivery, so drain the target queue.
        var published = markers.ToHashSet(StringComparer.Ordinal);
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(90.0) && delivered.IsProperSubsetOf(published))
        {
            foreach (var msg in await Drain(sqs, queueUrl, windowS: 10.0))
            {
                if (Body(msg) is JsonObject body && body["detail"] is JsonObject detail)
                {
                    if (detail["marker"] is JsonValue v && v.TryGetValue<string>(out var marker) && published.Contains(marker))
                    {
                        delivered.Add(marker);
                    }
                }
            }
        }

        var missing = published.Except(delivered).Order(StringComparer.Ordinal).ToList();
        Ensure(
            missing.Count == 0,
            $"rule {ruleName} lost {missing.Count} of {n} concurrently published events; " +
            $"missing markers [{string.Join(", ", missing.Select(m => $"'{m}'"))}]");
    }

    /// <summary>Runs every call at once (a thread pool of len(fns) workers); each result is the response or the exception.</summary>
    private static async Task<List<(T? Response, Exception? Exception)>> RunConcurrent<T>(List<Func<Task<T>>> fns)
        where T : class
    {
        var tasks = fns.Select(fn => Task.Run(async () =>
        {
            try
            {
                return ((T?)await fn(), (Exception?)null);
            }
#pragma warning disable CA1031 // The Python records every exception as the call's result.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return (null, ex);
            }
        })).ToList();
        return [.. await Task.WhenAll(tasks)];
    }

    private static IEnumerable<string> ConcurrentErrorCodes<T>(List<(T? Response, Exception? Exception)> results)
    {
        foreach (var (_, ex) in results)
        {
            if (ex is Amazon.Runtime.AmazonServiceException ase)
            {
                yield return AwsError.From(ase).Code ?? "None";
            }
            else if (ex is not null)
            {
                yield return ex.GetType().Name;
            }
        }
    }

    private static async Task<(string Url, string Arn)> CreateQueueWithEventbridgePolicy(FidelityContext ctx, string ruleArn)
    {
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("sim2real-evb-queue-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];
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

    private static JsonNode? Body(SqsMessage msg)
    {
        var raw = msg.Body ?? "";
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    /// <summary>Long-poll the queue for up to window_s and return every message seen, deleting each.</summary>
    private static async Task<List<SqsMessage>> Drain(IAmazonSQS sqs, string queueUrl, double windowS)
    {
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                break;
            }

            foreach (var msg in msgs)
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen;
    }

    /// <summary>
    /// Emit single control events until `consecutive` of them round-trip back to back.
    /// </summary>
    /// <remarks>
    /// A rule and its target become effective progressively on real EventBridge, and a dropped event is
    /// never retried, so one arrival proves only that SOME path is live. Requiring several consecutive
    /// round trips is the barrier that makes a later shortfall attributable to loss rather than warm-up.
    /// </remarks>
    private static async Task SettleDeliveryPath(FidelityContext ctx, string source, string detailType, string queueUrl, int consecutive)
    {
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var streak = 0;
        for (var i = 0; i < 15; i++)
        {
            var nonce = FidelityContext.UniqueName("ctl-");
            await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = detailType, Detail = $$"""{"marker": "{{nonce}}"}""" }],
            });
            var got = false;
            var started = FidelityTime.StartNew();
            var deadline = TimeSpan.FromSeconds(10.0);
            while (started.Elapsed < deadline && !got)
            {
                foreach (var msg in await Drain(sqs, queueUrl, windowS: Math.Min(5.0, (deadline - started.Elapsed).TotalSeconds)))
                {
                    if (Body(msg) is JsonObject body && body["detail"] is JsonObject detail
                        && detail["marker"] is JsonValue v && v.TryGetValue<string>(out var marker) && marker == nonce)
                    {
                        got = true;
                    }
                }
            }

            streak = got ? streak + 1 : 0;
            if (streak >= consecutive)
            {
                return;
            }
        }

        throw Fail(
            $"the rule -> {queueUrl} delivery path never round-tripped {consecutive} consecutive " +
            $"control events (longest streak reached: {streak})");
    }
}
