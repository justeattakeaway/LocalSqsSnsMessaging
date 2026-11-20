// Translated from behavioral/s3/check_delete_object_nonexistent_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-object-nonexistent-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectNonexistentIdempotent
{
    /// <summary>DeleteObject on a key that never existed SUCCEEDS with HTTP 204 -- deletes are idempotent, not 404s.</summary>
    [Test]
    [DisplayName("s3.delete-object-nonexistent-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "never-written.txt";

        var resp = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });

        Ensure(resp.HttpStatusCode == HttpStatusCode.NoContent, $"expected 204 for delete of a missing key, got {(int)resp.HttpStatusCode}");
        Ensure(resp.DeleteMarker is null, "unversioned bucket must not report a delete marker");

        // repeatable: an identical second delete of the same never-existed key is
        // itself a 204, not an error -- that is what makes this idempotent rather
        // than a one-shot side effect
        var resp2 = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        Ensure(resp2.HttpStatusCode == HttpStatusCode.NoContent, $"expected the repeat delete of a missing key to stay 204, got {(int)resp2.HttpStatusCode}");
        Ensure(resp2.DeleteMarker is null, "unversioned bucket must not report a delete marker on repeat delete");

        // independent read at the destination: the key is genuinely absent, not
        // merely unreported by delete_object's own response
        var (code, message) = await S3Fidelity.ClientError(
            () => s3.GetObjectMetadataAsync(bucket, key),
            "expected the call to fail with a not-found error, but it succeeded");
        // HEAD responses carry no body, so the code and message come from the bare 404 status.
        Ensure(code == "404", $"expected HeadObject 404 for the absent key, got {Py.Repr(code)}");
        Ensure(message == "Not Found", $"expected HeadObject's 'Not Found' for the absent key, got {Py.Repr(message)}");
    }
}
