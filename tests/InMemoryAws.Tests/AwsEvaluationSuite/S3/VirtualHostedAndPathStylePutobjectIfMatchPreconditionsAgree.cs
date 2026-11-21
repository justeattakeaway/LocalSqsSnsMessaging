// Translated from behavioral/s3/check_virtual_hosted_and_path_style_putobject_if_match_preconditions_agree.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.virtual-hosted-and-path-style-putobject-if-match-preconditions-agree</summary>
[Category("AwsEvaluationSuite")]
public sealed class VirtualHostedAndPathStylePutobjectIfMatchPreconditionsAgree
{
    private const string Key = "cross-addr/conditional.bin";

    /// <summary>
    /// A PutObject If-Match naming an ETag a sibling addressing style already superseded is
    /// refused PreconditionFailed and leaves the object unchanged, while an If-Match naming the
    /// current ETag succeeds across the two addressing styles.
    /// </summary>
    [Test]
    [DisplayName("s3.virtual-hosted-and-path-style-putobject-if-match-preconditions-agree")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var vhost = ctx.S3Client(pathStyle: false, maxAttempts: 1);
        var pstyle = ctx.S3Client(pathStyle: true, maxAttempts: 1);

        var v1 = "first version, written virtual-hosted"u8.ToArray();
        var put1 = await vhost.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(v1) });
        var etag1 = put1.ETag;

        // positive control: a path-style client's If-Match naming the virtual-hosted client's ETag
        // is accepted, proving the two addressing styles see the same ETag for the same write.
        var v2 = "second version, written path-style under If-Match of the first"u8.ToArray();
        var put2 = await pstyle.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(v2), IfMatch = etag1 });
        var etag2 = put2.ETag;
        Ensure(etag2 != etag1, $"second PutObject ETag {Py.Repr(etag2)} did not change from {Py.Repr(etag1)}");
        using (var seen = await vhost.GetObjectAsync(bucket, Key))
        {
            Ensure((await S3Fidelity.ReadBody(seen)).SequenceEqual(v2), "virtual-hosted client did not observe the path-style write");
            Ensure(seen.ETag == etag2, $"virtual-hosted client saw ETag {Py.Repr(seen.ETag)}, expected {Py.Repr(etag2)}");
        }

        // negative: retrying If-Match against the now-stale first ETag is refused from the virtual-
        // hosted client, and the object is left exactly as the path-style write left it.
        var v3 = "third version, must never be written"u8.ToArray();
        (string? Code, string? Message) preconditionFailed = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
        var error = await FidelityContext.CaptureError(() => vhost.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(v3), IfMatch = etag1 }))
            ?? throw Fail("expected the call to be refused, but it succeeded");
        var got = (error.Code, error.Message ?? "");
        Ensure(got == preconditionFailed, $"stale If-Match PutObject: expected {S3Fidelity.Repr(preconditionFailed)}, got {S3Fidelity.Repr(got)}");

        using var unchanged = await pstyle.GetObjectAsync(bucket, Key);
        Ensure((await S3Fidelity.ReadBody(unchanged)).SequenceEqual(v2), "the object changed despite the refused PutObject");
        Ensure(unchanged.ETag == etag2, $"path-style client saw ETag {Py.Repr(unchanged.ETag)} after the refusal, expected {Py.Repr(etag2)} unchanged");
    }
}
