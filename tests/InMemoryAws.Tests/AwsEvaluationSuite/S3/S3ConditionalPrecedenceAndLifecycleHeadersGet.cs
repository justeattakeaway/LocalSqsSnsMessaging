// Translated from behavioral/s3/check_s3_conditional_precedence_and_lifecycle_headers_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-conditional-precedence-and-lifecycle-headers-get</summary>
/// <remarks>The body bytes are compared as their (ASCII) text, so the outcome tuples compare as the Python's do.</remarks>
[Category("AwsEvaluationSuite")]
public sealed class S3ConditionalPrecedenceAndLifecycleHeadersGet
{
    private const string Body = "s3cond-get-precedence-payload";
    private const string PreconditionFailed = "At least one of the pre-conditions you specified did not hold";

    /// <summary>
    /// GetObject resolves paired conditional headers by S3's precedence rules: a holding If-Match
    /// overrides a failing If-Unmodified-Since, and If-None-Match decides over If-Modified-Since in both
    /// directions (304 or the body), each compared with the header sent alone.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-conditional-precedence-and-lifecycle-headers-get")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3cond-get-");
        const string key = "precedence/object.txt";

        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = Body });
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var etag = head.ETag;
        var lastModified = S3Fidelity.Utc(head.LastModified);
        Ensure(etag == put.ETag, $"HEAD ETag {Py.Repr(etag)} differs from PutObject's {Py.Repr(put.ETag)}");
        var before = lastModified - TimeSpan.FromSeconds(1);
        const string otherEtag = "\"00000000000000000000000000000000\"";
        Ensure(otherEtag != etag, $"the non-matching ETag collided with the live one {Py.Repr(etag)}");

        Task<(int, string?, string?)> Get(Action<GetObjectRequest> kw)
        {
            var request = new GetObjectRequest { BucketName = bucket, Key = key };
            kw(request);
            return Outcome(() => s3.GetObjectAsync(request));
        }

        (int, string?, string?) ok = (200, null, Body);
        (int, string?, string?) notModified = (304, "304", "Not Modified");
        (int, string?, string?) failed = (412, "PreconditionFailed", PreconditionFailed);

        // --- Controls: each header ALONE, so each pair below has a measured baseline.
        var iusAlone = await Get(r => r.UnmodifiedSinceDate = before);
        Ensure(
            iusAlone == failed,
            "If-Unmodified-Since=LastModified-1s alone must fail 412 PreconditionFailed (the object "
            + $"WAS modified after that instant); got {Py.Repr(iusAlone)}");
        var imAlone = await Get(r => r.EtagToMatch = etag);
        Ensure(imAlone == ok, $"If-Match=<live etag> alone must return the body, got {Py.Repr(imAlone)}");

        var imsBeforeAlone = await Get(r => r.ModifiedSinceDate = before);
        Ensure(
            imsBeforeAlone == ok,
            "If-Modified-Since=LastModified-1s alone must return the body (the object WAS modified "
            + $"after that instant); got {Py.Repr(imsBeforeAlone)}");
        var imsAtAlone = await Get(r => r.ModifiedSinceDate = lastModified);
        Ensure(imsAtAlone == notModified, $"If-Modified-Since=LastModified alone must be 304 Not Modified, got {Py.Repr(imsAtAlone)}");
        var inmAlone = await Get(r => r.EtagToNotMatch = etag);
        Ensure(inmAlone == notModified, $"If-None-Match=<live etag> alone must be 304 Not Modified, got {Py.Repr(inmAlone)}");

        // --- Pair 1: If-Match true overrides If-Unmodified-Since false -> 200 WITH the body.
        var pair1 = await Get(r =>
        {
            r.EtagToMatch = etag;
            r.UnmodifiedSinceDate = before;
        });
        Ensure(
            pair1 == ok,
            "If-Match=<live etag> + If-Unmodified-Since=LastModified-1s must return 200 with the body "
            + $"(If-Match satisfied means If-Unmodified-Since is ignored); got {Py.Repr(pair1)}");

        // --- Pair 2: If-None-Match false beats If-Modified-Since true -> 304, not the body.
        var pair2 = await Get(r =>
        {
            r.EtagToNotMatch = etag;
            r.ModifiedSinceDate = before;
        });
        Ensure(
            pair2 == notModified,
            "If-None-Match=<live etag> + If-Modified-Since=LastModified-1s must be 304 with no body "
            + $"(If-None-Match decides), even though If-Modified-Since alone returned the body; got {Py.Repr(pair2)}");

        // --- Pair 3: If-None-Match true overrides If-Modified-Since false -> 200 with the body.
        var pair3 = await Get(r =>
        {
            r.EtagToNotMatch = otherEtag;
            r.ModifiedSinceDate = lastModified;
        });
        Ensure(
            pair3 == ok,
            "If-None-Match=<other etag> + If-Modified-Since=LastModified must return the body "
            + "(If-None-Match satisfied means If-Modified-Since is ignored), even though "
            + $"If-Modified-Since alone was 304; got {Py.Repr(pair3)}");

        // --- Pair 4 (the override is one-way): If-Match FALSE still fails even with a true
        // If-Unmodified-Since, so pair 1 is precedence and not "any true condition passes".
        var futureIus = lastModified + TimeSpan.FromDays(1);
        var pair4 = await Get(r =>
        {
            r.EtagToMatch = otherEtag;
            r.UnmodifiedSinceDate = futureIus;
        });
        Ensure(
            pair4 == failed,
            "If-Match=<other etag> + If-Unmodified-Since=<future> must fail 412 PreconditionFailed; "
            + $"got {Py.Repr(pair4)}");

        // --- The object is untouched by all of the above.
        var final = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            (final.ETag, S3Fidelity.Utc(final.LastModified)) == (etag, lastModified),
            "conditional reads must not mutate the object; ETag/LastModified went from "
            + $"{Py.Repr((etag, lastModified))} to {Py.Repr((final.ETag, S3Fidelity.Utc(final.LastModified)))}");
    }

    /// <summary>
    /// Call fn(); return (http_status, error_code_or_None, body_or_error_message).
    /// A success returns its real 2xx status and the body bytes, so an unexpected success can never
    /// be mistaken for an error code.
    /// </summary>
    private static async Task<(int, string?, string?)> Outcome(Func<Task<GetObjectResponse>> fn)
    {
        var (response, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var (code, message) = S3Fidelity.Botocore(error);
            return ((int)error.StatusCode, code, message);
        }

        using (response)
        {
            return ((int)response!.HttpStatusCode, null, Encoding.UTF8.GetString(await S3Fidelity.ReadBody(response)));
        }
    }
}
