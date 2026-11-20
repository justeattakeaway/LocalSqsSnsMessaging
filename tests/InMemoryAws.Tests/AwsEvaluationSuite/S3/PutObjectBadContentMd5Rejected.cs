// Translated from behavioral/s3/check_put_object_bad_content_md5_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-bad-content-md5-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectBadContentMd5Rejected
{
    /// <summary>
    /// PutObject with a mismatched Content-MD5 is rejected with BadDigest and stores nothing;
    /// the same bytes with the correct Content-MD5 are accepted, so the refusal is the digest's.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-bad-content-md5-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "baddigest.txt";
        var body = Encoding.ASCII.GetBytes("the real payload");
        var correctMd5 = Convert.ToBase64String(S3Fidelity.Md5(body));
        var wrongMd5 = Convert.ToBase64String(S3Fidelity.Md5([.. body, (byte)'x']));
        Ensure(wrongMd5 != correctMd5, "the wrong digest collided with the correct one");

        // --- Break exactly one thing: the Content-MD5 header, over otherwise valid bytes.
        var exc = await S3Fidelity.ExpectError(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(body),
            MD5Digest = wrongMd5,
        }));
        var status = (int)exc.StatusCode;
        Ensure(exc.Code == "BadDigest", $"expected BadDigest for a mismatched Content-MD5, got {Py.Repr(exc.Code)}");
        Ensure(
            exc.Message == "The Content-MD5 you specified did not match what we received.",
            $"expected the exact BadDigest message for a mismatched Content-MD5, got {Py.Repr(exc.Message)}");
        Ensure(status == 400, $"expected HTTP 400 for BadDigest, got {status}");

        // --- Effect absent: nothing was stored at the key.
        var missing = await S3Fidelity.ExpectError(() => s3.GetObjectMetadataAsync(bucket, key));
        var (missingCode, missingMessage) = S3Fidelity.HeadError(missing);
        var missingStatus = (int)missing.StatusCode;
        Ensure(missingStatus == 404, $"HeadObject after the BadDigest refusal returned {missingStatus}, expected 404");
        Ensure(missingCode == "404", $"HeadObject after the BadDigest refusal returned code {Py.Repr(missingCode)}, expected '404'");
        Ensure(missingMessage == "Not Found", $"HeadObject after the BadDigest refusal said {Py.Repr(missingMessage)}, expected 'Not Found'");
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(listed.Count == 0, $"the bucket holds {Py.Repr(listed)} after the refused PUT; it must be empty");

        // --- POSITIVE CONTROL: the identical bytes at the identical key, with the correct
        // MD5-computed Content-MD5, must be accepted.
        var stored = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(body),
            MD5Digest = correctMd5,
        });
        Ensure(
            stored.HttpStatusCode == HttpStatusCode.OK,
            $"the byte-identical PutObject with a correct Content-MD5 was not accepted: {(int)stored.HttpStatusCode}");

        using var got = await s3.GetObjectAsync(bucket, key);
        var gotBody = await S3Fidelity.ReadBody(got);
        Ensure(gotBody.AsSpan().SequenceEqual(body), $"the stored object is {Py.Repr(Encoding.ASCII.GetString(gotBody))}, want the positive control's {Py.Repr(Encoding.ASCII.GetString(body))}");
        var md5Hex = S3Fidelity.Md5Hex(body);
        Ensure(
            got.ETag == $"\"{md5Hex}\"",
            $"the stored object's ETag is {Py.Repr(got.ETag)}; for a single-part, unencrypted upload "
            + $"it must equal the hex MD5 the client computed over the same bytes, \"{md5Hex}\"");
    }
}
