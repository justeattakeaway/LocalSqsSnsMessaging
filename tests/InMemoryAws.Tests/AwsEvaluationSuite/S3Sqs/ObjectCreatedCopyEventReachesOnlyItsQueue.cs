// Translated from behavioral/s3__sqs/check_object_created_copy_event_reaches_only_its_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs.S3EventFamilyQueues;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.object-created-copy-event-reaches-only-its-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectCreatedCopyEventReachesOnlyItsQueue
{
    private const string Check = "object-created-copy-event-reaches-only-its-queue";
    private const string Subject = "created-copy";
    private const string Key = "fam/copy-dst.txt";

    /// <summary>
    /// CopyObject emits exactly `ObjectCreated:Copy` for the destination key, only to the Copy queue.
    /// Four single-event queues share one bucket; the subject queue's arrival is the barrier for
    /// the other three staying empty, and duplicate deliveries must carry the same eventName.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.object-created-copy-event-reaches-only-its-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateAclEnabledBucket(ctx, Check);
        var queues = await WireFamilyQueues(ctx, bucket);

        const string srcKey = "fam/copy-src.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = srcKey, ContentBody = "copy-subject" });

        var started = FidelityTime.StartNew();
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket.Name,
            SourceKey = srcKey,
            DestinationBucket = bucket.Name,
            DestinationKey = Key,
            TaggingDirective = TaggingDirective.REPLACE,
            TagSet = [new Tag { Key = "copied", Value = "yes" }],
        });
        var (records, others) = await WaitForRecords(ctx, queues[Subject].Url, Key, timeoutS: 120.0);
        Ensure(
            records.Count > 0,
            $"CopyObject on {Py.Repr(Key)} produced no notification on the s3:ObjectCreated:Copy queue within 120s; " +
            $"other traffic seen there: {Py.Repr(others.Take(2).ToList())}");
        var latencyS = started.Elapsed.TotalSeconds;
        var rec = records[0];
        Ensure(
            NameOf(rec) == "ObjectCreated:Copy",
            $"the CopyObject notification carries eventName {Py.Repr(NameOf(rec))}, expected exactly 'ObjectCreated:Copy'");
        Ensure(
            (string?)rec["s3"]?["bucket"]?["name"] == bucket.Name,
            $"the record names bucket {Py.Repr((string?)rec["s3"]?["bucket"]?["name"])}, expected {Py.Repr(bucket.Name)}");
        Ensure(
            (string?)rec["eventSource"] == "aws:s3",
            $"the record's eventSource is {Py.Repr((string?)rec["eventSource"])}, expected 'aws:s3'");

        var size = rec["s3"]?["object"]?["size"];
        Ensure(
            size is JsonValue v && v.TryGetValue<long>(out var n) && n == "copy-subject".Length,
            $"the Copy record reports size {size?.ToJsonString() ?? "None"}, expected {"copy-subject".Length}");
        var copied = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket.Name, Key));
        Ensure(copied == "copy-subject", $"the copied object holds {Py.Repr(copied)}");

        var holdS = Math.Min(Math.Max(3.0 * latencyS, 15.0), 120.0);
        await AssertFamilyQuiet(ctx, queues, subject: Subject, expectedName: "ObjectCreated:Copy", holdS: holdS, latencyS: latencyS);
    }
}
