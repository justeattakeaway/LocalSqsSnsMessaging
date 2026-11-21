// Translated from behavioral/s3/check_get_object_attributes_on_a_permanently_deleted_version_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-on-a-permanently-deleted-version-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesOnAPermanentlyDeletedVersionRejected
{
    private const string Check = "get-object-attributes-on-a-permanently-deleted-version-rejected";
    private const string Key = "ledger/entry.bin";
    private const string V1Body = "entry one";
    private const string V2Body = "entry two, revised";

    /// <summary>
    /// GetObjectAttributes on a version id that was permanently deleted is refused NoSuchVersion,
    /// not NoSuchKey, while the key's current (newer) version still answers.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-on-a-permanently-deleted-version-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-goadelv-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);

        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = V1Body })).VersionId;
        var put2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = V2Body });
        var v2 = put2.VersionId;

        // Naming a VersionId on DeleteObject erases exactly that version outright: no delete marker,
        // no new version — the version id is simply gone from here on.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = Key, VersionId = v1 });

        var error = await FidelityContext.CaptureError(() => s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = Key,
            VersionId = v1,
            ObjectAttributes = [ObjectAttributes.ETag, ObjectAttributes.ObjectSize],
        })) ?? throw Fail("expected (NoSuchVersion, 'The specified version does not exist.'), but the call succeeded");
        Ensure(error.Code == "NoSuchVersion", $"expected code 'NoSuchVersion', got {Py.Repr(error.Code)}");
        Ensure(
            error.Message == "The specified version does not exist.",
            $"expected message 'The specified version does not exist.', got {Py.Repr(error.Message)}");

        // --- Positive control: the key's current (newer) version still answers, and its
        // GetObjectAttributes ETag is the PutObject ETag with the surrounding quotes stripped.
        var current = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = Key,
            VersionId = v2,
            ObjectAttributes = [ObjectAttributes.ETag, ObjectAttributes.ObjectSize],
        });
        Ensure(
            current.ETag == put2.ETag.Trim('"'),
            $"GetObjectAttributes ETag must be the PutObject ETag {Py.Repr(put2.ETag)} with the quotes stripped; got {Py.Repr(current.ETag)}");
        Ensure(
            current.ObjectSize == V2Body.Length,
            $"the surviving version's size must match its {V2Body.Length}-byte body; got {Py.Repr(current.ObjectSize)}");
    }
}
