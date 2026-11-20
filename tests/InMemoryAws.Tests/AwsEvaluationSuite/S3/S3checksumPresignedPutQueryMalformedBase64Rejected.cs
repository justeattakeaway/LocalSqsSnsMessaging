// Translated from behavioral/s3/check_s3checksum_presigned_put_query_malformed_base64_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-presigned-put-query-malformed-base64-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumPresignedPutQueryMalformedBase64Rejected
{
    /// <summary>
    /// A presigned PUT URL whose signed ChecksumCRC32 is not valid base64 is refused InvalidRequest and
    /// stores nothing; a URL signed with a well-formed checksum for the same body succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-presigned-put-query-malformed-base64-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string malformedCrc32 = "not-valid-base64!!";
        var payload = S3Fidelity.RepeatText("presigned-checksum-query-malformed-base64-payload-", 4);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);

        const string subjectKey = "presigned/malformed.bin";
        var malformedHeaders = new Dictionary<string, string> { ["x-amz-checksum-crc32"] = malformedCrc32, ["x-amz-sdk-checksum-algorithm"] = "CRC32" };
        var subjectUrl = S3Fidelity.Presign(ctx, "PUT", bucket, subjectKey, 900, signedHeaders: malformedHeaders);
        var signed = S3Fidelity.SignedHeaderNames(subjectUrl);
        Ensure(
            signed.IsSupersetOf(["x-amz-checksum-crc32", "x-amz-sdk-checksum-algorithm"]),
            $"X-Amz-SignedHeaders is {Py.Repr(signed.Order(StringComparer.Ordinal).ToList())}; expected it to bind both " +
            "x-amz-checksum-crc32 and x-amz-sdk-checksum-algorithm into the signature");
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", subjectUrl, malformedHeaders, payload);
        Ensure(
            status == 400,
            $"a presigned PUT signed with a non-base64 ChecksumCRC32 returned {status}, expected 400: {S3Fidelity.Head(body, 300)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("InvalidRequest", "Value for x-amz-checksum-crc32 header is invalid."),
            $"the malformed-checksum refusal carries no InvalidRequest error: {S3Fidelity.Head(body, 300)}");
        Ensure(
            (await S3Fidelity.HeadError(s3, bucket, subjectKey)).Code == "404",
            $"{Py.Repr(subjectKey)} exists after the presigned PUT signed with a malformed checksum");

        const string controlKey = "presigned/control.bin";
        var crc = S3Fidelity.Crc32B64(payload);
        var controlHeaders = new Dictionary<string, string> { ["x-amz-checksum-crc32"] = crc, ["x-amz-sdk-checksum-algorithm"] = "CRC32" };
        var controlUrl = S3Fidelity.Presign(ctx, "PUT", bucket, controlKey, 900, signedHeaders: controlHeaders);
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", controlUrl, controlHeaders, payload);
        Ensure(
            status == 200,
            "the control presigned PUT, signed with a well-formed ChecksumCRC32 for the same body, " +
            $"returned {status}: {S3Fidelity.Head(body, 300)} — the refusal above was not about the mechanism");
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = controlKey, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumCRC32 == crc, $"the control object reports ChecksumCRC32={Py.Repr(head.ChecksumCRC32)}, expected {Py.Repr(crc)}");
    }
}
