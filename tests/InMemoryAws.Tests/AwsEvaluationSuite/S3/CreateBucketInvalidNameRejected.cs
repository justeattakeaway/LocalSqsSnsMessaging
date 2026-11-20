// Translated from behavioral/s3/check_create_bucket_invalid_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-bucket-invalid-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateBucketInvalidNameRejected
{
    private const string InvalidName = "InvalidBucketName";

    /// <summary>
    /// CreateBucket with a name over 63 characters is rejected with 400 InvalidBucketName, its exact
    /// message and the BucketName, and is not listed, while a valid name is created and listed.
    /// </summary>
    [Test]
    [DisplayName("s3.create-bucket-invalid-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var goodName = FidelityContext.UniqueName("sim2real-behav-s3fid-");
        Ensure(goodName.Length <= 63, "assert len(good_name) <= 63");
        await s3.PutBucketAsync(BucketParams(ctx, goodName));
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, goodName));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = goodName,
            TagSet = [new Tag { Key = "sim2real", Value = "create-bucket-invalid-name-rejected" }],
        });
        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = goodName }); // independent read: the control bucket really exists

        var tooLong = goodName + new string('z', 64);
        Ensure(tooLong.Length > 63, "assert len(too_long) > 63");
        // Only does anything if the create below unexpectedly lands.
        ctx.OnCleanup(() => DeleteBucket(s3, tooLong));
        var refusal = await Error(() => s3.PutBucketAsync(BucketParams(ctx, tooLong)));
        var status = (int)refusal.StatusCode;
        Ensure(refusal.Code == InvalidName, $"expected {Py.Repr(InvalidName)} for a 64+ char bucket name, got {Py.Repr(refusal.Code)}");
        Ensure(status == 400, $"expected HTTP 400 for {InvalidName}, got {status}");
        Ensure(refusal.Message == "The specified bucket is not valid.", $"expected the exact {InvalidName} message, got {refusal}");
        // The .NET SDK keeps no members of the error document beyond Code and Message: the same
        // CreateBucket, sent over raw HTTP, shows its BucketName.
        var configuration = ctx.Region == "us-east-1"
            ? null
            : $"<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>{ctx.Region}</LocationConstraint></CreateBucketConfiguration>";
        var bucketName = await S3CoreFidelity.RawErrorDetail(ctx, HttpMethod.Put, tooLong, configuration, "BucketName");
        Ensure(
            bucketName == tooLong,
            $"expected Error.BucketName to be the supplied {tooLong.Length}-char name, got {Py.Repr(bucketName)}");

        // Nothing was created: HeadBucket on the refused name is refused (a bodiless 4xx: the name
        // is invalid as well as absent), and ListBuckets omits it.
        var head = await Error(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = tooLong }));
        var headStatus = (int)head.StatusCode;
        Ensure(headStatus is 400 or 404, $"expected HeadBucket on the refused name to answer 400 or 404, got {headStatus}");
        var names = (await s3.ListBucketsAsync(new ListBucketsRequest { Prefix = goodName })).Buckets?.Select(b => b.BucketName).ToList() ?? [];
        Ensure(!names.Contains(tooLong), "the refused bucket name is listed by ListBuckets");
        Ensure(names.Contains(goodName), $"the control bucket is missing from ListBuckets: {Py.Repr(names)}");
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

    /// <summary>Call fn(); return the ClientError response it raises. A call that SUCCEEDS fails the check.</summary>
    private static async Task<AwsError> Error(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");

    /// <summary>
    /// Teardown: empty then delete. Only a bucket that never existed (NoSuchBucket, or
    /// InvalidBucketName for the over-long name) is tolerated; every other error raises.
    /// </summary>
    private static async Task DeleteBucket(IAmazonS3 s3, string bucket)
    {
        try
        {
            await S3Fidelity.EmptyAndDelete(s3, bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "InvalidBucketName")
        {
        }
    }
}
