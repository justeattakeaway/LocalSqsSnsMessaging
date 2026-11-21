// Translated from behavioral/s3/check_bucket_request_payment_invalid_payer_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-request-payment-invalid-payer-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketRequestPaymentInvalidPayerRejected
{
    /// <summary>
    /// PutBucketRequestPayment rejects a Payer outside Requester/BucketOwner with MalformedXML,
    /// leaving the bucket's existing Payer setting unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-request-payment-invalid-payer-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-reqpay-");

        // positive control: a legal Payer value is accepted and reflected back
        await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = bucket,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "Requester" },
        });
        var before = await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket });
        Ensure(before.Payer == "Requester", $"expected Payer Requester after a legal put, got {Py.Repr(before.Payer)}");

        var (code, message) = await S3Fidelity.ErrorPair(() => s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = bucket,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "Owner" },
        }));
        Ensure(code == "MalformedXML", $"expected MalformedXML, got {Py.Repr(code)}");
        Ensure(
            message == "The XML you provided was not well-formed or did not validate against our published schema",
            $"unexpected message {Py.Repr(message)}");

        var after = await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket });
        Ensure(after.Payer == "Requester", $"the rejected put must leave the prior Payer setting intact, got {Py.Repr(after.Payer)}");
    }
}
