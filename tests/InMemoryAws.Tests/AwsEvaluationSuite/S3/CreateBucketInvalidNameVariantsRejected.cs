// Translated from behavioral/s3/check_create_bucket_invalid_name_variants_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-bucket-invalid-name-variants-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateBucketInvalidNameVariantsRejected
{
    private const string InvalidName = "InvalidBucketName";
    private const string ExpectedMessage = "The specified bucket is not valid.";

    /// <summary>
    /// CreateBucket rejects uppercase, underscore, adjacent-dot, IP-shaped and 2-character names with
    /// InvalidBucketName and its exact message, while a valid name is created and is the only bucket
    /// listed under its prefix.
    /// </summary>
    [Test]
    [DisplayName("s3.create-bucket-invalid-name-variants-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var goodName = FidelityContext.UniqueName("sim2real-behav-s3bn-");
        await s3.PutBucketAsync(BucketParams(ctx, goodName));
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, goodName));
        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = goodName }); // independent read: the control bucket really exists

        await AssertRejected(ctx, s3, goodName.ToUpperInvariant(), "uppercase letters");
        await AssertRejected(ctx, s3, goodName + "_x", "an underscore");
        await AssertRejected(ctx, s3, goodName + "..x", "two adjacent dots");
        await AssertRejected(ctx, s3, "192.168.1.1", "an IP-address-shaped name");
        await AssertRejected(ctx, s3, "ab", "a name under 3 characters");

        // Effect absent: the underscore and dots variants share the control's prefix, so a listing
        // under it holds the control alone.
        var names = await S3CoreFidelity.ListBucketNames(s3, goodName);
        Ensure(names.SequenceEqual([goodName]), $"ListBuckets under the control's prefix read {Py.Repr(names)}, expected only it");
    }

    private static PutBucketRequest BucketParams(FidelityContext ctx, string name)
    {
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        return request;
    }

    private static async Task AssertRejected(FidelityContext ctx, IAmazonS3 s3, string name, string reason)
    {
        // Only does anything if the create below unexpectedly lands.
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, name));
        var err = await FidelityContext.CaptureError(() => s3.PutBucketAsync(BucketParams(ctx, name)))
            ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure(err.Code == InvalidName, $"{reason}: expected {Py.Repr(InvalidName)}, got {Py.Repr(err.Code)} for {Py.Repr(name)}");
        Ensure(err.Message == ExpectedMessage, $"{reason}: expected the exact message, got {Py.Repr(err.Message)}");
    }
}
