// Translated from behavioral/s3/check_notification_filter_non_matching_key_not_delivered.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.notification-filter-non-matching-key-not-delivered</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationFilterNonMatchingKeyNotDelivered
{
    /// <summary>
    /// A prefix+suffix-filtered S3 notification delivers only keys matching both rules. Keys
    /// breaking either rule are written but not delivered, and the matching key is still delivered
    /// afterward.
    /// </summary>
    [Test]
    [DisplayName("s3.notification-filter-non-matching-key-not-delivered")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-", s3);
        var (queueUrl, queueArn) = await CreateNotifiedQueue(ctx, bucket);
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations =
            [
                new QueueConfiguration
                {
                    Id = "only-incoming-csv",
                    Queue = queueArn,
                    Events = ["s3:ObjectCreated:*"],
                    Filter = new Filter
                    {
                        S3KeyFilter = new S3KeyFilter
                        {
                            FilterRules =
                            [
                                new FilterRule { Name = "prefix", Value = "incoming/" },
                                new FilterRule { Name = "suffix", Value = ".csv" },
                            ],
                        },
                    },
                },
            ],
        });

        // Burn the s3:TestEvent that configuration emits so it cannot be mistaken for a delivery.
        await DrainRecords(sqs, queueUrl, timeoutS: 20);

        // --- Control: the one key that satisfies BOTH rules must be delivered.
        const string matchKey = "incoming/report.csv";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = matchKey, ContentBody = "a,b\n1,2\n" });
        var delivered = await DrainRecords(sqs, queueUrl, timeoutS: 60);
        Ensure(
            delivered.Contains(matchKey),
            $"matching key {Py.Repr(matchKey)} was not delivered within 60s; got {Py.Repr(delivered)} " +
            "(the positive half of the filter contract is broken, so the negative half below " +
            "would be vacuously true)");

        // --- Negative: break exactly one filter component at a time. None may be delivered.
        var nonMatching = new Dictionary<string, string>
        {
            ["other/report.csv"] = "prefix does not match",
            ["incoming/report.json"] = "suffix does not match",
            ["other/report.json"] = "neither prefix nor suffix matches",
            ["report.csv"] = "suffix matches but the key is at the bucket root",
            ["incoming/nested/report.csv.bak"] = "suffix is .bak, not .csv",
        };
        foreach (var key in nonMatching.Keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" });
        }

        var leaked = await DrainRecords(sqs, queueUrl, timeoutS: 60);
        Ensure(
            leaked.Count == 0,
            "S3 delivered notifications for keys that do not match the prefix/suffix filter: "
            + string.Join(", ", leaked.Select(k => $"{Py.Repr(k)} ({(k is not null && nonMatching.TryGetValue(k, out var why) ? why : "unexpected key")})")));

        // --- Effect present: the suppressed puts still created real objects. Suppression is a
        // routing decision, NOT a write failure — a target that "passes" by rejecting the put
        // must fail here.
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var paginator = s3.Paginators.ListObjectsV2(new ListObjectsV2Request { BucketName = bucket });
        await foreach (var page in paginator.Responses)
        {
            foreach (var o in page.S3Objects ?? [])
            {
                listed.Add(o.Key);
            }
        }

        var expected = new HashSet<string>(nonMatching.Keys, StringComparer.Ordinal) { matchKey };
        Ensure(
            listed.SetEquals(expected),
            $"expected objects {Py.Repr(expected.Order(StringComparer.Ordinal).ToList())} to exist, listing had {Py.Repr(listed.Order(StringComparer.Ordinal).ToList())}");

        // --- Control still works after the negatives: the filter is not left in a broken state.
        const string secondMatch = "incoming/second.csv";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = secondMatch, ContentBody = "c,d\n3,4\n" });
        var delivered2 = await DrainRecords(sqs, queueUrl, timeoutS: 60);
        Ensure(
            delivered2.Contains(secondMatch),
            $"matching key {Py.Repr(secondMatch)} was not delivered after the non-matching puts; got {Py.Repr(delivered2)}");
    }

    /// <summary>Create an SQS queue whose policy lets `bucket` send it S3 events; returns (url, arn).</summary>
    private static async Task<(string Url, string Arn)> CreateNotifiedQueue(FidelityContext ctx, string bucket)
    {
        var sqs = ctx.Sqs;
        var queueUrl = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-q-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });
        return (queueUrl, queueArn);
    }

    /// <summary>
    /// Receive-and-delete S3 event records for timeout_s; return keys of s3:Object* records.
    /// Always burns the full window, since one empty long-poll receive does not mean nothing
    /// arrives.
    /// </summary>
    private static async Task<List<string?>> DrainRecords(AmazonSQSClient sqs, string queueUrl, double timeoutS)
    {
        var keys = new List<string?>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(msg.Body);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (body is not JsonObject obj)
                {
                    continue;
                }

                if ((string?)obj["Event"] == "s3:TestEvent")
                {
                    continue;
                }

                foreach (var rec in obj["Records"]?.AsArray() ?? [])
                {
                    keys.Add((string?)rec?["s3"]?["object"]?["key"]);
                }
            }
        }

        return keys;
    }
}
