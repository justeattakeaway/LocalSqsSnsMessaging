// Translated from behavioral/s3/check_s3_conditional_precedence_and_lifecycle_headers_copy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-conditional-precedence-and-lifecycle-headers-copy</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ConditionalPrecedenceAndLifecycleHeadersCopy
{
    private const string Body = "s3cond-copy-precedence-source";

    /// <summary>
    /// CopyObject resolves paired x-amz-copy-source-if-* headers by S3's precedence rules (a true IfMatch
    /// overrides a false IfUnmodifiedSince, IfNoneMatch decides over IfModifiedSince), and every failed
    /// precondition is 412 PreconditionFailed with no destination written.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-conditional-precedence-and-lifecycle-headers-copy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3cond-copy-");
        const string src = "source/object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = src, ContentBody = Body });
        var head = await s3.GetObjectMetadataAsync(bucket, src);
        var etag = head.ETag;
        var lastModified = S3Fidelity.Utc(head.LastModified);
        var before = lastModified - TimeSpan.FromSeconds(1);
        const string otherEtag = "\"00000000000000000000000000000000\"";
        Ensure(otherEtag != etag, $"the non-matching ETag collided with the live one {Py.Repr(etag)}");

        // Return (status, error_code_or_None, error_message_or_None) and the effect: whether
        // dest exists afterwards.
        async Task<((int, string?, string?) Outcome, bool Present)> Copy(string dest, Action<CopyObjectRequest> kw)
        {
            var request = new CopyObjectRequest { SourceBucket = bucket, SourceKey = src, DestinationBucket = bucket, DestinationKey = dest };
            kw(request);
            var (response, error) = await FidelityContext.Capture(() => s3.CopyObjectAsync(request));
            (int, string?, string?) outcome;
            if (error is null)
            {
                outcome = ((int)response!.HttpStatusCode, null, null);
            }
            else
            {
                var (code, message) = S3Fidelity.Botocore(error);
                outcome = ((int)error.StatusCode, code, message);
            }

            return (outcome, await Exists(s3, bucket, dest));
        }

        async Task Accepted(string label, string dest, Action<CopyObjectRequest> kw)
        {
            var (outcome, present) = await Copy(dest, kw);
            Ensure(outcome == (200, null, null), $"{label}: expected the copy to succeed (200), got {Py.Repr(outcome)}");
            Ensure(present, $"{label}: the copy returned 200 but {Py.Repr(dest)} does not exist");
            var body = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, dest));
            Ensure(body == Body, $"{label}: copied bytes differ from the source, got {Py.Repr(body)}");
        }

        async Task Rejected(string label, string dest, Action<CopyObjectRequest> kw)
        {
            var (outcome, present) = await Copy(dest, kw);
            Ensure(
                outcome == (412, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold"),
                $"{label}: expected 412 PreconditionFailed, got {Py.Repr(outcome)}");
            Ensure(!present, $"{label}: the copy was refused yet {Py.Repr(dest)} was written");
        }

        // --- Controls: each header alone.
        await Rejected("CopySourceIfUnmodifiedSince=LM-1s alone", "dest/ius-alone", r => r.UnmodifiedSinceDate = before);
        await Accepted("CopySourceIfMatch=<etag> alone", "dest/im-alone", r => r.ETagToMatch = etag);
        await Accepted("CopySourceIfModifiedSince=LM-1s alone", "dest/ims-before-alone", r => r.ModifiedSinceDate = before);
        await Rejected("CopySourceIfModifiedSince=LM alone (not 304 as on GET)", "dest/ims-at-alone", r => r.ModifiedSinceDate = lastModified);
        await Rejected("CopySourceIfNoneMatch=<etag> alone (not 304 as on GET)", "dest/inm-alone", r => r.ETagToNotMatch = etag);

        // --- Pair 1: if-match true overrides if-unmodified-since false.
        await Accepted("CopySourceIfMatch=<etag> + CopySourceIfUnmodifiedSince=LM-1s", "dest/pair1", r =>
        {
            r.ETagToMatch = etag;
            r.UnmodifiedSinceDate = before;
        });

        // --- Pair 2: if-none-match false beats if-modified-since true.
        await Rejected("CopySourceIfNoneMatch=<etag> + CopySourceIfModifiedSince=LM-1s", "dest/pair2", r =>
        {
            r.ETagToNotMatch = etag;
            r.ModifiedSinceDate = before;
        });

        // --- Pair 3: if-none-match true overrides if-modified-since false.
        await Accepted("CopySourceIfNoneMatch=<other> + CopySourceIfModifiedSince=LM", "dest/pair3", r =>
        {
            r.ETagToNotMatch = otherEtag;
            r.ModifiedSinceDate = lastModified;
        });

        // --- Pair 4: the override is one-way; a false if-match still rejects.
        await Rejected("CopySourceIfMatch=<other> + CopySourceIfUnmodifiedSince=<future>", "dest/pair4", r =>
        {
            r.ETagToMatch = otherEtag;
            r.UnmodifiedSinceDate = lastModified + TimeSpan.FromDays(1);
        });

        // --- The source is untouched, and exactly the accepted destinations exist.
        var final = await s3.GetObjectMetadataAsync(bucket, src);
        Ensure(
            (final.ETag, S3Fidelity.Utc(final.LastModified)) == (etag, lastModified),
            $"conditional copies must not mutate the source; went from {Py.Repr((etag, lastModified))} "
            + $"to {Py.Repr((final.ETag, S3Fidelity.Utc(final.LastModified)))}");
        var keys = (await S3CoreFidelity.ListAllKeys(s3, bucket, "dest/")).ToHashSet(StringComparer.Ordinal);
        string[] expected = ["dest/im-alone", "dest/ims-before-alone", "dest/pair1", "dest/pair3"];
        Ensure(
            keys.SetEquals(expected),
            $"destinations present {Py.Repr(keys.Order(StringComparer.Ordinal).ToList())}, expected {Py.Repr(expected.ToList())}");
    }

    /// <summary>True if HEAD finds the key, False on a 404; any other error propagates.</summary>
    private static async Task<bool> Exists(IAmazonS3 s3, string bucket, string key)
    {
        var error = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        if (error is null)
        {
            return true;
        }

        return error.StatusCode == System.Net.HttpStatusCode.NotFound ? false : throw error.Exception;
    }
}
