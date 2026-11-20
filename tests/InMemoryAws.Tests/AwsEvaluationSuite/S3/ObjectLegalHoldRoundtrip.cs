// Translated from behavioral/s3/check_object_legal_hold_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-legal-hold-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLegalHoldRoundtrip
{
    /// <summary>PutObjectLegalHold status transitions (ON -> OFF -> ON) are reflected by GetObjectLegalHold.</summary>
    [Test]
    [DisplayName("s3.object-legal-hold-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateObjectLockBucket(ctx);
        const string key = "legalhold.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "data" });

        await PutHold(s3, name, key, ObjectLockLegalHoldStatus.On);
        var status1 = await GetHold(s3, name, key);
        Ensure(status1 == "ON", $"expected legal hold ON after put, got {Py.Repr(status1)}");

        await PutHold(s3, name, key, ObjectLockLegalHoldStatus.Off);
        var status2 = await GetHold(s3, name, key);
        Ensure(status2 == "OFF", $"expected legal hold OFF after put, got {Py.Repr(status2)}");

        await PutHold(s3, name, key, ObjectLockLegalHoldStatus.On);
        var status3 = await GetHold(s3, name, key);
        Ensure(status3 == "ON", $"expected legal hold ON after second put, got {Py.Repr(status3)}");

        // Release before teardown so cleanup's own force-delete doesn't have to.
        await PutHold(s3, name, key, ObjectLockLegalHoldStatus.Off);
    }

    private static Task<PutObjectLegalHoldResponse> PutHold(IAmazonS3 s3, string bucket, string key, ObjectLockLegalHoldStatus status) =>
        s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest { BucketName = bucket, Key = key, LegalHold = new ObjectLockLegalHold { Status = status } });

    private static async Task<string?> GetHold(IAmazonS3 s3, string bucket, string key) =>
        (await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = key })).LegalHold.Status?.Value;
}
