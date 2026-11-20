// Translated from behavioral/s3/check_deep_s3_immutability_requester_pays_anonymous_with_payer_header_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-requester-pays-anonymous-with-payer-header-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityRequesterPaysAnonymousWithPayerHeaderRejected
{
    private const string Check = "deep-s3-immutability-requester-pays-anonymous-with-payer-header-rejected";
    private const string Body = "public-dataset-bytes";
    private const string Key = "dataset.bin";
    private static readonly TimeSpan Converge = TimeSpan.FromSeconds(60.0);

    /// <summary>
    /// On a Requester-Pays bucket an unsigned GetObject with x-amz-request-payer fails with AccessDenied
    /// and S3's exact anonymous-requester message, while the same request (allowed by a secret-Referer
    /// grant) returns the bytes under Payer=BucketOwner before and after.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-requester-pays-anonymous-with-payer-header-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-imm-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = Body });

        // Only requests carrying this per-run secret as Referer may read the object anonymously.
        var referer = S3Fidelity.TokenUrlSafe();
        await S3Fidelity.GrantPublicRead(s3, bucket, "PublicReadForRequesterPaysProbe", [$"arn:aws:s3:::{bucket}/{Key}"], referer);
        ctx.OnCleanup(() => s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket }));

        Ensure(
            (await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket })).Payer == "BucketOwner",
            "a new bucket's Payer is not the default BucketOwner");
        var got = await Poll(() => AnonymousGet(ctx, bucket, referer), r => r.Body == Body,
            "anonymous GET succeeding under Payer=BucketOwner with a public-read policy");
        Ensure(got.Body == Body, $"anonymous GET under BucketOwner returned {got}");

        await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = bucket,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "Requester" },
        });
        ctx.OnCleanup(() => s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = bucket,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "BucketOwner" },
        }));
        Ensure(
            (await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket })).Payer == "Requester",
            "PutBucketRequestPayment(Requester) did not read back");

        var refused = await Poll(() => AnonymousGet(ctx, bucket, referer), r => r.Body is null,
            "anonymous GET refused on a Requester-Pays bucket");
        Ensure(
            (refused.Error?.Code, refused.Error?.Message) == ("AccessDenied", "Anonymous users cannot invoke requests against Requester Pays buckets. Please authenticate."),
            "expected AccessDenied for an anonymous GET with x-amz-request-payer on a Requester-Pays " +
            $"bucket, got {refused}");

        var owner = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, Key));
        Ensure(owner == Body, $"the bucket owner's header-less GET returned {Py.Repr(owner)}");

        await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = bucket,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "BucketOwner" },
        });
        var again = await Poll(() => AnonymousGet(ctx, bucket, referer), r => r.Body == Body,
            "anonymous GET succeeding again after Payer reverted to BucketOwner");
        Ensure(again.Body == Body, $"anonymous GET after the revert returned {again}");
    }

    /// <summary>Anonymous GET WITH x-amz-request-payer: the body, or the (code, message).</summary>
    private static async Task<Outcome> AnonymousGet(FidelityContext ctx, string bucket, string referer)
    {
        var (response, error) = await FidelityContext.Capture(() => S3Fidelity.AnonymousClient(ctx, referer).GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = Key,
            RequestPayer = RequestPayer.Requester,
        }));
        if (response is null)
        {
            return new Outcome(null, error);
        }

        using (response)
        {
            return new Outcome(Encoding.UTF8.GetString(await S3Fidelity.ReadBody(response)), null);
        }
    }

    /// <summary>Poll for a convergence and assert it ARRIVES, never how fast.</summary>
    private static async Task<Outcome> Poll(Func<Task<Outcome>> probe, Func<Outcome, bool> done, string description)
    {
        var clock = FidelityTime.StartNew();
        var last = await probe();
        while (!done(last))
        {
            Ensure(clock.Elapsed < Converge, $"{description} not observed within {Converge.TotalSeconds}s; last saw {last}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            last = await probe();
        }

        return last;
    }

    private sealed record Outcome(string? Body, AwsError? Error);
}
