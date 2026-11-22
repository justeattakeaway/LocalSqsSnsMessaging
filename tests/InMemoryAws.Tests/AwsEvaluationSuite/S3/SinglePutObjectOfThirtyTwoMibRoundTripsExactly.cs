// Translated from behavioral/s3/check_single_put_object_of_thirty_two_mib_round_trips_exactly.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.single-put-object-of-thirty-two-mib-round-trips-exactly</summary>
[Category("AwsEvaluationSuite")]
public sealed class SinglePutObjectOfThirtyTwoMibRoundTripsExactly
{
    private const string Check = "single-put-object-of-thirty-two-mib-round-trips-exactly";

    /// <summary>33554432 bytes, well under the 5 GiB single-PUT maximum.</summary>
    private const int Size = 32 * 1024 * 1024;

    /// <summary>
    /// A single 32 MiB PutObject succeeds; HeadObject and GetObject report the same size
    /// and ETag, and GetObject returns the exact bytes written.
    /// </summary>
    [Test]
    [DisplayName("s3.single-put-object-of-thirty-two-mib-round-trips-exactly")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-bigobj-");
        const string key = "lifecycle/large-object.bin";
        var body = S3Fidelity.RandomBytes(Size);
        var digest = S3Fidelity.Md5Hex(body);

        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
        Ensure(put.ETag == $"\"{digest}\"", $"PutObject ETag {Py.Repr(put.ETag)} must be the quoted MD5 {Py.Repr(digest)} of the body");

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ContentLength == Size, $"HeadObject ContentLength {head.ContentLength} != {Size}");
        Ensure(head.ETag == put.ETag, $"HeadObject ETag {Py.Repr(head.ETag)} != the ETag PutObject returned {Py.Repr(put.ETag)}");

        using var got = await s3.GetObjectAsync(bucket, key);
        Ensure(got.ContentLength == Size, $"GetObject ContentLength {got.ContentLength} != {Size}");
        var data = await S3Fidelity.ReadBody(got);
        Ensure(data.Length == Size, $"GetObject returned {data.Length} bytes, expected {Size}");
        Ensure(data.AsSpan().SequenceEqual(body), "GetObject returned bytes that differ from the body that was put");
    }
}
