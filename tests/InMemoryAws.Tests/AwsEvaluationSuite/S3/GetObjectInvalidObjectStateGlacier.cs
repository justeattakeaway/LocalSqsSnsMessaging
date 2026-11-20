// Translated from behavioral/s3/check_get_object_invalid_object_state_glacier.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-invalid-object-state-glacier</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectInvalidObjectStateGlacier
{
    /// <summary>
    /// GetObject on an unrestored GLACIER object fails with InvalidObjectState, and still does right
    /// after RestoreObject (the restore is async); a STANDARD object in the same bucket reads fine.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-invalid-object-state-glacier")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string controlKey = "active.bin";
        var controlBody = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        await S3Fidelity.PutWithClass(s3, name, controlKey, controlBody);
        Ensure((await S3Fidelity.GetBytes(s3, name, controlKey)).SequenceEqual(controlBody), "positive control: GetObject on a STANDARD object must succeed");
        const string key = "archived.bin";
        await S3Fidelity.PutWithClass(s3, name, key, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024), "GLACIER");
        var err = await S3Fidelity.Refusal(() => s3.GetObjectAsync(name, key));
        Ensure(err.Code == "InvalidObjectState", $"expected InvalidObjectState, got {S3Fidelity.Repr(err)}");
        Ensure(err.Message == "The operation is not valid for the object's storage class", S3Fidelity.Repr(err));
        await S3Fidelity.Restore(s3, name, key, 1);
        var err2 = await S3Fidelity.Refusal(() => s3.GetObjectAsync(name, key));
        Ensure(
            err2.Code == "InvalidObjectState",
            $"expected InvalidObjectState immediately after RestoreObject (restore is async and not yet complete), got {S3Fidelity.Repr(err2)}");
        Ensure(err2.Message == "The operation is not valid for the object's storage class", S3Fidelity.Repr(err2));
    }
}
