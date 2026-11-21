// Translated from behavioral/s3/check_put_object_metadata_exceeding_two_kib_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-metadata-exceeding-two-kib-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectMetadataExceedingTwoKibRejected
{
    private const string Check = "put-object-metadata-exceeding-two-kib-rejected";

    /// <summary>AWS limits the user-defined metadata (header names + values together) on an object to 2 KiB.</summary>
    private static readonly string OverLimitValue = new('v', 3000);

    private static readonly (string Code, string Message) MetadataTooLarge = ("MetadataTooLarge", "Your metadata headers exceed the maximum allowed metadata size");
    private static readonly (string Code, string Message) NotFound = ("404", "Not Found");

    /// <summary>
    /// PutObject rejects user-defined metadata over 2 KiB and leaves the key absent; a small
    /// metadata set on the same key succeeds and is readable back from HeadObject.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-metadata-exceeding-two-kib-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-metasz-");
        const string key = "lifecycle/metadata-target.bin";

        var big = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" };
        big.Metadata.Add("sim2real-big", OverLimitValue);
        var error = await S3Fidelity.ErrorPair(() => s3.PutObjectAsync(big), $"expected ({MetadataTooLarge.Code}, {Py.Repr(MetadataTooLarge.Message)}), but the call succeeded");
        Ensure(error.Code == MetadataTooLarge.Code, $"expected code {Py.Repr(MetadataTooLarge.Code)}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == MetadataTooLarge.Message, $"expected message {Py.Repr(MetadataTooLarge.Message)}, got {Py.Repr(error.Message)}");

        var headError = await S3Fidelity.HeadErrorPair(() => s3.GetObjectMetadataAsync(bucket, key), $"expected ({NotFound.Code}, {Py.Repr(NotFound.Message)}), but the call succeeded");
        Ensure(headError.Code == NotFound.Code, $"expected code {Py.Repr(NotFound.Code)}, got {Py.Repr(headError.Code)}");
        Ensure(headError.Message == NotFound.Message, $"expected message {Py.Repr(NotFound.Message)}, got {Py.Repr(headError.Message)}");

        // --- Control: metadata well under the limit, on the same key, succeeds.
        var small = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" };
        small.Metadata.Add("sim2real-small", "ok");
        await s3.PutObjectAsync(small);
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var metadata = S3Fidelity.UserMetadata(head.Metadata);
        Ensure(
            metadata.GetValueOrDefault("sim2real-small") == "ok",
            $"HeadObject metadata {Py.Repr(metadata)} must carry the small value that was put");
    }
}
