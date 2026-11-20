// Translated from behavioral/s3/check_notification_invalid_destination_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.notification-invalid-destination-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationInvalidDestinationRejected
{
    private const string Check = "notification-invalid-destination-rejected";

    /// <summary>
    /// PutBucketNotificationConfiguration targeting an SQS queue that denies S3 SendMessage is
    /// rejected with InvalidArgument and applies nothing; a permitted twin queue is accepted, and
    /// granting the permission on the same queue makes the identical call succeed.
    /// </summary>
    [Test]
    [DisplayName("s3.notification-invalid-destination-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await NewBucket(ctx, s3);
        var sqs = ctx.Sqs;

        // ---- positive control: an otherwise-identical queue WITH the permission is accepted ----
        var okQueueUrl = await NewQueue(ctx, sqs, "sim2real-behav-ok-policy-queue-");
        var okQueueArn = await S3Fidelity.NotificationQueueArn(sqs, okQueueUrl);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = okQueueUrl,
            Attributes = new() { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(okQueueArn, bucket) },
        });
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = okQueueArn, Events = ["s3:ObjectCreated:*"] }],
        });
        var control = await s3.GetBucketNotificationAsync(bucket);
        var controlArns = QueueArns(control);
        Ensure(
            controlArns.SequenceEqual([okQueueArn]),
            $"positive control: a queue WITH S3-SendMessage permission must be accepted, got {Py.Repr(controlArns)}");
        // clear it back out so the negative leg's effect-absent read below starts from a clean slate
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket });
        var cleared = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            QueueArns(cleared).Count == 0,
            $"expected the notification config cleared before the negative leg, got {Py.Repr(QueueArns(cleared))}");

        // ---- break exactly one thing: an otherwise-identical queue with NO permission policy ----
        var queueUrl = await NewQueue(ctx, sqs, "sim2real-behav-no-policy-queue-");
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);
        // deliberately NOT setting a resource policy that allows s3.amazonaws.com
        var (code, message) = await ErrCode(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = queueArn, Events = ["s3:ObjectCreated:*"] }],
        }));
        Ensure(
            code == "InvalidArgument",
            $"expected InvalidArgument for a queue with no S3-SendMessage permission, got {Py.Repr(code)}");
        Ensure(message == "Unable to validate the following destination configurations", message ?? "None");
        // the bucket must have NO notification configuration as a result of the rejected call
        var got = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            QueueArns(got).Count == 0,
            $"a rejected PutBucketNotificationConfiguration must not partially apply, got {Py.Repr(QueueArns(got))}");

        // ---- restore: granting the SAME queue the missing permission makes the identical call succeed ----
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Queue = queueArn, Events = ["s3:ObjectCreated:*"] }],
        });
        var restored = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            QueueArns(restored).SequenceEqual([queueArn]),
            $"the identical call must succeed once the missing permission is granted, got {Py.Repr(QueueArns(restored))}");
    }

    private static List<string> QueueArns(GetBucketNotificationResponse response) =>
        (response.QueueConfigurations ?? []).Select(c => c.Queue).ToList();

    /// <summary>Create a uniquely-named, tagged bucket, register empty+delete teardown, return its name.</summary>
    private static async Task<string> NewBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        return name;
    }

    /// <summary>Create a uniquely-named, tagged queue, register delete teardown, return its URL.</summary>
    private static async Task<string> NewQueue(FidelityContext ctx, AmazonSQSClient sqs, string prefix)
    {
        var queueUrl = (await sqs.CreateQueueAsync(FidelityContext.UniqueName(prefix))).QueueUrl;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteQueue(sqs, queueUrl));
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = queueUrl, Tags = new() { ["sim2real"] = Check } });
        return queueUrl;
    }

    /// <summary>
    /// Call fn(); return botocore (Code, Message) on a ClientError. An unexpected success raises,
    /// so accepting refused input cannot pass as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrCode(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn);
        if (err is null)
        {
            throw Fail("expected the call to be rejected, but it SUCCEEDED and returned {}");
        }

        return (err.Code, err.Message);
    }
}
