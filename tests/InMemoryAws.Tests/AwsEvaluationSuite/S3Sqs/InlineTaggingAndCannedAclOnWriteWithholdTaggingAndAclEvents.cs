// Translated from behavioral/s3__sqs/check_inline_tagging_and_canned_acl_on_write_withhold_tagging_and_acl_events.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs.S3EventFamilyQueues;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.inline-tagging-and-canned-acl-on-write-withhold-tagging-and-acl-events</summary>
[Category("AwsEvaluationSuite")]
public sealed class InlineTaggingAndCannedAclOnWriteWithholdTaggingAndAclEvents
{
    private const string Check = "inline-tagging-and-canned-acl-on-write-withhold-tagging-and-acl-events";
    private const string Key = "fam/written-with-tags-and-acl.txt";
    private const string CopyKey = "fam/copied-with-tags-and-acl.txt";

    /// <summary>
    /// PutObject/CopyObject carrying tags and a canned ACL emit only ObjectCreated:Put/Copy, never
    /// ObjectTagging:Put or ObjectAcl:Put; standalone tagging and ACL writes then reach those same
    /// queues, proving they were wired.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.inline-tagging-and-canned-acl-on-write-withhold-tagging-and-acl-events")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateAclEnabledBucket(ctx, Check);
        var control = await CreateQueue(ctx);
        await AllowS3Send(ctx, control, bucket.Arn);
        var queues = await WireFamilyQueues(ctx, bucket, extra:
        [
            new QueueConfiguration { Id = "control-put", Queue = control.Arn, Events = [new EventType("s3:ObjectCreated:Put")] },
        ]);

        var started = FidelityTime.StartNew();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket.Name,
            Key = Key,
            ContentBody = "inline-meta",
            TagSet = [new Tag { Key = "stage", Value = "draft" }],
            CannedACL = S3CannedACL.Private,
        });
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket.Name,
            SourceKey = Key,
            DestinationBucket = bucket.Name,
            DestinationKey = CopyKey,
            TaggingDirective = TaggingDirective.REPLACE,
            TagSet = [new Tag { Key = "stage", Value = "copied" }],
            CannedACL = S3CannedACL.Private,
        });

        var (putRecs, others) = await WaitForRecords(ctx, control.Url, Key, timeoutS: 120.0);
        Ensure(
            putRecs.Count > 0,
            $"the control s3:ObjectCreated:Put queue received nothing for {Py.Repr(Key)} within 120s, so no " +
            $"absence below would mean anything; other traffic: {Py.Repr(others.Take(2).ToList())}");
        Ensure(
            NameOf(putRecs[0]) == "ObjectCreated:Put",
            $"the control record carries eventName {Py.Repr(NameOf(putRecs[0]))}, expected 'ObjectCreated:Put'");
        var (copyRecs, copyOthers) = await WaitForRecords(ctx, queues["created-copy"].Url, CopyKey, timeoutS: 120.0);
        Ensure(
            copyRecs.Count > 0,
            $"the s3:ObjectCreated:Copy queue received nothing for {Py.Repr(CopyKey)} within 120s; " +
            $"other traffic: {Py.Repr(copyOthers.Take(2).ToList())}");
        Ensure(
            NameOf(copyRecs[0]) == "ObjectCreated:Copy",
            $"the copy record carries eventName {Py.Repr(NameOf(copyRecs[0]))}, expected 'ObjectCreated:Copy'");
        var latencyS = started.Elapsed.TotalSeconds;

        var stored = await TagSet(s3, bucket.Name, CopyKey);
        Ensure(
            stored == "[{'Key': 'stage', 'Value': 'copied'}]",
            $"the copy's tags are {stored}; the REPLACE directive must have applied, or the absence " +
            "below proves nothing about a tag write");

        // The control/copy arrivals bound how long the tagging/ACL queues must stay silent.
        var holdS = Math.Min(Math.Max(3.0 * latencyS, 15.0), 120.0);
        await AssertFamilyQuiet(ctx, queues, subject: "created-copy", expectedName: "ObjectCreated:Copy", holdS: holdS, latencyS: latencyS);

        // Close the loop: the same queues DO receive a standalone tagging write and ACL write.
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket.Name,
            Key = Key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "stage", Value = "final" }] },
        });
        await s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket.Name, Key = Key, ACL = S3CannedACL.Private });
        foreach (var (label, name) in new[] { ("tagging-put", "ObjectTagging:Put"), ("acl-put", "ObjectAcl:Put") })
        {
            var (recs, rest) = await WaitForRecords(ctx, queues[label].Url, Key, timeoutS: 120.0);
            Ensure(
                recs.Count > 0,
                $"the {label} queue received nothing for a standalone write within 120s, so its empty " +
                $"hold above only proved it was never wired; other traffic: {Py.Repr(rest.Take(2).ToList())}");
            Ensure(
                NameOf(recs[0]) == name,
                $"the standalone {label} write carries eventName {Py.Repr(NameOf(recs[0]))}, expected {Py.Repr(name)}");
        }
    }
}
