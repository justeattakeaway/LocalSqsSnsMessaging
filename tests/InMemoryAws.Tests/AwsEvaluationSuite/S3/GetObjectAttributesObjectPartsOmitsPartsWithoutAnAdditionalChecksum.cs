// Translated from behavioral/s3/check_get_object_attributes_object_parts_omits_parts_without_an_additional_checksum.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-object-parts-omits-parts-without-an-additional-checksum</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesObjectPartsOmitsPartsWithoutAnAdditionalChecksum
{
    private const string Check = "get-object-attributes-object-parts-omits-parts-without-an-additional-checksum";

    /// <summary>S3 multipart minimum part size (except the last part).</summary>
    private const int PartSize = 5 * 1024 * 1024;

    /// <summary>
    /// GetObjectAttributes's ObjectParts carries TotalPartsCount for every multipart object, but
    /// its per-part Parts list is populated only when the upload requested an additional checksum
    /// (CRC32/CRC32C/SHA1/SHA256); without one, Parts is empty even though the parts exist.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-object-parts-omits-parts-without-an-additional-checksum")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-goapc-");

        var plainParts = await UploadTwoParts(ctx, s3, bucket, "no-checksum.bin", checksumAlgorithm: null);
        var checkedParts = await UploadTwoParts(ctx, s3, bucket, "crc32-checksum.bin", checksumAlgorithm: ChecksumAlgorithm.CRC32);

        var plainAttrs = (await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "no-checksum.bin",
            ObjectAttributes = [ObjectAttributes.ObjectParts],
        })).ObjectParts ?? throw Fail("GetObjectAttributes returned no ObjectParts");
        Ensure(
            plainAttrs.TotalPartsCount == plainParts.Count,
            $"TotalPartsCount must count the {plainParts.Count} uploaded parts even with no additional checksum, got {Py.Repr(plainAttrs.TotalPartsCount)}");
        var plainList = (plainAttrs.Parts ?? []).Select(S3Fidelity.PartDict).ToList();
        Ensure(
            plainList.Count == 0,
            $"ObjectParts.Parts must be empty for a multipart upload with no additional checksum, got {Py.Repr(plainList)}");

        var checkedAttrs = (await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = "crc32-checksum.bin",
            ObjectAttributes = [ObjectAttributes.ObjectParts],
        })).ObjectParts ?? throw Fail("GetObjectAttributes returned no ObjectParts");
        Ensure(
            checkedAttrs.TotalPartsCount == checkedParts.Count,
            $"TotalPartsCount must count the {checkedParts.Count} uploaded parts, got {Py.Repr(checkedAttrs.TotalPartsCount)}");
        var reported = (checkedAttrs.Parts ?? []).ToDictionary(p => p.PartNumber ?? 0);
        Ensure(
            reported.Keys.ToHashSet().SetEquals(checkedParts.Keys),
            $"ObjectParts.Parts must list every part {Py.Repr(checkedParts.Keys.Order().ToList())} once the upload carried a CRC32 checksum, got {Py.Repr(reported.Keys.Order().ToList())}");
        foreach (var (n, expect) in checkedParts)
        {
            Ensure(
                reported[n].ChecksumCRC32 == expect.ChecksumCRC32,
                $"part {n}'s GetObjectAttributes ChecksumCRC32 {Py.Repr(reported[n].ChecksumCRC32)} != UploadPart's own {Py.Repr(expect.ChecksumCRC32)}");
            Ensure(
                reported[n].Size == expect.Size,
                $"part {n}'s GetObjectAttributes Size {Py.Repr(reported[n].Size)} != the {expect.Size} byte(s) uploaded");
        }
    }

    /// <summary>
    /// <c>_upload_two_parts(...)</c>: uploads a 2-part object (with <paramref name="checksumAlgorithm"/>
    /// or none) and returns each part's size and checksum.
    /// </summary>
    private static async Task<Dictionary<int, (long Size, string? ChecksumCRC32)>> UploadTwoParts(
        FidelityContext ctx, IAmazonS3 s3, string bucket, string key, ChecksumAlgorithm? checksumAlgorithm)
    {
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ChecksumAlgorithm = checksumAlgorithm,
        })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var sizes = new Dictionary<int, int> { [1] = PartSize, [2] = 4096 };
        var uploaded = new Dictionary<int, (long Size, string? ChecksumCRC32)>();
        var partsForComplete = new List<PartETag>();
        foreach (var (n, size) in sizes)
        {
            var body = new byte[size];
            Array.Fill(body, (byte)n);
            var part = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                PartNumber = n,
                UploadId = uploadId,
                InputStream = new MemoryStream(body),
                ChecksumAlgorithm = checksumAlgorithm,
            });
            uploaded[n] = (size, checksumAlgorithm is not null ? part.ChecksumCRC32 : null);
            partsForComplete.Add(new PartETag
            {
                PartNumber = n,
                ETag = part.ETag,
                ChecksumCRC32 = checksumAlgorithm is not null ? part.ChecksumCRC32 : null,
            });
        }

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = partsForComplete,
        });
        return uploaded;
    }
}
