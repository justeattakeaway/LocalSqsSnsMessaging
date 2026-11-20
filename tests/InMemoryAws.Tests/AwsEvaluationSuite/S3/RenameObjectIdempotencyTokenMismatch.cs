// Translated from behavioral/s3/check_rename_object_idempotency_token_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-idempotency-token-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectIdempotencyTokenMismatch
{
    /// <summary>
    /// RenameObject retried with the same ClientToken but different parameters returns
    /// IdempotentParameterMismatch instead of silently re-executing; the same ClientToken with the
    /// SAME parameters is a no-op idempotent success.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-idempotency-token-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var baseName = FidelityContext.UniqueName("sim2real")[..20].Trim('-');
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, baseName, S3Fidelity.AvailabilityZoneIds(ctx));
        const string srcKey = "src-object.txt";
        const string dstKey = "dst-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, ContentBody = "payload-v1" });
        var token = "tok-" + FidelityContext.UniqueName("rn")[..14];
        await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKey, RenameSource = srcKey, ClientToken = token });

        // confirm the rename actually happened: old key gone, new key present with content
        Ensure(await HeadCode(s3, bucket, srcKey) == "404", "assert _error_code(lambda: s3.head_object(Bucket=bucket, Key=src_key)) == '404'");
        Ensure((await S3Fidelity.GetBytes(s3, bucket, dstKey)).SequenceEqual("payload-v1"u8.ToArray()), "assert body == b'payload-v1'");

        // retry with the SAME client token and the SAME parameters: idempotent no-op success
        var resp = await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKey, RenameSource = srcKey, ClientToken = token });
        Ensure((int)resp.HttpStatusCode == 200, "assert resp['ResponseMetadata']['HTTPStatusCode'] == 200");

        // retry with the SAME client token but DIFFERENT parameters: mismatch, not a silent second rename
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "other-src.txt", ContentBody = "payload-v2" });
        var error = await S3Fidelity.Error(
            () => s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = "other-dst.txt", RenameSource = "other-src.txt", ClientToken = token }),
            "RenameObject reusing the client token with other parameters succeeded");
        Ensure(error.Code == "IdempotentParameterMismatch", $"expected IdempotentParameterMismatch, got {error.Code}");
        const string want = "Parameters on this idempotent request are inconsistent with parameters used in previous request(s).";
        Ensure(error.Message == want, $"expected {Py.Repr(want)}, got {Py.Repr(error.Message)}");

        // the forbidden effect did not happen: "other-src.txt" was NOT renamed to "other-dst.txt"
        Ensure(await HeadCode(s3, bucket, "other-dst.txt") == "404", "assert _error_code(lambda: s3.head_object(Bucket=bucket, Key='other-dst.txt')) == '404'");
        await s3.GetObjectMetadataAsync(bucket, "other-src.txt"); // still present, unchanged
    }

    /// <summary>HeadObject/HeadBucket surface as bare HTTP status; normalized to "404" for comparisons.</summary>
    private static async Task<string?> HeadCode(IAmazonS3 s3, string bucket, string key)
    {
        var error = await S3Fidelity.Error(() => s3.GetObjectMetadataAsync(bucket, key));
        return error.Code is "404" or "NotFound" || (int)error.StatusCode == 404 ? "404" : error.Code;
    }
}
