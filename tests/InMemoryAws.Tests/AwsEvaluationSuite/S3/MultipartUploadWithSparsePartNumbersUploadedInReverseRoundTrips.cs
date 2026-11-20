// Translated from behavioral/s3/check_multipart_upload_with_sparse_part_numbers_uploaded_in_reverse_round_trips.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-with-sparse-part-numbers-uploaded-in-reverse-round-trips</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadWithSparsePartNumbersUploadedInReverseRoundTrips
{
    /// <summary>
    /// Three parts with gapped random part numbers, uploaded highest first, complete in part-number order:
    /// the object is their concatenation in that order, its ETag the MD5 of the part MD5s with '-3', and
    /// GetObject, HeadObject and a presigned GET agree on bytes, size and ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-upload-with-sparse-part-numbers-uploaded-in-reverse-round-trips")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "multipart-upload-with-sparse-part-numbers-uploaded-in-reverse-round-trips";
        const int mib = 1024 * 1024;
        var s3 = ctx.S3;
        var bucket = FidelityContext.UniqueName("sim2real-behav-mpu-");
        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket,
            UseClientRegion = false,
            PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) },
        });
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, bucket));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = check }] });

        const string key = "uploads/sparse parts/archive.tar";
        var numbers = Enumerable.Range(2, 9998).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(3).Order().ToList();
        int[] sizes = [5 * mib + 1 + RandomNumberGenerator.GetInt32(4096), 5 * mib, 1 + RandomNumberGenerator.GetInt32(70000)];
        var data = numbers.Zip(sizes).ToDictionary(p => p.First, p => S3Fidelity.RandomBytes(p.Second));
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var etags = new Dictionary<int, string>();
        foreach (var number in Enumerable.Reverse(numbers))
        {
            var etag = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data[number]) })).ETag;
            Ensure(etag == $"\"{S3Fidelity.Md5Hex(data[number])}\"", $"part {number} ETag {Py.Repr(etag)} is not the MD5 of its bytes");
            etags[number] = etag;
        }

        var listed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var got = listed.Select(p => (p.PartNumber ?? 0, p.Size ?? 0, p.ETag)).ToList();
        var want = numbers.Select(n => (n, (long)data[n].Length, etags[n])).ToList();
        Ensure(got.SequenceEqual(want), $"ListParts reports [{string.Join(", ", got)}], expected ascending [{string.Join(", ", want)}]");

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [.. numbers.Select(n => S3Fidelity.Part(n, etags[n]))],
        });
        var composite = Convert.ToHexStringLower(MD5.HashData(numbers.SelectMany(n => S3Fidelity.Md5(data[n])).ToArray()));
        var wantEtag = $"\"{composite}-3\"";
        Ensure(done.ETag == wantEtag, $"CompleteMultipartUpload ETag {Py.Repr(done.ETag)}, expected {Py.Repr(wantEtag)}");

        var whole = numbers.SelectMany(n => data[n]).ToArray();
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            head.ETag == wantEtag && head.ContentLength == whole.Length,
            $"HeadObject (ETag, ContentLength) ({Py.Repr(head.ETag)}, {head.ContentLength}), expected " +
            $"({Py.Repr(wantEtag)}, {whole.Length})");
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            body.SequenceEqual(whole),
            $"GetObject returned {body.Length} bytes (md5 {S3Fidelity.Md5Hex(body)}), expected the parts " +
            $"in part-number order: {whole.Length} bytes (md5 {S3Fidelity.Md5Hex(whole)})");

        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.AddSeconds(300) });
        var (status, _, fetched) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"DATAPLANE_UNIMPLEMENTED: the presigned GET URL did not serve the object: HTTP Error {status}: {S3Fidelity.Head(fetched)}");
        Ensure(fetched.SequenceEqual(whole), $"the presigned GET returned {fetched.Length} bytes, expected the {whole.Length} assembled bytes");
        var pending = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(pending.Count == 0, $"ListMultipartUploads after completion still lists {pending.Count} upload(s)");
    }
}
