// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_ssec_multipart_upload_round_trips_only_with_the_customer_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-ssec-multipart-upload-round-trips-only-with-the-customer-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementSsecMultipartUploadRoundTripsOnlyWithTheCustomerKey
{
    /// <summary>
    /// An SSE-C multipart upload reassembles byte-exact; every part and read needs the initiate call's key
    /// (SDK and presigned raw HTTPS), while ListParts metadata needs no key.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-ssec-multipart-upload-round-trips-only-with-the-customer-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-ssec-multipart-upload-round-trips-only-with-the-customer-key";
        const int part1Size = 5 * 1024 * 1024; // the minimum size of every part but the last
        const int part2Size = 1234;
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        await S3Fidelity.AllowSsec(s3, bucket);
        var key = RandomNumberGenerator.GetBytes(32);
        var otherKey = RandomNumberGenerator.GetBytes(32);
        var md5 = S3Fidelity.B64(MD5.HashData(key));
        const string obj = "ssec-multipart.bin";
        var part1 = RandomNumberGenerator.GetBytes(part1Size);
        var part2 = RandomNumberGenerator.GetBytes(part2Size);

        var mpu = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = obj,
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        });
        var uploadId = mpu.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, obj, uploadId));
        Ensure(
            mpu.ServerSideEncryptionCustomerMethod == ServerSideEncryptionCustomerMethod.AES256,
            $"CreateMultipartUpload echoed SSECustomerAlgorithm {Py.Repr(mpu.ServerSideEncryptionCustomerMethod?.Value)}, expected 'AES256'");
        Ensure(
            mpu.ServerSideEncryptionCustomerProvidedKeyMD5 == md5,
            $"CreateMultipartUpload echoed key MD5 {Py.Repr(mpu.ServerSideEncryptionCustomerProvidedKeyMD5)}, expected {Py.Repr(md5)}");

        // A part without the key, and a part with another key, are refused.
        var (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = obj, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(part2),
        }));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.InitiateEncrypted),
            $"UploadPart with no SSE-C key into an SSE-C upload returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");
        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = obj, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(part2),
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(otherKey),
        }));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.ParamsMismatch),
            $"UploadPart with a DIFFERENT SSE-C key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");

        // With the key, both parts go in and echo its MD5.
        UploadPartRequest Part(int number, byte[] data) => new()
        {
            BucketName = bucket, Key = obj, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data),
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        };
        var e1 = await s3.UploadPartAsync(Part(1, part1));
        var e2 = await s3.UploadPartAsync(Part(2, part2));
        foreach (var (n, e) in new[] { (1, e1), (2, e2) })
        {
            Ensure(e.SSECustomerKeyMD5 == md5, $"UploadPart {n} echoed key MD5 {Py.Repr(e.SSECustomerKeyMD5)}, expected {Py.Repr(md5)}");
        }

        // ListParts is metadata: no key needed.
        var listed = await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = obj, UploadId = uploadId });
        var sizes = (listed.Parts ?? []).Select(p => (p.PartNumber ?? 0, p.Size ?? 0)).ToList();
        Ensure(
            sizes.SequenceEqual([(1, (long)part1Size), (2, part2Size)]),
            $"ListParts without the key listed [{string.Join(", ", sizes)}]; expected [(1, {part1Size}), (2, {part2Size})]");

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = obj,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, e1.ETag), S3Fidelity.Part(2, e2.ETag)],
            SSECustomerAlgorithm = "AES256",
            SSECustomerKey = S3Fidelity.B64(key),
            SSECustomerKeyMD5 = S3Fidelity.B64(MD5.HashData(key)),
        });
        Ensure(done.ETag.Trim('"').EndsWith("-2", StringComparison.Ordinal), $"CompleteMultipartUpload ETag {Py.Repr(done.ETag)} does not carry the 2-part suffix");

        // Reads need the key: with it the whole and a part come back; without or with another, refused.
        GetObjectRequest Read(byte[]? withKey, int? partNumber = null) => new()
        {
            BucketName = bucket,
            Key = obj,
            PartNumber = partNumber,
            ServerSideEncryptionCustomerMethod = withKey is null ? null : ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = withKey is null ? null : S3Fidelity.B64(withKey),
        };
        using (var got = await s3.GetObjectAsync(Read(key)))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(
                body.SequenceEqual([.. part1, .. part2]),
                $"GetObject with the key returned {body.Length} bytes that are not the concatenated parts ({part1Size + part2Size} bytes)");
            Ensure(
                got.ServerSideEncryptionCustomerMethod == ServerSideEncryptionCustomerMethod.AES256 && got.ServerSideEncryptionCustomerProvidedKeyMD5 == md5,
                $"GetObject echoed SSE-C {Py.Repr(got.ServerSideEncryptionCustomerMethod?.Value)}/{Py.Repr(got.ServerSideEncryptionCustomerProvidedKeyMD5)}, expected 'AES256'/{Py.Repr(md5)}");
        }

        using (var p2 = await s3.GetObjectAsync(Read(key, 2)))
        {
            var body = await S3Fidelity.ReadBody(p2);
            Ensure(body.SequenceEqual(part2), $"GetObject PartNumber=2 returned {body.Length} bytes that are not part 2");
        }

        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(Read(null)));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.StoredSse),
            $"GetObject without the key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");
        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(Read(otherKey)));
        Ensure(
            (code, status, msg) == ("AccessDenied", 403, S3Fidelity.WrongSecret),
            $"GetObject with the wrong key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected AccessDenied/403");

        // Presigned raw HTTPS: with the key headers the bytes; without, refused.
        var hdrs = S3Fidelity.SseCustomerHeaders(key);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, obj, 300, signedHeaders: hdrs, pathStyle: true);
        Ensure(url.StartsWith("https://", StringComparison.Ordinal), $"presigned URL {Py.Repr(url.Split('?')[0])} is not https");
        var (rawStatus, _, raw) = await S3Fidelity.Send(ctx, "GET", url, hdrs);
        Ensure(rawStatus == 200, $"presigned SSE-C GET with the key headers returned HTTP {rawStatus}: {S3Fidelity.Head(raw, 300)}");
        Ensure(raw.SequenceEqual([.. part1, .. part2]), $"presigned SSE-C GET returned {raw.Length} bytes that are not the concatenated parts");
        var plainUrl = S3Fidelity.Presign(ctx, "GET", bucket, obj, 300, pathStyle: true);
        (rawStatus, _, raw) = await S3Fidelity.Send(ctx, "GET", plainUrl);
        var (xmlCode, xmlMessage) = S3Fidelity.XmlCodeMessage(raw);
        Ensure(
            (rawStatus, xmlCode, xmlMessage) == (400, "InvalidRequest", S3Fidelity.StoredSse),
            $"presigned GET with no SSE-C headers returned HTTP {rawStatus} code {Py.Repr(xmlCode)}; " +
            $"expected 400 InvalidRequest — body {S3Fidelity.Head(raw, 300)}");
        Ensure(raw.AsSpan().IndexOf(part1.AsSpan(0, 64)) < 0, "the keyless raw GET leaked object bytes");
    }
}
