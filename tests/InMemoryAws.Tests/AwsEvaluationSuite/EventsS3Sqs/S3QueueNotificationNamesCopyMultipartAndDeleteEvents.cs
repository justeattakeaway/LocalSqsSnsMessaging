// Translated from behavioral/events__s3__sqs/check_s3_queue_notification_names_copy_multipart_and_delete_events.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.S3QueueNotificationKit;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-queue-notification-names-copy-multipart-and-delete-events</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3QueueNotificationNamesCopyMultipartAndDeleteEvents
{
    /// <summary>Bounded convergence for a delete to reach its terminal not-found answer.</summary>
    private const double GoneS = 40.0;

    /// <summary>
    /// A queue subscribed to Copy, CompleteMultipartUpload and ObjectRemoved:Delete gets one record
    /// per such action, named for it, with the copy's MD5 eTag, the multipart '-1' eTag and sizes, and
    /// none for the plain PutObject, through a later control delete and a quiet window after it.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-queue-notification-names-copy-multipart-and-delete-events")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx, tagStrictly: true);
        var queue = await CreateQueue(ctx, tolerant: true);
        var notification = new List<QueueConfiguration>
        {
            QueueConfig(queue.Arn, ["s3:ObjectCreated:Copy", "s3:ObjectCreated:CompleteMultipartUpload", "s3:ObjectRemoved:Delete"]),
        };
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, bucket.Arn));
        // The configuration is accepted once S3 may send to the queue (retried through propagation).
        await PutNotificationAfterPolicy(ctx, bucket.Name, notification);

        var name = bucket.Name;
        var source = RandomNumberGenerator.GetBytes(500 + RandomNumberGenerator.GetInt32(500));
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = "src/original.bin", InputStream = new MemoryStream(source) });
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = name,
            SourceKey = "src/original.bin",
            DestinationBucket = name,
            DestinationKey = "dst/copy.bin",
        });
        var part = RandomNumberGenerator.GetBytes(1000 + RandomNumberGenerator.GetInt32(1000));
        var upload = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = name, Key = "mpu/assembled.bin" })).UploadId;
        ctx.OnCleanup(Tolerant(
            () => s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = name, Key = "mpu/assembled.bin", UploadId = upload }),
            "NoSuchUpload", "NoSuchBucket"));
        var etag = (await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = name,
            Key = "mpu/assembled.bin",
            UploadId = upload,
            PartNumber = 1,
            InputStream = new MemoryStream(part),
        })).ETag;
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = name,
            Key = "mpu/assembled.bin",
            UploadId = upload,
            PartETags = [new PartETag { PartNumber = 1, ETag = etag }],
        });
        await s3.DeleteObjectAsync(name, "src/original.bin");
        // Control: a delete sent last, so its delivery bounds the PutObject's absence.
        await s3.DeleteObjectAsync(name, "dst/copy.bin");

        var records = await ReceiveRecords(
            ctx,
            queue.Url,
            rs => rs.Any(r => Str(r, "eventName") == "ObjectRemoved:Delete" && Str(Object(r), "key") == "dst/copy.bin")
                && Distinct(rs).Count >= 4,
            "four records ending with ObjectRemoved:Delete of dst/copy.bin");
        var got = Distinct(records)
            .Select(r => Row(Str(r, "eventName"), Str(Object(r), "key"), Size(Object(r)), Str(Object(r), "eTag")))
            .Order(StringComparer.Ordinal)
            .ToList();
#pragma warning disable CA5351 // The check derives the expected eTags from MD5.
        var multipart = Convert.ToHexStringLower(MD5.HashData(MD5.HashData(part))) + "-1";
        // A removal record names the key only: it carries no size and no eTag.
        var want = new List<string>
        {
            Row("ObjectCreated:Copy", "dst/copy.bin", source.Length, Convert.ToHexStringLower(MD5.HashData(source))),
            Row("ObjectCreated:CompleteMultipartUpload", "mpu/assembled.bin", part.Length, multipart),
            Row("ObjectRemoved:Delete", "src/original.bin", null, null),
            Row("ObjectRemoved:Delete", "dst/copy.bin", null, null),
        }.Order(StringComparer.Ordinal).ToList();
#pragma warning restore CA5351
        Ensure(got.SequenceEqual(want), $"expected one record per subscribed action [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");

        // Tear down in reverse dependency order and prove each resource gone.
        await GoneBucket(ctx, bucket.Name, ["404"], GoneS);
        await GoneQueue(ctx, queue.Name, queue.Url, GoneS);
    }

    /// <summary>A record's (eventName, key, size, eTag) tuple, rendered as Python would print it.</summary>
    private static string Row(string? name, string? key, long? size, string? etag) =>
        $"({Py.Repr(name)}, {Py.Repr(key)}, {(size is { } s ? s.ToString(CultureInfo.InvariantCulture) : "None")}, {Py.Repr(etag)})";

    private static long? Size(JsonObject obj) => obj["size"] is JsonValue v && v.TryGetValue<long>(out var size) ? size : null;
}
