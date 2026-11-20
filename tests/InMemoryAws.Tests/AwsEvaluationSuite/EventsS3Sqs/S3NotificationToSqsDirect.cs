// Translated from behavioral/events__s3__sqs/check_s3_notification_to_sqs_direct.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.S3QueueNotificationKit;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-notification-to-sqs-direct</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3NotificationToSqsDirect
{
    /// <summary>Bounded convergence for a delete to reach its terminal not-found answer.</summary>
    private const double GoneS = 120.0;

    /// <summary>S3 bucket notifications deliver ObjectCreated events directly to SQS.</summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-notification-to-sqs-direct")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx, tolerant: true);

        var notification = new List<QueueConfiguration> { QueueConfig(queue.Arn, ["s3:ObjectCreated:*"]) };
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, bucket.Arn));

        // The configuration is accepted once S3 may send to the queue (retried through propagation).
        await PutNotificationAfterPolicy(ctx, bucket.Name, notification);

        var key = $"{FidelityContext.UniqueName("obj-")}.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = key, ContentBody = "hi" });

        var messages = await WaitForS3Records(ctx, queue.Url, expectedCount: 1, timeoutS: 90);
        Ensure(messages.Count > 0, "expected S3 event notification in SQS queue");
        var body = messages[0];
        var records = Records(body);
        Ensure(records.Count > 0, $"malformed S3 notification body: {body?.ToJsonString()}");
        Ensure(
            Str(records[0]["s3"]!["bucket"], "name") == bucket.Name,
            $"expected bucket {Py.Repr(bucket.Name)}, got {Py.Repr(Str(records[0]["s3"]!["bucket"], "name"))}");
        Ensure(Str(Object(records[0]), "key") == key, $"expected key {Py.Repr(key)}, got {Py.Repr(Str(Object(records[0]), "key"))}");
        Ensure(Str(records[0], "eventSource") == "aws:s3", $"expected eventSource 'aws:s3', got {Py.Repr(Str(records[0], "eventSource"))}");
        Ensure(Str(records[0], "awsRegion") == ctx.Region, $"expected awsRegion {Py.Repr(ctx.Region)}, got {Py.Repr(Str(records[0], "awsRegion"))}");
        Ensure(
            (Str(records[0], "eventName") ?? string.Empty).StartsWith("ObjectCreated:", StringComparison.Ordinal),
            $"expected eventName 'ObjectCreated:*', got {Py.Repr(Str(records[0], "eventName"))}");

        // Tear down in reverse dependency order and prove each resource gone.
        await GoneBucket(ctx, bucket.Name, ["404", "NoSuchBucket", "NotFound"], GoneS);
        await GoneQueue(ctx, queue.Name, queue.Url, GoneS);
    }
}
