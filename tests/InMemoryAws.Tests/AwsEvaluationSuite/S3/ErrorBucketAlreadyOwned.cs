// Translated from behavioral/s3/check_error_bucket_already_owned.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.error-bucket-already-owned</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorBucketAlreadyOwned
{
    private const string ErrBucketAlreadyOwned = "BucketAlreadyOwnedByYou";
    private const string AlreadyOwnedMessage = "Your previous request to create the named bucket succeeded and you already own it.";

    /// <summary>
    /// Re-creating an existing bucket you own returns BucketAlreadyOwnedByYou, and the bucket
    /// itself is untouched by the rejected attempt — not silently deleted and recreated.
    /// </summary>
    [Test]
    [DisplayName("s3.error-bucket-already-owned")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var before = await CreationDate(s3, bucket);
        Ensure(before is not null, $"bucket {Py.Repr(bucket)} missing from list_buckets right after create");

        PutBucketRequest CreateKwargs()
        {
            var request = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };
            if (ctx.Region != "us-east-1")
            {
                request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
            }

            return request;
        }

        // On real AWS in us-east-1, re-creating your own bucket succeeds (200).
        // BucketAlreadyOwnedByYou is returned in other regions.
        if (ctx.Region == "us-east-1")
        {
            if (await FidelityContext.CaptureError(() => s3.PutBucketAsync(CreateKwargs())) is { } exc)
            {
                Ensure(exc.Code == ErrBucketAlreadyOwned, $"expected success or {ErrBucketAlreadyOwned}, got {exc}");
                Ensure(exc.Message == AlreadyOwnedMessage, exc.Message ?? "None");
            }
        }
        else
        {
            var err = await Err(() => s3.PutBucketAsync(CreateKwargs()), "re-creating a bucket this account owns");
            Ensure(err.Code == ErrBucketAlreadyOwned, $"expected {ErrBucketAlreadyOwned}, got {err}");
            Ensure(err.Message == AlreadyOwnedMessage, err.Message ?? "None");
        }

        // Independent read at a different operation (list_buckets, not create_bucket): the bucket
        // must still be there. list_buckets' CreationDate is not a stable identity signal (it can
        // differ by a second between calls), so only existence is asserted.
        var after = await CreationDate(s3, bucket);
        Ensure(after is not null, $"bucket {Py.Repr(bucket)} vanished from list_buckets after the re-create attempt");

        // Restore leg: delete the bucket, then the identical create call must now succeed — proving
        // the rejection was really "you already own this name", not a blanket ban on a second
        // CreateBucket call for this session.
        await s3.DeleteBucketAsync(bucket);
        // HeadBucket answers with no body, so its 404 status is the whole refusal to grade
        var gone = await Err(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }), "HeadBucket of the deleted bucket");
        Ensure(S3CoreFidelity.IsBareHead404(gone), $"expected head_bucket 404 after delete, got {Py.Repr(gone.Code)}");
        if (await FidelityContext.CaptureError(() => s3.PutBucketAsync(CreateKwargs())) is { } recreate)
        {
            throw Fail($"expected the identical create_bucket to succeed once the bucket was deleted, got {recreate}");
        }
    }

    /// <summary>Call fn(); return the botocore Error dict it raised. A success raises AssertionError.</summary>
    private static async Task<AwsError> Err(Func<Task> fn, string what) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail($"{what} succeeded; expected a refusal");

    /// <summary>list_buckets entry for `bucket`, or None if it is not there — an independent read.</summary>
    private static async Task<DateTime?> CreationDate(IAmazonS3 s3, string bucket)
    {
        var buckets = (await s3.ListBucketsAsync(new ListBucketsRequest())).Buckets ?? [];
        return buckets.FirstOrDefault(b => b.BucketName == bucket)?.CreationDate;
    }
}
