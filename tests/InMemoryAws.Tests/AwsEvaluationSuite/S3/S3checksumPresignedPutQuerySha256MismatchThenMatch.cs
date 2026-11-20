// Translated from behavioral/s3/check_s3checksum_presigned_put_query_sha256_mismatch_then_match.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-presigned-put-query-sha256-mismatch-then-match</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumPresignedPutQuerySha256MismatchThenMatch
{
    /// <summary>
    /// A presigned PUT URL signing ChecksumSHA256 in its query refuses a body that does not match it with
    /// BadDigest and stores nothing; the body it was signed for is accepted and read back under that same
    /// SHA256.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-presigned-put-query-sha256-mismatch-then-match")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string key = "presigned/sha256.bin";
        var checksumA = S3Fidelity.Sha256B64(BodyA);
        var headers = new Dictionary<string, string> { ["x-amz-checksum-sha256"] = checksumA, ["x-amz-sdk-checksum-algorithm"] = "SHA256" };
        var url = S3Fidelity.Presign(ctx, "PUT", bucket, key, 900, signedHeaders: headers);
        var signed = S3Fidelity.SignedHeaderNames(url);
        Ensure(
            signed.IsSupersetOf(["x-amz-checksum-sha256", "x-amz-sdk-checksum-algorithm"]),
            $"X-Amz-SignedHeaders is {Py.Repr(signed.Order(StringComparer.Ordinal).ToList())}; expected it to bind both " +
            "x-amz-checksum-sha256 and x-amz-sdk-checksum-algorithm into the signature");

        // Beat 1 — the same signed URL, a body the signed checksum does not describe.
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, headers, BodyB);
        Ensure(
            status == 400,
            $"PUTting a body other than the one ChecksumSHA256={Py.Repr(checksumA)} was signed for returned " +
            $"{status}, expected 400: {S3Fidelity.Head(body, 300)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("BadDigest", "The SHA256 you specified did not match the calculated checksum."),
            $"the mismatched-body refusal carries no BadDigest error: {S3Fidelity.Head(body, 300)}");
        Ensure(
            (await S3Fidelity.HeadError(s3, bucket, key)).Code == "404",
            $"{Py.Repr(key)} exists after the presigned PUT whose body did not match its signed SHA256");

        // Beat 2 — the identical URL, the body its checksum actually describes.
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, headers, BodyA);
        Ensure(
            status == 200,
            $"the same presigned URL, PUT with the body its ChecksumSHA256 was signed for, returned " +
            $"{status}: {S3Fidelity.Head(body, 300)}");
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumSHA256 == checksumA, $"HeadObject reports ChecksumSHA256={Py.Repr(head.ChecksumSHA256)}, expected {Py.Repr(checksumA)}");
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        var gotBody = await S3Fidelity.ReadBody(got);
        Ensure(gotBody.SequenceEqual(BodyA), $"stored body is {S3Fidelity.Head(gotBody)}, expected {S3Fidelity.Head(BodyA)}");
        Ensure(got.ChecksumSHA256 == checksumA, $"GetObject reports ChecksumSHA256={Py.Repr(got.ChecksumSHA256)}, expected {Py.Repr(checksumA)}");
    }

    private static readonly byte[] BodyA = S3Fidelity.RepeatText("presigned-checksum-query-sha256-body-a-", 4);
    private static readonly byte[] BodyB = S3Fidelity.RepeatText("presigned-checksum-query-sha256-body-b-different-bytes-", 4);
}
