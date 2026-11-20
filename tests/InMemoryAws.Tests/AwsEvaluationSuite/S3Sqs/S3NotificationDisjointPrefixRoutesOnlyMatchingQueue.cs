// Translated from behavioral/s3__sqs/check_s3_notification_disjoint_prefix_routes_only_matching_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.s3-notification-disjoint-prefix-routes-only-matching-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3NotificationDisjointPrefixRoutesOnlyMatchingQueue
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Two prefix-scoped notification configs on one bucket route each object to its own queue
    /// only: the alpha/ arrival is the barrier for the beta queue staying empty, and a later beta/
    /// arrival proves the beta leg was live.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.s3-notification-disjoint-prefix-routes-only-matching-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx);
        var qAlpha = await CreateQueue(ctx);
        var qBeta = await CreateQueue(ctx);
        foreach (var q in new[] { qAlpha, qBeta })
        {
            await SetQueuePolicy(ctx, q.Url, AllowServiceSend(q.Arn, "s3.amazonaws.com", bucket.Arn));
        }

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            QueueConfigurations =
            [
                new QueueConfiguration
                {
                    Id = "alpha",
                    Queue = qAlpha.Arn,
                    Events = ["s3:ObjectCreated:*"],
                    Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule { Name = "prefix", Value = "alpha/" }] } },
                },
                new QueueConfiguration
                {
                    Id = "beta",
                    Queue = qBeta.Arn,
                    Events = ["s3:ObjectCreated:*"],
                    Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule { Name = "prefix", Value = "beta/" }] } },
                },
            ],
        });

        var started = FidelityTime.StartNew();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "alpha/one.txt", ContentBody = "a" });
        var (ctl, alphaOthers) = await WaitFor(ctx, qAlpha.Url, b => HasRecordFor(b, "alpha/one.txt"), timeoutS: 90.0);
        Ensure(ctl.Count >= 1, "control: alpha/one.txt must notify the alpha-prefixed queue");
        var alphaLatencyS = started.Elapsed.TotalSeconds;
        Ensure(
            ObjectKeys(alphaOthers).Count == 0,
            $"the alpha/-scoped destination received unexpected object notification(s) for {Py.Repr(ObjectKeys(alphaOthers))}");

        var leakWindowS = Math.Min(45.0, Math.Max(15.0, 3.0 * alphaLatencyS));
        var leaked = ObjectKeys(await Collect(ctx, qBeta.Url, windowS: leakWindowS));
        Ensure(
            leaked.Count == 0,
            $"the beta/-scoped destination received object notification(s) for {Py.Repr(leaked)} -- S3 is " +
            "fanning every event out to every configured destination instead of applying the per-" +
            "destination prefix filter");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "beta/two.txt", ContentBody = "b" });
        var (ctl2, _) = await WaitFor(ctx, qBeta.Url, b => HasRecordFor(b, "beta/two.txt"), timeoutS: 90.0);
        Ensure(
            ctl2.Count >= 1,
            "control: beta/two.txt must notify the beta-prefixed queue -- without this the empty beta " +
            "queue above could simply mean the beta destination was never wired");
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Queue(string Name, string Url, string Arn);

    private static bool HasRecordFor(JsonNode? body, string key) =>
        body is JsonObject obj && (obj["Records"]?.AsArray() ?? []).Any(r => (string?)r?["s3"]?["object"]?["key"] == key);

    /// <summary>Object keys carried by real notification messages; s3:TestEvent has no Records.</summary>
    private static List<string?> ObjectKeys(List<SqsMessage> msgs)
    {
        var keys = new List<string?>();
        foreach (var m in msgs)
        {
            if (Body(m) is not JsonObject b)
            {
                continue;
            }

            foreach (var r in b["Records"]?.AsArray() ?? [])
            {
                keys.Add((string?)r?["s3"]?["object"]?["key"]);
            }
        }

        return keys;
    }

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = XName("q");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = [], Tags = XsvcResourceTags() })).QueueUrl;
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Create an S3 bucket (registers empty-then-delete teardown). Returns {name,arn}.</summary>
    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await FidelityContext.CaptureError(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Tag { Key = t.Key, Value = t.Value })],
        }));
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(SqsMessage msg)
    {
        var raw = msg.Body ?? string.Empty;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    /// <summary>
    /// Drain a queue over a bounded window with long-polling ReceiveMessage(MaxNumberOfMessages=10)
    /// and return EVERY message seen.
    /// </summary>
    /// <remarks>
    /// This is the NEGATIVE observable. An empty return is only meaningful next to a positive control
    /// observed elsewhere in the check, which shows a delivery would have arrived in that time; used
    /// alone it would pass against a target that delivers nothing at all.
    /// </remarks>
    private static async Task<List<SqsMessage>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10, bool keep = false)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < windowS && seen.Count < maxTotal)
        {
            var wait = (int)Math.Min(5, Math.Max(1, windowS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                if (!keep)
                {
                    await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Poll a queue until a message whose parsed body satisfies predicate() arrives. Returns
    /// (matched, others) -- every non-matching message seen along the way is returned too so the
    /// caller can assert on leakage.
    /// </summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS)))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
    }

    /// <summary>A queue policy granting one AWS service SendMessage on this queue, scoped by SourceArn.</summary>
    private static JsonObject AllowServiceSend(string queueArn, string service, string sourceArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowServiceSend",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = service },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });
}
