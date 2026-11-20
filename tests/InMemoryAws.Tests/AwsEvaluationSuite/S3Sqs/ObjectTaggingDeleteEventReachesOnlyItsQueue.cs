// Translated from behavioral/s3__sqs/check_object_tagging_delete_event_reaches_only_its_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs.S3EventFamilyQueues;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.object-tagging-delete-event-reaches-only-its-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectTaggingDeleteEventReachesOnlyItsQueue
{
    private const string Check = "object-tagging-delete-event-reaches-only-its-queue";
    private const string Subject = "tagging-delete";
    private const string Key = "fam/untagged.txt";

    /// <summary>
    /// DeleteObjectTagging emits exactly `ObjectTagging:Delete`, and only to the queue subscribed to it.
    /// Four single-event queues share one bucket; the subject queue's arrival is the barrier for
    /// the other three staying empty, and duplicate deliveries must carry the same eventName.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.object-tagging-delete-event-reaches-only-its-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateAclEnabledBucket(ctx, Check);
        var queues = await WireFamilyQueues(ctx, bucket);

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket.Name,
            Key = Key,
            ContentBody = "tagging-delete-subject",
            TagSet = [new Tag { Key = "stage", Value = "draft" }],
        });
        var before = await TagSet(s3, bucket.Name, Key);
        Ensure(
            before == "[{'Key': 'stage', 'Value': 'draft'}]",
            $"inline tagging on PutObject stored {before}, expected the one stage=draft tag");

        var started = FidelityTime.StartNew();
        await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket.Name, Key = Key });
        var (records, others) = await WaitForRecords(ctx, queues[Subject].Url, Key, timeoutS: 120.0);
        Ensure(
            records.Count > 0,
            $"DeleteObjectTagging on {Py.Repr(Key)} produced no notification on the s3:ObjectTagging:Delete queue within 120s; " +
            $"other traffic seen there: {Py.Repr(others.Take(2).ToList())}");
        var latencyS = started.Elapsed.TotalSeconds;
        var rec = records[0];
        Ensure(
            NameOf(rec) == "ObjectTagging:Delete",
            $"the DeleteObjectTagging notification carries eventName {Py.Repr(NameOf(rec))}, expected exactly 'ObjectTagging:Delete'");
        Ensure(
            (string?)rec["s3"]?["bucket"]?["name"] == bucket.Name,
            $"the record names bucket {Py.Repr((string?)rec["s3"]?["bucket"]?["name"])}, expected {Py.Repr(bucket.Name)}");
        Ensure(
            (string?)rec["eventSource"] == "aws:s3",
            $"the record's eventSource is {Py.Repr((string?)rec["eventSource"])}, expected 'aws:s3'");

        var after = await TagSet(s3, bucket.Name, Key);
        Ensure(after == "[]", $"GetObjectTagging after DeleteObjectTagging returned {after}, expected []");

        var holdS = Math.Min(Math.Max(3.0 * latencyS, 15.0), 120.0);
        await AssertFamilyQuiet(ctx, queues, subject: Subject, expectedName: "ObjectTagging:Delete", holdS: holdS, latencyS: latencyS);
    }
}
