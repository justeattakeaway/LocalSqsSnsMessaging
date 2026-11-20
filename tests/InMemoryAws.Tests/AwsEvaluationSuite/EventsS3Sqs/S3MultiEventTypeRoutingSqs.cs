// Translated from behavioral/events__s3__sqs/check_s3_multi_event_type_routing_sqs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.S3QueueNotificationKit;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-multi-event-type-routing-sqs</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3MultiEventTypeRoutingSqs
{
    /// <summary>
    /// An S3 notification configuration delivers ObjectCreated only to its queue and ObjectRemoved
    /// only to its own, with nothing leaking to the other queue in either direction.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-multi-event-type-routing-sqs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx);
        var createdQueue = await CreateQueue(ctx);
        var removedQueue = await CreateQueue(ctx);

        await SetQueuePolicy(ctx, createdQueue.Url, SqsPolicyForS3(createdQueue.Arn, bucket.Arn));
        await SetQueuePolicy(ctx, removedQueue.Url, SqsPolicyForS3(removedQueue.Arn, bucket.Arn));

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            QueueConfigurations =
            [
                QueueConfig(createdQueue.Arn, ["s3:ObjectCreated:*"], id: "created"),
                QueueConfig(removedQueue.Arn, ["s3:ObjectRemoved:*"], id: "removed"),
            ],
        });

        // Control 1: ObjectCreated must land on created_queue, and must not leak to removed_queue.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "route-me.txt", ContentBody = "x" });
        var createdMsgs = await WaitForS3Records(ctx, createdQueue.Url, expectedCount: 1, timeoutS: 90);
        var createdEventName = Str(Records(createdMsgs[0])[0], "eventName") ?? string.Empty;
        Ensure(
            createdEventName.StartsWith("ObjectCreated", StringComparison.Ordinal),
            $"expected an ObjectCreated* eventName on created_queue, got {Py.Repr(createdEventName)}");
        var leakedToRemoved = RealEventNames(await Drain(ctx, removedQueue.Url, windowS: 45));
        Ensure(
            leakedToRemoved.Count == 0,
            "ObjectCreated notification leaked to removed_queue -- routing is not isolated per " +
            $"destination, got {Py.Repr(leakedToRemoved)}");

        // Control 2: ObjectRemoved must land on removed_queue, and must not leak to created_queue.
        await s3.DeleteObjectAsync(bucket.Name, "route-me.txt");
        var removedMsgs = await WaitForS3Records(ctx, removedQueue.Url, expectedCount: 1, timeoutS: 90);
        var removedEventName = Str(Records(removedMsgs[0])[0], "eventName") ?? string.Empty;
        Ensure(
            removedEventName.StartsWith("ObjectRemoved", StringComparison.Ordinal),
            $"expected an ObjectRemoved* eventName on removed_queue, got {Py.Repr(removedEventName)}");
        var leakedToCreated = RealEventNames(await Drain(ctx, createdQueue.Url, windowS: 45));
        Ensure(
            leakedToCreated.Count == 0,
            "ObjectRemoved notification leaked to created_queue -- routing is not isolated per " +
            $"destination, got {Py.Repr(leakedToCreated)}");
    }

    /// <summary>
    /// Drain a queue over a bounded window, deleting every message seen, and return them all. The
    /// negative half of a routing-isolation check: meaningful only after a positive control.
    /// </summary>
    private static async Task<List<SqsMessage>> Drain(FidelityContext ctx, string queueUrl, double windowS = 45.0, int maxTotal = 10)
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
            foreach (var m in resp.Messages ?? [])
            {
                seen.Add(m);
                await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
            }
        }

        return seen;
    }

    /// <summary>eventName of every real (non s3:TestEvent) S3 record among raw SQS messages.</summary>
    private static List<string?> RealEventNames(List<SqsMessage> msgs)
    {
        var output = new List<string?>();
        foreach (var m in msgs)
        {
            var body = Body(m);
            if (IsS3TestEvent(body) || body is not JsonObject)
            {
                continue;
            }

            output.AddRange(Records(body).Select(r => Str(r, "eventName")));
        }

        return output;
    }
}
