// Translated from behavioral/events__s3__sqs/check_s3_event_message_format_fields.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.S3QueueNotificationKit;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-event-message-format-fields</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3EventMessageFormatFields
{
    /// <summary>
    /// S3 event notification records carry eventName, eventTime, awsRegion, size, eTag (MD5 of the
    /// body) and the bucket ARN, all matching the PutObject call that triggered them.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-event-message-format-fields")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx);
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, bucket.Arn));
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            QueueConfigurations = [QueueConfig(queue.Arn, ["s3:ObjectCreated:Put"])],
        });

        var bodyBytes = "0123456789"u8.ToArray();
        var beforePut = FidelityTime.UtcNow;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "format-check.bin", InputStream = new MemoryStream(bodyBytes) });
#pragma warning disable CA5351 // The check compares the ETag with the body's MD5.
        var expectedEtag = Convert.ToHexStringLower(MD5.HashData(bodyBytes));
#pragma warning restore CA5351

        var messages = await WaitForS3Records(ctx, queue.Url, expectedCount: 1, timeoutS: 90);
        Ensure(messages.Count > 0, "expected S3 notification for format check");
        var record = Records(messages[0])[0];
        Ensure(Str(record, "eventName") == "ObjectCreated:Put", $"expected eventName 'ObjectCreated:Put', got {Py.Repr(Str(record, "eventName"))}");
        Ensure(Str(record, "eventSource") == "aws:s3", $"expected eventSource 'aws:s3', got {Py.Repr(Str(record, "eventSource"))}");
        var eventTime = DateTimeOffset.ParseExact(
            Str(record, "eventTime")!, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var now = FidelityTime.UtcNow;
        Ensure(
            beforePut - TimeSpan.FromMinutes(1) <= eventTime && eventTime <= now + TimeSpan.FromMinutes(1),
            $"eventTime {Py.Repr(Str(record, "eventTime"))} is not within a minute of the PutObject call " +
            $"(issued at {beforePut:O}, observed at {now:O})");
        Ensure(Str(record, "awsRegion") == ctx.Region, $"expected awsRegion {Py.Repr(ctx.Region)}, got {Py.Repr(Str(record, "awsRegion"))}");

        var s3Data = record["s3"]!.AsObject();
        Ensure(Str(s3Data["bucket"], "name") == bucket.Name, $"expected bucket name {Py.Repr(bucket.Name)}, got {Py.Repr(Str(s3Data["bucket"], "name"))}");
        Ensure(Str(s3Data["bucket"], "arn") == bucket.Arn, $"expected bucket arn {Py.Repr(bucket.Arn)}, got {Py.Repr(Str(s3Data["bucket"], "arn"))}");
        Ensure(Str(s3Data["object"], "key") == "format-check.bin", $"expected object key 'format-check.bin', got {Py.Repr(Str(s3Data["object"], "key"))}");
        var size = s3Data["object"]!["size"];
        Ensure(
            size is JsonValue sv && long.Parse(sv.ToString(), CultureInfo.InvariantCulture) == bodyBytes.Length,
            $"expected object size {bodyBytes.Length}, got {size?.ToJsonString()}");
        Ensure(
            Str(s3Data["object"], "eTag") == expectedEtag,
            $"expected eTag {Py.Repr(expectedEtag)} (MD5 of the {bodyBytes.Length}-byte body), got {Py.Repr(Str(s3Data["object"], "eTag"))}");
    }
}
