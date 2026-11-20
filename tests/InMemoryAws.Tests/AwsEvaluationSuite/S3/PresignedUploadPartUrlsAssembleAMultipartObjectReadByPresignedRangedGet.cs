// Translated from behavioral/s3/check_presigned_upload_part_urls_assemble_a_multipart_object_read_by_presigned_ranged_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.presigned-upload-part-urls-assemble-a-multipart-object-read-by-presigned-ranged-get</summary>
[Category("AwsEvaluationSuite")]
public sealed class PresignedUploadPartUrlsAssembleAMultipartObjectReadByPresignedRangedGet
{
    /// <summary>
    /// Parts PUT with no credentials to presigned UploadPart URLs return their MD5 ETags and complete into
    /// an object with the composite '-2' ETag; a presigned GET with a Range header returns 206 and exactly
    /// the addressed bytes spanning the part boundary.
    /// </summary>
    [Test]
    [DisplayName("s3.presigned-upload-part-urls-assemble-a-multipart-object-read-by-presigned-ranged-get")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "presigned-upload-part-urls-assemble-a-multipart-object-read-by-presigned-ranged-get";
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

        const string key = "browser/upload.bin";
        byte[][] parts = [S3Fidelity.RandomBytes(5 * mib + RandomNumberGenerator.GetInt32(2048)), S3Fidelity.RandomBytes(1 + RandomNumberGenerator.GetInt32(9000))];
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var done = new List<PartETag>();
        for (var number = 1; number <= parts.Length; number++)
        {
            var data = parts[number - 1];
            var url = S3Fidelity.Presign(
                ctx,
                "PUT",
                bucket,
                key,
                300,
                query: [new("partNumber", number.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("uploadId", uploadId)],
                pathStyle: true);
            var (status, headers, _) = await Request(ctx, url, "PUT", data);
            Ensure(status == 200, $"presigned UploadPart {number} answered HTTP {status}");
            var etag = headers.GetValueOrDefault("etag");
            Ensure(etag == $"\"{S3Fidelity.Md5Hex(data)}\"", $"presigned UploadPart {number} ETag {Py.Repr(etag)} is not the MD5 of its bytes");
            done.Add(S3Fidelity.Part(number, etag!));
        }

        var listed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var sizes = listed.Select(p => (p.PartNumber ?? 0, p.Size ?? 0)).ToList();
        Ensure(sizes.SequenceEqual([(1, (long)parts[0].Length), (2, parts[1].Length)]), $"ListParts reports [{string.Join(", ", sizes)}]");
        var completed = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = done });
        var composite = Convert.ToHexStringLower(MD5.HashData(parts.SelectMany(S3Fidelity.Md5).ToArray()));
        var wantEtag = $"\"{composite}-2\"";
        Ensure(completed.ETag == wantEtag, $"CompleteMultipartUpload ETag {Py.Repr(completed.ETag)}, expected {Py.Repr(wantEtag)}");

        byte[] whole = [.. parts[0], .. parts[1]];
        var start = parts[0].Length - 1 - RandomNumberGenerator.GetInt32(100);
        var end = parts[0].Length + RandomNumberGenerator.GetInt32(parts[1].Length);
        var getUrl = S3Fidelity.Presign(ctx, "GET", bucket, key, 300, pathStyle: true);
        var (getStatus, getHeaders, body) = await Request(ctx, getUrl, "GET", headers: new Dictionary<string, string> { ["Range"] = $"bytes={start}-{end}" });
        Ensure(getStatus == 206, $"presigned ranged GET answered HTTP {getStatus}, expected 206");
        Ensure(
            getHeaders.GetValueOrDefault("content-range") == $"bytes {start}-{end}/{whole.Length}",
            $"presigned ranged GET Content-Range {Py.Repr(getHeaders.GetValueOrDefault("content-range"))}, expected " +
            Py.Repr($"bytes {start}-{end}/{whole.Length}"));
        Ensure(
            body.SequenceEqual(whole[start..(end + 1)]),
            $"presigned ranged GET bytes={start}-{end} returned {body.Length} bytes, not the {end - start + 1} addressed");

        using var got = await s3.GetObjectAsync(bucket, key);
        var readBack = await S3Fidelity.ReadBody(got);
        var seen = (readBack.Length, S3Fidelity.Md5Hex(readBack), got.ETag);
        var want = (whole.Length, S3Fidelity.Md5Hex(whole), wantEtag);
        Ensure(seen == want, $"GetObject reads (len, md5, ETag) {seen}, expected the assembled object's {want}");
    }

    /// <summary>
    /// <c>_request(ctx, url, method, data, headers)</c>: an unauthenticated request to a presigned URL;
    /// an HTTP error status is a fidelity failure.
    /// </summary>
    private static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> Request(
        FidelityContext ctx,
        string url,
        string method,
        byte[]? data = null,
        Dictionary<string, string>? headers = null)
    {
        var answer = await S3Fidelity.Send(ctx, method, url, headers, data);
        Ensure(answer.Status < 400, $"DATAPLANE_UNIMPLEMENTED: the presigned {method} URL did not serve: HTTPError: HTTP Error {answer.Status}: {S3Fidelity.Head(answer.Body)}");
        return answer;
    }
}
