// Translated from behavioral/s3/check_conc_s3_conditional_writes_read_after_conditional_overwrite_every_read_path_is_current.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-read-after-conditional-overwrite-every-read-path-is-current</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesReadAfterConditionalOverwriteEveryReadPathIsCurrent
{
    private const int Rounds = 8;
    private const int Mib = 1024 * 1024;

    /// <summary>
    /// After each of eight If-Match overwrites (the last via multipart), GET, HEAD, ListObjectsV2
    /// and GetObjectAttributes immediately agree on the new bytes, ETag and size (strong consistency).
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-read-after-conditional-overwrite-every-read-path-is-current")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var cas = ctx.S3Client(maxAttempts: 1);
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "state/document.json";

        var etag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{}" })).ETag;
        for (var r = 1; r <= Rounds; r++)
        {
            var size = (r * 9_973) + ((r % 3) * 511);
            var body = S3Fidelity.Pattern($"round-{r}", size);
            string newEtag;
            if (r < Rounds)
            {
                newEtag = (await cas.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), IfMatch = etag })).ETag;
            }
            else
            {
                size = (5 * Mib) + 4_321;
                body = S3Fidelity.Pattern("round-final-multipart", size);
                var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
                ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
                var parts = await S3Fidelity.UploadParts(s3, bucket, key, uploadId, body[..(5 * Mib)], body[(5 * Mib)..]);
                newEtag = (await cas.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = parts,
                    IfMatch = etag,
                })).ETag;
            }

            Ensure(newEtag != etag, $"round {r}: overwrite kept ETag {Py.Repr(etag)}");

            using var got = await s3.GetObjectAsync(bucket, key);
            var read = await S3Fidelity.ReadBody(got);
            Ensure(
                read.AsSpan().SequenceEqual(body),
                $"round {r}: immediate GET returned {read.Length} bytes (sha256 {S3Fidelity.Sha256Hex(read)}), not the {size}-byte body just written");
            Ensure(got.ETag == newEtag, $"round {r}: GET ETag {Py.Repr(got.ETag)} != {Py.Repr(newEtag)}");

            var head = await s3.GetObjectMetadataAsync(bucket, key);
            Ensure(
                head.ETag == newEtag && head.ContentLength == size,
                $"round {r}: HEAD reports ({Py.Repr(head.ETag)}, {head.ContentLength}); want ({Py.Repr(newEtag)}, {size})");

            var listed = await S3Fidelity.ListedObjects(s3, bucket);
            Ensure(
                listed.SequenceEqual([(key, newEtag, (long)size)]),
                $"round {r}: ListObjectsV2 reports {Py.Repr(listed.Select(l => l.ToString()).ToList())}; want [({Py.Repr(key)}, {Py.Repr(newEtag)}, {size})]");

            var attrs = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
            {
                BucketName = bucket,
                Key = key,
                ObjectAttributes = [ObjectAttributes.ETag, ObjectAttributes.ObjectSize],
            });
            Ensure(
                attrs.ETag == newEtag.Trim('"') && attrs.ObjectSize == size,
                $"round {r}: GetObjectAttributes reports ({Py.Repr(attrs.ETag)}, {attrs.ObjectSize}); want ({Py.Repr(newEtag.Trim('"'))}, {size})");

            // The next If-Match uses the ETag just read, so a stale read would 412 the next round.
            etag = got.ETag;
        }

        Ensure(etag.EndsWith("-2\"", StringComparison.Ordinal), $"the final multipart overwrite has ETag {Py.Repr(etag)}; want a -2 ETag");
    }
}
