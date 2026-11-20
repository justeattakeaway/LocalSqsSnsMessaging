// Translated from behavioral/s3/check_presigned_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.presigned-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class PresignedPut
{
    /// <summary>
    /// A presigned PUT URL allows uploading an object without credentials.
    /// </summary>
    [Test]
    [DisplayName("s3.presigned-put")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string contentType = "text/plain";
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = "presigned-upload.txt",
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.AddSeconds(300),
        });
        var uploadBody = "uploaded-via-presigned-put"u8.ToArray();

        // The URL is one the target itself just generated, so a target that will not accept the upload
        // is a fidelity failure.
        var (status, _, answer) = await S3Fidelity.Send(ctx, "PUT", url, new Dictionary<string, string> { ["Content-Type"] = contentType }, uploadBody);
        Ensure(status == 200, $"{DataplaneMarker}: presigned PUT URL did not accept the upload: HTTP Error {status}: {S3Fidelity.Head(answer)}");

        // Verify the object landed
        var got = await S3Fidelity.GetBytes(s3, bucket, "presigned-upload.txt");
        Ensure(got.SequenceEqual(uploadBody), $"object body after presigned PUT was {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(uploadBody)}");
    }

    /// <summary>An endpoint data-plane FAIL marker: a detail containing it is a fidelity failure.</summary>
    private const string DataplaneMarker = "DATAPLANE_UNIMPLEMENTED";
}
