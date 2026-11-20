// Translated from behavioral/s3/check_bucket_holding_more_than_sixty_four_mib_reads_back_every_object.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-holding-more-than-sixty-four-mib-reads-back-every-object</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketHoldingMoreThanSixtyFourMibReadsBackEveryObject
{
    private const int Objects = 5;
    private const int Size = 13 * 1024 * 1024; // 5 x 13 MiB = 65 MiB, each a single-part PutObject

    /// <summary>
    /// One bucket stores 65 MiB as five 13 MiB single-part objects and serves all of them back.
    /// Every PutObject succeeds, the listing reports each size, GetObject returns every body exactly.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-holding-more-than-sixty-four-mib-reads-back-every-object")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-bigbkt-");
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Objects; i++)
        {
            var key = $"blob/{i}.bin";
            var body = RandomNumberGenerator.GetBytes(Size);
            digests[key] = Convert.ToHexStringLower(MD5.HashData(body));
            var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
            Ensure(put.ETag.Trim('"') == digests[key], $"PutObject {key} ETag {Py.Repr(put.ETag)} must be the body's MD5 {Py.Repr(digests[key])}");
        }

        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? [])
            .ToDictionary(o => o.Key, o => o.Size, StringComparer.Ordinal);
        Ensure(
            listed.Count == digests.Count && digests.Keys.All(k => listed.TryGetValue(k, out var size) && size == Size),
            $"the bucket must list {Objects} {Size}-byte objects; got {Py.Repr(listed)}");

        foreach (var (key, digest) in digests)
        {
            using var got = await s3.GetObjectAsync(bucket, key);
            var data = await S3Fidelity.ReadBody(got);
            Ensure(got.ContentLength == Size, $"GetObject {key} ContentLength {got.ContentLength} != {Size}");
            Ensure(
                data.Length == Size && Convert.ToHexStringLower(MD5.HashData(data)) == digest,
                $"GetObject {key} returned {data.Length} bytes that differ from the {Size} stored");
        }
    }
}
