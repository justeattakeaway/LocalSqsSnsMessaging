// Translated from behavioral/s3/check_put_object_if_none_match_precondition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-if-none-match-precondition</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectIfNoneMatchPrecondition
{
    /// <summary>
    /// PutObject with If-None-Match: * succeeds on an absent key, fails with PreconditionFailed and its
    /// exact message once the key exists (leaving the object unchanged), and succeeds again after the key is
    /// deleted.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-if-none-match-precondition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "conditional.txt";

        // --- Positive control: no current object yet, so If-None-Match: * must be honored.
        var created = await Put(s3, name, key, "original");
        var status = (int)created.HttpStatusCode;
        Ensure(created.HttpStatusCode == HttpStatusCode.OK, $"expected the create-if-absent PUT to succeed with 200, got {status}");

        // --- Break exactly one thing: the key now has a current object.
        var code = await S3Fidelity.ErrorPair(
            () => Put(s3, name, key, "clobber"),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(
            code == ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold"),
            $"expected PreconditionFailed, got {S3Fidelity.Repr(code)}");

        var still = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, name, key));
        Ensure(still == "original", "object must be unchanged after a failed conditional write");

        // --- Restore: delete the key, then the identical call must succeed again.
        await s3.DeleteObjectAsync(name, key);
        var restored = await Put(s3, name, key, "recreated");
        var restoredStatus = (int)restored.HttpStatusCode;
        Ensure(restored.HttpStatusCode == HttpStatusCode.OK, $"expected the identical call to succeed again once the key was gone, got {restoredStatus}");
        var recreated = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, name, key));
        Ensure(recreated == "recreated", "the restored create-if-absent PUT did not land its own bytes");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, string body) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(Encoding.ASCII.GetBytes(body)),
            IfNoneMatch = "*",
        });
}
