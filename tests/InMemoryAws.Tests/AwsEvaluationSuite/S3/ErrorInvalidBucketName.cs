// Translated from behavioral/s3/check_error_invalid_bucket_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.error-invalid-bucket-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorInvalidBucketName
{
    private const string ErrInvalidBucketName = "InvalidBucketName";

    /// <summary>
    /// CreateBucket with uppercase letters in the name is rejected with InvalidBucketName and its exact
    /// message, while a valid name is created and visible to an independent read.
    /// </summary>
    [Test]
    [DisplayName("s3.error-invalid-bucket-name")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var goodName = FidelityContext.UniqueName("sim2real-behav-s3fid-");
        await s3.PutBucketAsync(BucketParams(ctx, goodName));
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, goodName));
        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = goodName }); // independent read: the control bucket really exists

        var invalidName = goodName.ToUpperInvariant();
        // Only does anything if the create below unexpectedly lands.
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, invalidName));
        var err = await FidelityContext.CaptureError(() => s3.PutBucketAsync(BucketParams(ctx, invalidName)))
            ?? throw Fail($"CreateBucket with an uppercase bucket name (bucket={Py.Repr(invalidName)}) succeeded; expected a refusal");
        Ensure(err.Code == ErrInvalidBucketName, $"expected {ErrInvalidBucketName} for an uppercase bucket name, got {err}");
        Ensure(err.Message == "The specified bucket is not valid.", err.Message ?? "None");
    }

    private static PutBucketRequest BucketParams(FidelityContext ctx, string name)
    {
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        // us-east-1 rejects an explicit LocationConstraint for its own region.
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        return request;
    }
}
