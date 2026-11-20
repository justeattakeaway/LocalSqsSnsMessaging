// Translated from behavioral/s3/check_cli_s3_cp_large_file_multipart_etag_shape.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cli-s3-cp-large-file-multipart-etag-shape</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliS3CpLargeFileMultipartEtagShape
{
    /// <summary>Over the CLI's 8 MiB multipart threshold, so this always splits.</summary>
    private const int Size = 20 * 1024 * 1024;

    /// <summary>The CLI's default multipart_chunksize; its config file is empty here.</summary>
    private const int PartSize = 8 * 1024 * 1024;

    /// <summary>
    /// `aws s3 cp` of a 20 MiB file uploads as three 8 MiB-chunked parts, the stored ETag is the MD5
    /// of the concatenated part MD5s suffixed '-3', and a download round-trips the exact bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.cli-s3-cp-large-file-multipart-etag-shape")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-cpmpu-");

        var payload = S3Fidelity.RandomBytes(Size);

        const string key = "large.bin";
        // `aws s3 cp large.bin s3://<bucket>/large.bin`: CreateMultipartUpload, three UploadParts of
        // 8 MiB (the last 4 MiB), CompleteMultipartUpload.
        await S3Fidelity.CliUpload(s3, bucket, key, payload);

        var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest { BucketName = bucket, Key = key, ObjectAttributes = [ObjectAttributes.ObjectParts] });
        var totalParts = attrs.ObjectParts?.TotalPartsCount;
        var chunks = Enumerable.Range(0, (Size + PartSize - 1) / PartSize)
            .Select(i => payload.AsSpan(i * PartSize, Math.Min(PartSize, Size - (i * PartSize))).ToArray())
            .ToList();
        Ensure(
            totalParts == chunks.Count && chunks.Count == 3,
            $"expected `aws s3 cp` of {Size} bytes in {PartSize}-byte parts to upload 3 parts, got TotalPartsCount {Py.Repr(totalParts)}");

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var etag = head.ETag.Trim('"');
        var want = S3Fidelity.Md5Hex([.. chunks.SelectMany(S3Fidelity.Md5)]) + "-3";
        Ensure(etag == want, $"multipart ETag read as {Py.Repr(etag)}, expected the MD5 of the part MD5s {Py.Repr(want)}");

        // `aws s3 cp s3://<bucket>/large.bin roundtrip.bin`: HeadObject, then ranged GetObjects.
        var download = await S3Fidelity.CliDownload(s3, bucket, key);
        Ensure(download.AsSpan().SequenceEqual(payload), "the multipart upload did not round-trip byte-exact");
    }
}
