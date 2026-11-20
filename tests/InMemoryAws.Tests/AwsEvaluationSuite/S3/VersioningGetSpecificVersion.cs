// Translated from behavioral/s3/check_versioning_get_specific_version.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.versioning-get-specific-version</summary>
[Category("AwsEvaluationSuite")]
public sealed class VersioningGetSpecificVersion
{
    /// <summary>GetObject with VersionId retrieves the specific version's content.</summary>
    [Test]
    [DisplayName("s3.versioning-get-specific-version")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var r1 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "ver-key", ContentBody = "first" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "ver-key", ContentBody = "second" });

        // Get the first version specifically
        using (var first = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "ver-key", VersionId = r1.VersionId }))
        {
            var got = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(first));
            Ensure(got == "first", $"expected b'first', got {Py.Repr(got)}");
        }

        // Default GET returns latest
        var gotLatest = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, "ver-key"));
        Ensure(gotLatest == "second", $"expected b'second', got {Py.Repr(gotLatest)}");
    }
}
