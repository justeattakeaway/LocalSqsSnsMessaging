// Translated from behavioral/s3/check_put_object_sha1_checksum_round_trip_and_bad_digest_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-sha1-checksum-round-trip-and-bad-digest-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectSha1ChecksumRoundTripAndBadDigestRejected
{
    private const string Check = "put-object-sha1-checksum-round-trip-and-bad-digest-rejected";
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("sim2real-sha1-checksum-payload");

    /// <summary>
    /// PutObject ChecksumAlgorithm=SHA1 returns the true SHA1 on HeadObject/GetObject with
    /// ChecksumMode=ENABLED; a wrong x-amz-checksum-sha1 on the same bytes is BadDigest with its exact
    /// message and stores nothing.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-sha1-checksum-round-trip-and-bad-digest-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        const string key = "sha1-obj";
        var expected = S3Fidelity.Sha1B64(Body);

        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Body), ChecksumAlgorithm = ChecksumAlgorithm.SHA1 });
        Ensure(put.ChecksumSHA1 == expected, $"PutObject ChecksumSHA1 {Py.Repr(put.ChecksumSHA1)} != recomputed SHA1 {Py.Repr(expected)}");

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumSHA1 == expected, $"HeadObject(ChecksumMode=ENABLED) ChecksumSHA1 {Py.Repr(head.ChecksumSHA1)} != {Py.Repr(expected)}");

        using (var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }))
        {
            Ensure(got.ChecksumSHA1 == expected, $"GetObject(ChecksumMode=ENABLED) ChecksumSHA1 {Py.Repr(got.ChecksumSHA1)} != {Py.Repr(expected)}");
        }

        var other = Encoding.ASCII.GetBytes("sim2real-different-bytes-for-the-mismatch");
        var wrong = S3Fidelity.Sha1B64(other);
        var exc = await S3Fidelity.ExpectError(
            () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "sha1-bad-digest", InputStream = new MemoryStream(Body), ChecksumSHA1 = wrong }),
            "the call succeeded; real AWS rejects it, so there is no error code");
        Ensure(exc.Code == "BadDigest", $"expected BadDigest, got {exc}");
        Ensure(exc.Message == "The SHA1 you specified did not match the calculated checksum.", $"unexpected BadDigest message: {Py.Repr(exc.Message)}");

        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects?.Select(o => o.Key).ToHashSet() ?? [];
        Ensure(listed.SetEquals([key]), $"only the accepted upload must land; bucket lists {Py.Repr(listed)}");
    }
}
