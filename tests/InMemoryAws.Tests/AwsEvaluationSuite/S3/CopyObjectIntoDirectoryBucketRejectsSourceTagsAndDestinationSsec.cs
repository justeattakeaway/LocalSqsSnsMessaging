// Translated from behavioral/s3/check_copy_object_into_directory_bucket_rejects_source_tags_and_destination_ssec.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-into-directory-bucket-rejects-source-tags-and-destination-ssec</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectIntoDirectoryBucketRejectsSourceTagsAndDestinationSsec
{
    private const string Check = "copy-object-into-directory-bucket-rejects-source-tags-and-destination-ssec";

    /// <summary>
    /// CopyObject into a directory bucket refuses a tagged source copied with TaggingDirective=COPY and a
    /// destination requesting SSE-C, both with their exact errors; an untagged, unencrypted source still
    /// copies in.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-into-directory-bucket-rejects-source-tags-and-destination-ssec")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (directory, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2real-s3x-copy-"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        var source = await S3Fidelity.GeneralPurposeBucket(ctx, s3, "sim2real-s3x-copy-src-", Check);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = source,
            Key = "tagged.txt",
            ContentBody = "tagged-bytes",
            TagSet = [new Tag { Key = "class", Value = "gold" }],
        });

        var result = await S3Fidelity.ErrorPair(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = source,
            SourceKey = "tagged.txt",
            DestinationBucket = directory,
            DestinationKey = "copy-tagged.txt",
        }), "expected a ClientError, but the call succeeded");
        Ensure(
            result == ("NotImplemented", "This bucket does not support copying source objects with Object Tags."),
            $"CopyObject of a tagged source (TaggingDirective=COPY) into a directory bucket returned {S3Fidelity.Repr(result)}");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = "plain.txt", ContentBody = "plain-bytes" });
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        result = await S3Fidelity.ErrorPair(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = source,
            SourceKey = "plain.txt",
            DestinationBucket = directory,
            DestinationKey = "copy-ssec.txt",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = Convert.ToBase64String(keyBytes),
            ServerSideEncryptionCustomerProvidedKeyMD5 = Convert.ToBase64String(S3Fidelity.Md5(keyBytes)),
        }), "expected a ClientError, but the call succeeded");
        Ensure(
            result == ("NotImplemented", "This bucket does not support server side encryption with a customer provided key."),
            $"CopyObject requesting destination SSE-C on a directory bucket returned {S3Fidelity.Repr(result)}");

        foreach (var refused in new[] { "copy-tagged.txt", "copy-ssec.txt" })
        {
            var absent = await S3Fidelity.HeadErrorPair(() => s3.GetObjectMetadataAsync(directory, refused), "expected a ClientError, but the call succeeded");
            Ensure(absent.Code == "404", $"the refused copy to {Py.Repr(refused)} must create no object, got {S3Fidelity.Repr(absent)}");
        }

        // Positive control: the same source bucket, an untagged and unencrypted object, copies in fine.
        var copied = await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = source,
            SourceKey = "plain.txt",
            DestinationBucket = directory,
            DestinationKey = "copy-plain.txt",
        });
        Ensure(
            (int)copied.HttpStatusCode == 200,
            $"the untagged, unencrypted control copy returned HTTP {(int)copied.HttpStatusCode}, expected 200");
        var body = await S3Fidelity.GetBytes(s3, directory, "copy-plain.txt");
        Ensure(body.SequenceEqual("plain-bytes"u8.ToArray()), $"the control copy holds {Py.Repr(body)}, want b'plain-bytes'");
    }
}
