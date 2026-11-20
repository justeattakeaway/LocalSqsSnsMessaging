// Translated from behavioral/events__s3__sqs/check_s3_event_wrong_type_not_delivered.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.S3QueueNotificationKit;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-event-wrong-type-not-delivered</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3EventWrongTypeNotDelivered
{
    private const double ControlTimeoutS = 90.0;

    /// <summary>A notification config for ObjectRemoved must not fire when an object is created.</summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-event-wrong-type-not-delivered")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx);

        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, bucket.Arn));
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            QueueConfigurations = [QueueConfig(queue.Arn, ["s3:ObjectRemoved:*"])],
        });

        // Positive control on the exact same wiring: without this, a target with no S3-notification
        // delivery path at all would score identically to one that correctly filters by event type,
        // since neither would ever put a message on the queue.
        const string controlKey = "control-for-removal.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = controlKey, ContentBody = "control" });
        var deleteStarted = FidelityTime.StartNew();
        await s3.DeleteObjectAsync(bucket.Name, controlKey);
        var (controlMessages, latencyS) = await PollForRemovalEvent(ctx, queue.Url, deleteStarted);
        Ensure(
            controlMessages.Count > 0,
            "positive control failed: deleting an object under an ObjectRemoved:* subscription " +
            $"produced no notification within {ControlTimeoutS}s -- this wiring cannot deliver " +
            "at all, so the ObjectCreated absence below would prove nothing");
        var controlEventName = Str(Records(controlMessages[0])[0], "eventName") ?? string.Empty;
        Ensure(
            controlEventName.StartsWith("ObjectRemoved", StringComparison.Ordinal),
            $"positive control delivered the wrong event name: expected ObjectRemoved*, got {controlEventName}");

        // Negative: hold absence for >= 3x the positive control's own measured latency, so a
        // correct-but-slower target is never mistaken for one that blocked delivery.
        var windowS = Math.Max(15.0, 3 * latencyS);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "not-a-removal.txt", ContentBody = "data" });

        var messages = await PollForNoMessages(ctx, queue.Url, windowS: windowS);
        Ensure(
            messages.Count == 0,
            "queue received a notification for ObjectCreated even though only ObjectRemoved " +
            $"events were subscribed -- emulator is over-delivering events: [{string.Join(", ", messages.Select(m => m?.ToJsonString()))}]");

        var attrs = await WaitCountersZero(ctx, sqs, queue.Url, ["ApproximateNumberOfMessages"]);
        Ensure(
            attrs["ApproximateNumberOfMessages"] == "0",
            "queue attribute ApproximateNumberOfMessages should read 0 after the ObjectCreated " +
            $"window produced no delivery, got {Py.Repr(attrs["ApproximateNumberOfMessages"])}");
    }

    /// <summary>
    /// Watch a queue for <paramref name="windowS"/> and return whatever arrived (expected: nothing),
    /// exiting early as soon as any real message appears.
    /// </summary>
    private static async Task<List<JsonNode?>> PollForNoMessages(FidelityContext ctx, string queueUrl, double windowS = 45, bool ignoreS3TestEvent = true)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        var received = new List<JsonNode?>();
        while (started.Elapsed.TotalSeconds < windowS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                var body = Body(m);
                if (ignoreS3TestEvent && IsS3TestEvent(body))
                {
                    await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                    continue;
                }

                received.Add(body);
            }

            if (received.Count > 0)
            {
                break;
            }
        }

        return received;
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// Read the queue's counters until each is "0" or cap_s ends; return the last read. SQS
    /// documents these counters as eventually consistent.
    /// </summary>
    private static async Task<Dictionary<string, string>> WaitCountersZero(
        FidelityContext ctx, AmazonSQSClient sqs, string queueUrl, List<string> names, double capS = 90.0, double intervalS = 3.0)
    {
        var start = FidelityTime.StartNew();
        var budgetS = BudgetS(ctx, capS);
        var stop = Math.Min(capS, budgetS);
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = names })).Attributes;
            if (names.All(n => attrs.GetValueOrDefault(n) == "0"))
            {
                return attrs;
            }

            if (start.Elapsed.TotalSeconds >= stop)
            {
                if (budgetS < capS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before " +
                        $"the {capS}s counter wait ended (last={Py.Repr(attrs)}); not graded");
                }

                return attrs;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>
    /// Poll for the ObjectRemoved positive control; returns (messages, elapsed_s since
    /// <paramref name="startedAt"/>), this wiring's own delivery latency.
    /// </summary>
    private static async Task<(List<JsonNode?> Messages, double LatencyS)> PollForRemovalEvent(FidelityContext ctx, string queueUrl, FidelityStopwatch startedAt)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < ControlTimeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                var body = Body(m);
                await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                if (IsS3TestEvent(body))
                {
                    continue;
                }

                return ([body], startedAt.Elapsed.TotalSeconds);
            }
        }

        return ([], startedAt.Elapsed.TotalSeconds);
    }
}
