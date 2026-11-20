// Translated from behavioral/s3__sqs/check_notification_destination_validation_rejects_unresolvable_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.notification-destination-validation-rejects-unresolvable-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationDestinationValidationRejectsUnresolvableQueue
{
    /// <summary>
    /// PutBucketNotificationConfiguration validates destinations at call time: a real queue is
    /// accepted and gets an s3:TestEvent; a nonexistent queue ARN is rejected with InvalidArgument
    /// and the bucket keeps its prior configuration.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.notification-destination-validation-rejects-unresolvable-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-xsvc-bkt-", s3);

        var queueName = FidelityContext.UniqueName("sim2real-xsvc-q-");
        var queueUrl = (await sqs.CreateQueueAsync(queueName)).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });

        // IAM/SQS-policy propagation has no API to poll; retry the first use through it instead.
        var started = FidelityTime.StartNew();
        string? lastCode = null;
        while (started.Elapsed.TotalSeconds < 30.0)
        {
            var err = await FidelityContext.CaptureError(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
            {
                BucketName = bucket,
                QueueConfigurations = [new QueueConfiguration { Queue = queueArn, Events = ["s3:ObjectCreated:*"] }],
            }));
            if (err is null)
            {
                lastCode = null;
                break;
            }

            lastCode = err.Code;
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(lastCode is null, $"PutBucketNotificationConfiguration with a valid, policy-granting queue kept failing: {Py.Repr(lastCode)}");

        // --- Positive control: the valid destination fires an s3:TestEvent with the documented shape.
        var messages = await DrainMessages(sqs, queueUrl, timeoutS: 20.0);
        var testEvents = messages.OfType<JsonObject>().Where(m => (string?)m["Event"] == "s3:TestEvent").ToList();
        Ensure(
            testEvents.Count > 0,
            $"no s3:TestEvent arrived after a valid PutBucketNotificationConfiguration; saw [{string.Join(", ", messages.Select(m => m?.ToJsonString()))}]");
        var @event = testEvents[0];
        Ensure((string?)@event["Service"] == "Amazon S3", $"TestEvent Service was {Py.Repr((string?)@event["Service"])}");
        Ensure((string?)@event["Bucket"] == bucket, $"TestEvent Bucket was {Py.Repr((string?)@event["Bucket"])}, want {Py.Repr(bucket)}");
        Ensure(!string.IsNullOrEmpty((string?)@event["RequestId"]), $"TestEvent carried no RequestId: {@event.ToJsonString()}");
        Ensure(!string.IsNullOrEmpty((string?)@event["HostId"]), $"TestEvent carried no HostId: {@event.ToJsonString()}");
        Ensure(
            !@event.ContainsKey("Records"),
            $"TestEvent carried a Records key: {@event.ToJsonString()}; it is not an event-record envelope");

        // --- Break exactly one thing: an ARN naming a queue that was never created.
        var missingArn = queueArn + "-missing";
        var (code, message) = await Error(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = missingArn, Events = ["s3:ObjectCreated:*"] }],
        }));
        Ensure(
            code == "InvalidArgument",
            $"PutBucketNotificationConfiguration against a nonexistent queue ARN raised {Py.Repr(code)}, " +
            "want 'InvalidArgument'");
        Ensure(
            message == "Unable to validate the following destination configurations",
            $"the unresolvable-destination refusal message is {Py.Repr(message)}");

        // The rejected call must not have replaced the bucket's (valid) configuration with anything new.
        var current = await s3.GetBucketNotificationAsync(bucket);
        var currentArns = (current.QueueConfigurations ?? []).Select(q => q.Queue).ToList();
        Ensure(
            currentArns.SequenceEqual([queueArn]),
            $"after the rejected call, GetBucketNotificationConfiguration reports queues {Py.Repr(currentArns)}; " +
            $"want the untouched valid configuration [{Py.Repr(queueArn)}]");
    }

    /// <summary>Call fn(); return the (Error.Code, Error.Message) of its ClientError. A success raises.</summary>
    private static async Task<(string? Code, string Message)> Error(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn);
        if (err is null)
        {
            throw Fail("expected a ClientError, the call unexpectedly succeeded");
        }

        return (err.Code, err.Message ?? string.Empty);
    }

    /// <summary>Receive-and-delete every message that shows up within timeout_s; return their JSON bodies.</summary>
    private static async Task<List<JsonNode?>> DrainMessages(AmazonSQSClient sqs, string queueUrl, double timeoutS = 20.0)
    {
        var output = new List<JsonNode?>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            var messages = resp.Messages ?? [];
            if (messages.Count == 0)
            {
                if (output.Count > 0)
                {
                    break;
                }

                continue;
            }

            foreach (var msg in messages)
            {
                output.Add(JsonNode.Parse(msg.Body));
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
            }
        }

        return output;
    }
}
