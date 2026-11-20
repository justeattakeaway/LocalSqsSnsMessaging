// Translated from behavioral/s3/check_requester_pays_enforcement.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.requester-pays-enforcement</summary>
[Category("AwsEvaluationSuite")]
public sealed class RequesterPaysEnforcement
{
    /// <summary>
    /// With Payer=Requester, an unsigned GetObject without x-amz-request-payer fails with
    /// AccessDenied and S3's exact anonymous-requester message; after Payer is reset to BucketOwner
    /// the same call (allowed by a secret-Referer grant) returns the object's bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.requester-pays-enforcement")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "payer.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "large-shared-dataset" });

        // A per-run secret Referer keeps the public grant below usable by this check alone.
        var referer = S3Fidelity.TokenUrlSafe();
        try
        {
            await S3Fidelity.GrantPublicRead(s3, bucket, "PublicReadForRequesterPaysProbe", [$"arn:aws:s3:::{bucket}/{key}"], referer);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "AccessDenied" or "AccessDeniedByBlockPublicPolicy")
        {
            throw Fail("account-level Block Public Access forbids the bucket policy this check requires");
        }

        try
        {
            await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
            {
                BucketName = bucket,
                RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "Requester" },
            });
            var payer = (await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket })).Payer;
            Ensure(payer == "Requester", $"expected Payer=Requester after PutBucketRequestPayment, got {payer}");

            var refusal = await S3Fidelity.PollUntil(
                () => AnonymousGet(ctx, bucket, key, referer),
                r => r.Error?.Code == "AccessDenied",
                TimeSpan.FromSeconds(30.0),
                TimeSpan.FromSeconds(1.0),
                r => $"anonymous GetObject denied for a Requester-Pays bucket without a RequestPayer ack not observed within 30.0s; last saw {r}");
            var want = ("AccessDenied", "Anonymous users cannot invoke requests against Requester Pays buckets. Please authenticate.");
            Ensure(
                (refusal.Error!.Code, refusal.Error.Message) == want,
                $"expected AccessDenied for GetObject without RequestPayer ack, got {refusal}");

            await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
            {
                BucketName = bucket,
                RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "BucketOwner" },
            });
            var resp = await S3Fidelity.PollUntil(
                () => AnonymousGet(ctx, bucket, key, referer),
                r => r.Error is null,
                TimeSpan.FromSeconds(30.0),
                TimeSpan.FromSeconds(1.0),
                r => $"anonymous GetObject succeeding again after RequestPayment reset to BucketOwner not observed within 30.0s; last saw {r}");
            var body = Encoding.UTF8.GetString(resp.Body!);
            Ensure(body == "large-shared-dataset", $"expected the object body back, got {Py.Repr(body)}");
        }
        finally
        {
            await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        }
    }

    /// <summary>An unsigned GetObject carrying the Referer: the body, or the error.</summary>
    private static async Task<Outcome> AnonymousGet(FidelityContext ctx, string bucket, string key, string referer)
    {
        var (response, error) = await FidelityContext.Capture(() => S3Fidelity.AnonymousClient(ctx, referer).GetObjectAsync(bucket, key));
        if (response is null)
        {
            return new Outcome(null, error);
        }

        using (response)
        {
            return new Outcome(await S3Fidelity.ReadBody(response), null);
        }
    }

    private sealed record Outcome(byte[]? Body, AwsError? Error);
}
