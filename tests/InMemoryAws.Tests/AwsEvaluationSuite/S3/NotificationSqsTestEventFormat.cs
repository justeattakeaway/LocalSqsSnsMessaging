// Translated from behavioral/s3/check_notification_sqs_test_event_format.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.notification-sqs-test-event-format</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class NotificationSqsTestEventFormat
{
    private const string Check = "notification-sqs-test-event-format";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex TimeRe();

    /// <summary>
    /// The first PutBucketNotificationConfiguration to an SQS queue delivers a flat s3:TestEvent
    /// (Service, Event, Time, Bucket, RequestId, HostId; no Records) before any real event.
    /// </summary>
    [Test]
    [DisplayName("s3.notification-sqs-test-event-format")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await NewBucket(ctx, s3);
        var (queueUrl, queueArn) = await NewQueue(ctx, sqs, bucket);

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = queueArn, Events = ["s3:ObjectCreated:*"] }],
        });

        var body = await ReceiveOne(ctx, sqs, queueUrl);

        var keys = body.Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            keys.SequenceEqual(["Bucket", "Event", "HostId", "RequestId", "Service", "Time"]),
            $"expected the flat test-event field set, got keys {Py.Repr(keys)} in {body.ToJsonString()}");
        Ensure((string?)body["Service"] == "Amazon S3", $"expected Service 'Amazon S3', got {Py.Repr((string?)body["Service"])}");
        Ensure((string?)body["Event"] == "s3:TestEvent", $"expected Event 's3:TestEvent', got {Py.Repr((string?)body["Event"])}");
        Ensure((string?)body["Bucket"] == bucket, $"expected Bucket {Py.Repr(bucket)}, got {Py.Repr((string?)body["Bucket"])}");
        Ensure(TimeRe().IsMatch((string?)body["Time"] ?? string.Empty), $"expected an ISO-8601 millisecond UTC Time, got {Py.Repr((string?)body["Time"])}");
        Ensure(
            body["RequestId"] is JsonValue r && r.TryGetValue<string>(out var requestId) && requestId.Length > 0,
            $"expected a non-empty RequestId, got {body["RequestId"]?.ToJsonString()}");
        Ensure(
            body["HostId"] is JsonValue h && h.TryGetValue<string>(out var hostId) && hostId.Length > 0,
            $"expected a non-empty HostId, got {body["HostId"]?.ToJsonString()}");
    }

    /// <summary>Create a uniquely-named, tagged bucket, register teardown, return its name.</summary>
    private static async Task<string> NewBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        return name;
    }

    /// <summary>
    /// Create a uniquely-named queue, permit S3 to send to it, tag it, register teardown, return
    /// (queue_url, queue_arn).
    /// </summary>
    private static async Task<(string Url, string Arn)> NewQueue(FidelityContext ctx, AmazonSQSClient sqs, string bucket)
    {
        var queueUrl = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-q-"))).QueueUrl;
        ctx.OnCleanup(() => DeleteQueue(sqs, queueUrl));
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = queueUrl, Tags = new() { ["sim2real"] = Check } });
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:s3:::{bucket}" } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, queueArn);
    }

    private static async Task DeleteQueue(AmazonSQSClient sqs, string queueUrl)
    {
        try
        {
            await sqs.DeleteQueueAsync(queueUrl);
        }
        catch (AmazonSQSException ex) when (ex.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist")
        {
        }
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the run's remaining time less 5 s so the poll fails with
    /// its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// The JSON body of the first message on the queue, deleted once read; none within the budget
    /// raises TimeoutError.
    /// </summary>
    private static async Task<JsonObject> ReceiveOne(FidelityContext ctx, AmazonSQSClient sqs, string queueUrl, double capS = 60.0)
    {
        var timeoutS = BudgetS(ctx, capS);
        var started = FidelityTime.StartNew();
        while (true)
        {
            var wait = (int)Math.Min(2, Math.Max(0, timeoutS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = wait });
            if (resp.Messages is { Count: > 0 } messages)
            {
                await sqs.DeleteMessageAsync(queueUrl, messages[0].ReceiptHandle);
                return JsonNode.Parse(messages[0].Body)!.AsObject();
            }

            if (started.Elapsed.TotalSeconds >= timeoutS)
            {
                throw new TimeoutException($"no message arrived on the configured queue within {timeoutS:F0}s");
            }
        }
    }
}
