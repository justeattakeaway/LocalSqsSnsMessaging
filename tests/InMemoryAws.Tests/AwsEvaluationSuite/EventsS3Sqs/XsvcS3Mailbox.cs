using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>
/// The <c>_Mailbox</c> of the <c>xsvc-s3-1-*</c> detail-type checks: it keeps every body it drains,
/// so waiting for one event never discards another's evidence.
/// </summary>
internal sealed class XsvcS3Mailbox(AmazonSQSClient sqs, string url, string label)
{
    public string Label => label;

    public List<string> Bodies { get; } = [];

    private async Task Pull(int wait)
    {
        var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait })).Messages ?? [];
        if (msgs.Count > 0)
        {
            await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
            });
        }

        Bodies.AddRange(msgs.Select(m => m.Body));
    }

    public List<JsonObject> Envelopes() => [.. Bodies.Select(b => JsonNode.Parse(b)!.AsObject())];

    public HashSet<string> Types() => Envelopes().Select(e => (string)e["detail-type"]!).ToHashSet(StringComparer.Ordinal);

    /// <summary>(detail-type, deletion-type) of every envelope seen.</summary>
    public List<(string? DetailType, string? DeletionType)> Seen() =>
        [.. Envelopes().Select(e => ((string?)e["detail-type"], (string?)e["detail"]?["deletion-type"]))];

    /// <summary>(first matching envelope, seconds waited), or (null, null) if <paramref name="budgetS"/> runs out.</summary>
    public async Task<(JsonObject? Envelope, double? Latency)> Poll(Func<JsonObject, bool> predicate, double budgetS)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var hits = Envelopes().Where(predicate).ToList();
            if (hits.Count > 0)
            {
                return (hits[0], started.Elapsed.TotalSeconds);
            }

            var remaining = budgetS - started.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                return (null, null);
            }

            await Pull(Math.Max(1, Math.Min(5, (int)remaining)));
        }
    }

    public async Task<(JsonObject Envelope, double Latency)> WaitFor(Func<JsonObject, bool> predicate, double budgetS, string what)
    {
        var (env, latency) = await Poll(predicate, budgetS);
        if (env is null)
        {
            throw Fail($"{label}: {what} not delivered within {budgetS}s; (detail-type, deletion-type) seen so far: {Py.Repr(Seen())}");
        }

        return (env, latency!.Value);
    }

    public async Task Soak(double budgetS)
    {
        var started = FidelityTime.StartNew();
        double remaining;
        while ((remaining = budgetS - started.Elapsed.TotalSeconds) > 0)
        {
            await Pull(Math.Max(1, Math.Min(5, (int)remaining)));
        }
    }

    public async Task<Dictionary<string, string>> Depth() =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
        })).Attributes;

    public static async Task EmptyAndDeleteBucket(IAmazonS3 s3, string bucket)
    {
        var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
        var items = (versions.Versions ?? []).Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId }).ToList();
        if (items.Count > 0)
        {
            var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = items, Quiet = true });
            if (resp.DeleteErrors is { Count: > 0 } failed)
            {
                throw new InvalidOperationException($"DeleteObjects left {failed.Count} object(s)");
            }
        }

        await s3.DeleteBucketAsync(bucket);
    }

    /// <summary>A queue whose policy admits default-bus rules; S3 service events land on the DEFAULT bus.</summary>
    public static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, AmazonSQSClient sqs, string account, string prefix, string check)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName(prefix),
            Tags = new() { ["sim2real"] = check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "AllowEventBridge",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject
                        {
                            ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:events:{ctx.Region}:{account}:rule/*" },
                        },
                    }),
                }.ToJsonString(),
            },
        });
        return (url, arn);
    }

    public static async Task<string> MakeRule(FidelityContext ctx, AmazonEventBridgeClient ev, JsonObject pattern, string queueArn, string prefix, string check)
    {
        var rule = FidelityContext.UniqueName(prefix);
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            State = RuleState.ENABLED,
            EventPattern = pattern.ToJsonString(),
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = check }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "q", Arn = queueArn }] });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["q"] }));
        Ensure(resp.FailedEntryCount == 0, $"put_targets on {Py.Repr(rule)} reported failures: {Py.Repr(resp.FailedEntries?.Select(f => f.ErrorMessage).ToList())}");
        return rule;
    }
}
