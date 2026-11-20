// Translated from behavioral/s3/check_notification_to_sqs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.notification-to-sqs</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationToSqs
{
    /// <summary>PutObject fires an s3:ObjectCreated event to a configured SQS queue.</summary>
    [Test]
    [DisplayName("s3.notification-to-sqs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-", s3);
        var sqs = ctx.Sqs;
        var queueUrl = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-q-"))).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));

        // Get queue ARN
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);

        // Set queue policy to allow S3 to send messages
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });

        // Configure bucket notification
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = queueArn, Events = ["s3:ObjectCreated:*"] }],
        });

        // Drain the S3 test event that's sent on configuration
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            var messages = resp.Messages ?? [];
            foreach (var msg in messages)
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
            }

            // Check if we got the test event
            if (messages.Exists(msg => (string?)JsonNode.Parse(msg.Body)?["Event"] == "s3:TestEvent"))
            {
                break;
            }
        }

        // Put an object to trigger the real notification
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "notify-test.txt", ContentBody = "trigger-event" });

        // Poll SQS for the ObjectCreated event
        JsonObject? received = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                // Skip any lingering test events
                if ((string?)body["Event"] == "s3:TestEvent")
                {
                    continue;
                }

                if (body.ContainsKey("Records"))
                {
                    received = body;
                    break;
                }
            }

            if (received is not null)
            {
                break;
            }
        }

        Ensure(received is not null, "No S3 event notification received within 30s");
        var records = received["Records"]!.AsArray();
        Ensure(records.Count >= 1, $"expected at least one record, got {records.ToJsonString()}");
        var record = records[0]!;
        Ensure((string?)record["eventSource"] == "aws:s3", $"expected eventSource aws:s3, got {Py.Repr((string?)record["eventSource"])}");
        Ensure(((string?)record["eventName"] ?? string.Empty).Contains("ObjectCreated", StringComparison.Ordinal), $"expected an ObjectCreated event, got {Py.Repr((string?)record["eventName"])}");
        Ensure(
            (string?)record["s3"]?["bucket"]?["name"] == bucket,
            $"expected bucket {Py.Repr(bucket)}, got {Py.Repr((string?)record["s3"]?["bucket"]?["name"])}");
        Ensure(
            (string?)record["s3"]?["object"]?["key"] == "notify-test.txt",
            $"expected key 'notify-test.txt', got {Py.Repr((string?)record["s3"]?["object"]?["key"])}");
    }
}
