// Translated from behavioral/s3/check_upload_part_of_twenty_mib_completes_and_reads_back_exactly.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.upload-part-of-twenty-mib-completes-and-reads-back-exactly</summary>
[Category("AwsEvaluationSuite")]
public sealed class UploadPartOfTwentyMibCompletesAndReadsBackExactly
{
    private const int Mib = 1024 * 1024;
    private const int PartSize = 20 * Mib;
    private const string Key = "twenty-mib-part.bin";

    /// <summary>
    /// UploadPart accepts a single 20 MiB part and the completed object reads back exactly.
    /// The part ETag is its MD5, CompleteMultipartUpload reports MD5(part MD5)-1, GetObject the bytes.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/qfacts.html
    /// </summary>
    [Test]
    [DisplayName("s3.upload-part-of-twenty-mib-completes-and-reads-back-exactly")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-mpu20-");

        var body = RandomNumberGenerator.GetBytes(PartSize);
        var partMd5 = MD5.HashData(body);
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = Key })).UploadId;
        var part = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = Key, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(body) });
        Ensure(
            part.ETag.Trim('"') == Convert.ToHexStringLower(partMd5),
            $"UploadPart ETag {Py.Repr(part.ETag)} is not the part's MD5 {Py.Repr(Convert.ToHexStringLower(partMd5))}");

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = Key,
            UploadId = uploadId,
            PartETags = [new PartETag { PartNumber = 1, ETag = part.ETag }],
        });
        var expectedEtag = $"{Convert.ToHexStringLower(MD5.HashData(partMd5))}-1";
        Ensure(done.ETag.Trim('"') == expectedEtag, $"CompleteMultipartUpload ETag {Py.Repr(done.ETag)}, expected {Py.Repr(expectedEtag)}");

        using var got = await s3.GetObjectAsync(bucket, Key);
        var data = await S3Fidelity.ReadBody(got);
        Ensure(
            (got.ContentLength, data.Length) == (PartSize, PartSize),
            $"GetObject reported {got.ContentLength}, returned {data.Length}, expected {PartSize}");
        Ensure(data.AsSpan().SequenceEqual(body), "the object read back differs from the 20 MiB part uploaded");
        Ensure(got.ETag.Trim('"') == expectedEtag, $"GetObject ETag {Py.Repr(got.ETag)}, expected the multipart ETag {Py.Repr(expectedEtag)}");
    }
}
