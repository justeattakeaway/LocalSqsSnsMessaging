// Translated from behavioral/s3/check_conc_s3_conditional_writes_if_match_on_absent_key_rejected_and_nothing_created.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-if-match-on-absent-key-rejected-and-nothing-created</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesIfMatchOnAbsentKeyRejectedAndNothingCreated
{
    private const string NoSuchKeyMsg = "The specified key does not exist.";

    /// <summary>
    /// If-Match on an absent key is refused 404 NoSuchKey on PutObject and CompleteMultipartUpload
    /// and creates nothing; once the key exists, If-Match with its current ETag succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-if-match-on-absent-key-rejected-and-nothing-created")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "cas/absent.txt";
        const string mpuKey = "cas/absent-multipart.bin";
        var body = Encoding.UTF8.GetBytes("compare-and-swap-must-not-create-a-missing-key");

        // The offered ETag is the body's own MD5, so only the key's absence can fail the request.
        var exc = await S3Fidelity.ExpectError(
            () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), IfMatch = S3Fidelity.Md5ETag(body) }),
            "PutObject If-Match on an absent key SUCCEEDED; the guard is being read as 'no condition' and the key was created");
        Ensure(exc.Code == "NoSuchKey", $"expected NoSuchKey for If-Match on an absent key, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == NoSuchKeyMsg, $"expected message {Py.Repr(NoSuchKeyMsg)}, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.NotFound, $"expected HTTP 404, got {(int)exc.StatusCode}");

        // --- Same boundary, CompleteMultipartUpload path.
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = mpuKey })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, mpuKey, uploadId));
        var partEtag = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = mpuKey, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(body) })).ETag;
        var mpuExc = await S3Fidelity.ExpectError(
            () => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = mpuKey,
                UploadId = uploadId,
                PartETags = [S3Fidelity.Part(1, partEtag)],
                IfMatch = partEtag,
            }),
            "CompleteMultipartUpload If-Match on an absent key SUCCEEDED and created the object");
        Ensure(mpuExc.Code == "NoSuchKey", $"expected NoSuchKey on the multipart path, got {Py.Repr(mpuExc.Code)}");
        Ensure(mpuExc.Message == NoSuchKeyMsg, $"expected message {Py.Repr(NoSuchKeyMsg)} on the multipart path, got {Py.Repr(mpuExc.Message)}");
        Ensure(mpuExc.StatusCode == HttpStatusCode.NotFound, $"expected HTTP 404 on the multipart path, got {(int)mpuExc.StatusCode}");

        // --- Effect absent: neither key exists, by listing and by direct read.
        var listed = await Listed(s3, bucket);
        Ensure(listed.Count == 0, $"after two refused If-Match writes the bucket lists {Py.Repr(listed)}; want []");
        var gone = await S3Fidelity.ExpectError(() => s3.GetObjectAsync(bucket, key), "GetObject on the refused key succeeded; the refused write created it");
        Ensure(gone.Code == "NoSuchKey", $"GetObject on the refused key did not report NoSuchKey: {gone}");
        Ensure(gone.Message == "The specified key does not exist.", $"expected the exact NoSuchKey message AWS returns, got {Py.Repr(gone.Message)}");
        Ensure(gone.Message == NoSuchKeyMsg, $"expected message {Py.Repr(NoSuchKeyMsg)} from GetObject, got {Py.Repr(gone.Message)}");

        // --- POSITIVE CONTROL: once the key exists, If-Match with its CURRENT ETag succeeds.
        var first = Encoding.UTF8.GetBytes("now-the-key-exists");
        var current = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(first) })).ETag;
        var updated = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), IfMatch = current });
        Ensure(updated.HttpStatusCode == HttpStatusCode.OK, $"If-Match with the current ETag returned {(int)updated.HttpStatusCode}");
        using var got = await s3.GetObjectAsync(bucket, key);
        Ensure((await S3Fidelity.ReadBody(got)).AsSpan().SequenceEqual(body), "the If-Match update with the current ETag did not land");
        Ensure(
            got.ETag == updated.ETag && updated.ETag == S3Fidelity.Md5ETag(body),
            $"GET ETag {Py.Repr(got.ETag)}, PUT ETag {Py.Repr(updated.ETag)}, MD5 {Py.Repr(S3Fidelity.Md5ETag(body))}: want all three equal");
        listed = await Listed(s3, bucket);
        Ensure(listed.SequenceEqual([key]), $"the bucket lists {Py.Repr(listed)}; want exactly [{Py.Repr(key)}]");
    }

    /// <summary><c>_listed(s3, bucket)</c>: every key, sorted.</summary>
    private static async Task<List<string>> Listed(IAmazonS3 s3, string bucket) =>
        [.. (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).Order(StringComparer.Ordinal)];
}
