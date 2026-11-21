// Translated from behavioral/s3/check_copy_object_unrecognized_checksum_algorithm_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-unrecognized-checksum-algorithm-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectUnrecognizedChecksumAlgorithmRejected
{
    private const string Body = "the checksum algorithm on this copy is not one of the ones s3 supports";

    private static readonly (string? Code, string? Message) UnsupportedAlgorithm = (
        "InvalidRequest",
        "Checksum algorithm provided is unsupported. Please try again with any of the valid types: [CRC32, CRC32C, CRC64NVME, MD5, SHA1, SHA256, SHA512, XXHASH128, XXHASH3, XXHASH64]");

    /// <summary>
    /// CopyObject with a ChecksumAlgorithm value outside the supported set is rejected with
    /// InvalidRequest naming the valid types, and creates no destination object.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-unrecognized-checksum-algorithm-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-cpuca-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "src.bin", ContentBody = Body });

        var got = await S3Fidelity.ErrorPair(
            () => s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = "src.bin",
                DestinationBucket = bucket,
                DestinationKey = "dst-bad.bin",
                ChecksumAlgorithm = new ChecksumAlgorithm("SHA3-256"),
            }),
            $"expected {S3Fidelity.Repr(UnsupportedAlgorithm)}, but the call succeeded");
        Ensure(got == UnsupportedAlgorithm, $"expected {S3Fidelity.Repr(UnsupportedAlgorithm)}, got {S3Fidelity.Repr(got)}");

        var keys = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(keys.SequenceEqual(["src.bin"]), $"the bucket holds {Py.Repr(keys)}; the refused copy must not have created a destination object");

        // Positive control: the same copy with a supported algorithm succeeds.
        var control = await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = "src.bin",
            DestinationBucket = bucket,
            DestinationKey = "dst-ok.bin",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        });
        Ensure(control.ChecksumType?.Value == "FULL_OBJECT", $"the control copy reported ChecksumType {Py.Repr(control.ChecksumType?.Value)}");
        var body = await S3Fidelity.GetBytes(s3, bucket, "dst-ok.bin");
        Ensure(body.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(Body)), "the control copy's bytes differ from the source's bytes");
    }
}
