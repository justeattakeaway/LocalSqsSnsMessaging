// Translated from behavioral/s3__sqs/check_notification_prefix_filter_url_decodes_before_key_matching.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.notification-prefix-filter-url-decodes-before-key-matching</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationPrefixFilterUrlDecodesBeforeKeyMatching
{
    // The prefix filter is configured URL-encoded ("%3D" for "="); AWS decodes it to "folder=tag/"
    // before matching. A key under the literal encoded text is a different string from a key under
    // the decoded prefix, so the two cases are distinguishable.
    private const string EncodedPrefix = "folder%3Dtag/";
    private const string DecodedPrefix = "folder=tag/";

    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A URL-encoded prefix filter decodes before matching object keys: a key under the decoded
    /// prefix notifies, a key matching only the literal encoded text does not, and
    /// GetBucketNotificationConfiguration echoes the filter value exactly as configured.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.notification-prefix-filter-url-decodes-before-key-matching")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await CreateBucket(ctx);
        var (queueUrl, queueArn) = await CreateQueue(ctx);

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string> { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations =
            [
                new QueueConfiguration
                {
                    Id = "prefix-filter",
                    Queue = queueArn,
                    Events = ["s3:ObjectCreated:*"],
                    Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule { Name = "prefix", Value = EncodedPrefix }] } },
                },
            ],
        });

        // Control-plane read: the filter value comes back exactly as supplied, not decoded.
        var config = await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket });
        var rules = (config.QueueConfigurations[0].Filter?.S3KeyFilter?.FilterRules ?? [])
            .Select(r => new Dictionary<string, object?> { ["Name"] = r.Name, ["Value"] = r.Value })
            .ToList();
        Ensure(
            Py.Equal(rules, new List<object?> { new Dictionary<string, object?> { ["Name"] = "Prefix", ["Value"] = EncodedPrefix } }),
            $"GetBucketNotificationConfiguration returned FilterRules {Py.Repr(rules)}, expected the supplied value {Py.Repr(EncodedPrefix)} echoed unchanged");

        const string missKey = EncodedPrefix + "literal-encoded-miss.txt";
        const string hitKey = DecodedPrefix + "decoded-hit.txt";

        // The literal-encoded key is put first so that, if it were wrongly delivered, its notification
        // would arrive before the later hit_key's -- making hit_key's arrival a proof that the drain
        // below did not simply stop too early to see a leaked miss notification.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = missKey, ContentBody = "miss" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = hitKey, ContentBody = "hit" });

        var (seenKeys, foundHit) = await DrainUntilKey(ctx, sqs, queueUrl, hitKey, capS: 180.0);

        Ensure(
            foundHit,
            $"expected {Py.Repr(hitKey)} (under the decoded prefix {Py.Repr(DecodedPrefix)}) to notify the queue; keys seen while draining: {Py.Repr(seenKeys)}");
        Ensure(
            !seenKeys.Contains(missKey),
            $"the literal encoded prefix text {Py.Repr(EncodedPrefix)} matched key {Py.Repr(missKey)} and it was delivered to the queue, but the filter must match only the url-decoded prefix {Py.Repr(DecodedPrefix)}");
    }

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary><c>_create_queue(ctx)</c>: a tagged queue, deleted at cleanup; its URL and ARN.</summary>
    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName($"{XsvcPrefix}q-"), Tags = XsvcResourceTags() })).QueueUrl;
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteQueue(sqs, url));
        return (url, arn);
    }

    /// <summary><c>_create_bucket(ctx)</c>: a bucket, emptied and deleted at cleanup, tagged when tagging works.</summary>
    private static async Task<string> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await FidelityContext.CaptureError(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Tag { Key = t.Key, Value = t.Value })],
        }));
        return name;
    }

    /// <summary>
    /// <c>_record_keys(msg)</c>: the object keys a notification's Records carry, URL-decoded back to
    /// the literal key; none for a message without Records (s3:TestEvent, or a body that isn't JSON).
    /// </summary>
    private static List<string?> RecordKeys(Message msg)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(msg.Body ?? string.Empty);
        }
        catch (JsonException)
        {
            return [];
        }

        if (body is not JsonObject obj)
        {
            return [];
        }

        var keys = new List<string?>();
        foreach (var record in obj["Records"]?.AsArray() ?? [])
        {
            var key = (string?)record?["s3"]?["object"]?["key"];
            keys.Add(key is null ? null : Uri.UnescapeDataString(key));
        }

        return keys;
    }

    /// <summary>
    /// <c>_drain_until_key(...)</c>: drains the queue, collecting every record key seen, until
    /// <paramref name="targetKey"/> arrives or the deadline (<paramref name="capS"/>, bounded by the
    /// runner's remaining time) passes.
    /// </summary>
    private static async Task<(List<string?> Keys, bool Found)> DrainUntilKey(FidelityContext ctx, IAmazonSQS sqs, string queueUrl, string targetKey, double capS = 180.0)
    {
        var left = ctx.TimeLeftSeconds;
        var budget = Math.Max(1.0, Math.Min(capS, left - 10.0));
        var started = FidelityTime.StartNew();
        var keys = new List<string?>();
        var found = false;
        while (started.Elapsed.TotalSeconds < budget && !found)
        {
            var wait = (int)Math.Min(5, Math.Max(1, budget - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                foreach (var key in RecordKeys(msg))
                {
                    keys.Add(key);
                    if (key == targetKey)
                    {
                        found = true;
                    }
                }
            }
        }

        return (keys, found);
    }
}
