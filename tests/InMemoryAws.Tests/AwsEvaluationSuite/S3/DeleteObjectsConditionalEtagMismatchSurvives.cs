// Translated from behavioral/s3/check_delete_objects_conditional_etag_mismatch_survives.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-objects-conditional-etag-mismatch-survives</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectsConditionalEtagMismatchSurvives
{
    private const string Check = "delete-objects-conditional-etag-mismatch-survives";
    private const string WrongEtag = "\"deadbeefdeadbeefdeadbeefdeadbeef\"";
    private const string PreconditionMsg = "At least one of the pre-conditions you specified did not hold";

    /// <summary>
    /// A batch DeleteObjects evaluates each entry's ETag independently: the matching entry lands in
    /// Deleted and is gone, the mismatching one lands in Errors with PreconditionFailed and its object
    /// survives untouched.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-objects-conditional-etag-mismatch-survives")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-delcond-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = Check }] });

        const string matchKey = "match.txt";
        const string mismatchKey = "mismatch.txt";
        var matchEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = matchKey, ContentBody = "delete-me" })).ETag;
        var mismatchEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = mismatchKey, ContentBody = "keep-me" })).ETag;

        // The .NET SDK raises DeleteObjectsException for a response with Errors; botocore returns it.
        DeleteObjectsResponse resp;
        try
        {
            resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = [new KeyVersion { Key = matchKey, ETag = matchEtag }, new KeyVersion { Key = mismatchKey, ETag = WrongEtag }],
            });
        }
        catch (DeleteObjectsException ex)
        {
            resp = ex.Response;
        }

        var deleted = (resp.DeletedObjects ?? []).Select(d => d.Key).ToList();
        Ensure(deleted.SequenceEqual([matchKey]), $"expected only {Py.Repr(matchKey)} deleted, got {Py.Repr(deleted)}");

        var errors = (resp.DeleteErrors ?? []).Select(e => (e.Key, e.Code, e.Message)).ToList();
        Ensure(
            errors.SequenceEqual([(mismatchKey, "PreconditionFailed", PreconditionMsg)]),
            $"expected [{{'Key': {Py.Repr(mismatchKey)}, 'Code': 'PreconditionFailed', 'Message': {Py.Repr(PreconditionMsg)}}}], got [{string.Join(", ", errors.Select(e => $"{{'Key': {Py.Repr(e.Key)}, 'Code': {Py.Repr(e.Code)}, 'Message': {Py.Repr(e.Message)}}}"))}]");

        var matchAbsent = await S3Fidelity.ClientError(() => s3.GetObjectMetadataAsync(bucket, matchKey), "expected a ClientError, but the call succeeded");
        Ensure(matchAbsent.Code == "404", $"the matching key must be gone, got ({Py.Repr(matchAbsent.Code)}, {Py.Repr(matchAbsent.Message)})");

        var survivor = await s3.GetObjectMetadataAsync(bucket, mismatchKey);
        Ensure(
            survivor.ETag == mismatchEtag,
            $"the mismatched key must survive unchanged; ETag {Py.Repr(survivor.ETag)} != {Py.Repr(mismatchEtag)}");
    }
}
