// Translated from behavioral/s3/check_bucket_request_payment_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-request-payment-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketRequestPaymentRoundtrip
{
    /// <summary>
    /// PutBucketRequestPayment toggles Requester between BucketOwner and Requester;
    /// GetBucketRequestPayment reflects the current setting at every step.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-request-payment-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-reqpay-");
        var initial = await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = name });
        Ensure(initial.Payer == "BucketOwner", $"expected Payer BucketOwner by default, got {Py.Repr(initial.Payer)}");
        await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = name,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "Requester" },
        });
        var got = await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = name });
        Ensure(got.Payer == "Requester", $"expected Payer Requester after put, got {Py.Repr(got.Payer)}");
        await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = name,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "BucketOwner" },
        });
        var got2 = await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = name });
        Ensure(got2.Payer == "BucketOwner", $"expected Payer BucketOwner after second put, got {Py.Repr(got2.Payer)}");
    }
}
