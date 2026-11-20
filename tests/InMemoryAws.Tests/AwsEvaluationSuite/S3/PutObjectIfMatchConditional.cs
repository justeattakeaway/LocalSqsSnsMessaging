// Translated from behavioral/s3/check_put_object_if_match_conditional.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-if-match-conditional</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectIfMatchConditional
{
    /// <summary>
    /// PutObject with If-Match succeeds only on the object's current ETag: the previous version's
    /// real ETag is refused with 412 PreconditionFailed and leaves the object unchanged, because
    /// accepting a stale If-Match silently loses data.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-if-match-conditional")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "if-match-obj.txt";

        var v1Etag = (await Put(s3, bucket, key, "v1-payload")).ETag;
        var v2Etag = (await Put(s3, bucket, key, "v2-payload")).ETag;
        Ensure(v2Etag != v1Etag, $"expected v2 to carry a new ETag, both are {Py.Repr(v1Etag)}");

        // v1's real ETag is stale once v2 lands: the write is refused and the object is untouched.
        var stale = await S3Fidelity.ExpectError(
            () => Put(s3, bucket, key, "should-not-land", v1Etag),
            "expected a stale If-Match PutObject to be rejected, but it SUCCEEDED");
        var staleErr = (stale.Code, stale.Message);
        var staleStatus = (int)stale.StatusCode;
        (string?, string?) expected = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
        Ensure(staleErr == expected, $"expected {S3Fidelity.Repr(expected)} for a stale If-Match, got {S3Fidelity.Repr(staleErr)}");
        Ensure(staleStatus == 412, $"expected HTTP 412 for a stale If-Match, got {staleStatus}");
        var unchanged = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(unchanged == "v2-payload", $"expected the stale If-Match PutObject to have NOT landed, body is {Py.Repr(unchanged)}");
        var headEtag = (await s3.GetObjectMetadataAsync(bucket, key)).ETag;
        Ensure(headEtag == v2Etag, $"HeadObject ETag {Py.Repr(headEtag)} != the pre-refusal ETag {Py.Repr(v2Etag)}");

        // The current ETag is accepted and actually updates the object.
        var updated = await Put(s3, bucket, key, "updated-payload", v2Etag);
        Ensure(updated.ETag != v2Etag, "expected a new ETag after the conditional update landed");

        var finalBody = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(finalBody == "updated-payload", "expected the correctly-conditioned PutObject to land");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, string body, string? ifMatch = null) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(Encoding.ASCII.GetBytes(body)),
            IfMatch = ifMatch,
        });
}
