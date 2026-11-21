// Translated from behavioral/s3/check_bucket_request_payment_not_found_after_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-request-payment-not-found-after-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketRequestPaymentNotFoundAfterDelete
{
    /// <summary>
    /// GetBucketRequestPayment and PutBucketRequestPayment both reject a deleted bucket with
    /// NoSuchBucket, after succeeding against that same bucket while it existed.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-request-payment-not-found-after-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-reqpay-del-");

        // positive control: both calls succeed while the bucket exists
        await s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = name,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "Requester" },
        });
        var before = await s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = name });
        Ensure(before.Payer == "Requester", $"expected Payer Requester before delete, got {Py.Repr(before.Payer)}");

        await s3.DeleteBucketAsync(name);

        var (code, message) = await S3Fidelity.ErrorPair(() => s3.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = name }));
        Ensure(code == "NoSuchBucket", $"expected NoSuchBucket from GetBucketRequestPayment, got {Py.Repr(code)}");
        Ensure(message == "The specified bucket does not exist", $"unexpected message {Py.Repr(message)}");

        var (code2, message2) = await S3Fidelity.ErrorPair(() => s3.PutBucketRequestPaymentAsync(new PutBucketRequestPaymentRequest
        {
            BucketName = name,
            RequestPaymentConfiguration = new RequestPaymentConfiguration { Payer = "BucketOwner" },
        }));
        Ensure(code2 == "NoSuchBucket", $"expected NoSuchBucket from PutBucketRequestPayment, got {Py.Repr(code2)}");
        Ensure(message2 == "The specified bucket does not exist", $"unexpected message {Py.Repr(message2)}");
    }
}
