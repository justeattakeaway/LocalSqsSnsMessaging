// Translated from behavioral/s3/check_presigned_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.presigned-get</summary>
[Category("AwsEvaluationSuite")]
public sealed class PresignedGet
{
    /// <summary>
    /// A presigned GET URL returns the object content without credentials.
    /// </summary>
    [Test]
    [DisplayName("s3.presigned-get")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var body = "presigned-test-content-sim2real"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "presigned.txt", InputStream = new MemoryStream(body) });
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = "presigned.txt",
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddSeconds(300),
        });

        // Fetch without any AWS credentials — the signature is in the URL.
        // The URL is one the target itself just generated, so a target that will not serve it is a
        // fidelity failure.
        var (status, _, got) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"{DataplaneMarker}: presigned GET URL did not serve the object: HTTP Error {status}: {S3Fidelity.Head(got)}");
        Ensure(got.SequenceEqual(body), $"presigned GET returned {got.Length} bytes, expected {body.Length}");
        Ensure(status == 200, $"presigned GET returned status {status}, expected 200");
    }

    /// <summary>An endpoint data-plane FAIL marker: a detail containing it is a fidelity failure.</summary>
    private const string DataplaneMarker = "DATAPLANE_UNIMPLEMENTED";
}
