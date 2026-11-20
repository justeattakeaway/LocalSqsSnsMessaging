// Translated from behavioral/s3/check_copy_object_from_specific_version_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-from-specific-version-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectFromSpecificVersionSource
{
    private const string NoSuchVersionMsg = "The specified version does not exist.";

    /// <summary>
    /// CopyObject with a versioned CopySource copies THAT version, not the current one.
    /// A CopySource carrying ?versionId=&lt;old&gt; is how a caller restores history, and the response echoes
    /// x-amz-copy-source-version-id; copying the latest version instead silently undoes a rollback.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-from-specific-version-source")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var src = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var dst = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = src, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        const string key = "history.txt";

        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = src, Key = key, ContentBody = "one" })).VersionId;
        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = src, Key = key, ContentBody = "two" })).VersionId;
        var v3 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = src, Key = key, ContentBody = "three" })).VersionId;
        Ensure(new[] { v1, v2, v3 }.Distinct(StringComparer.Ordinal).Count() == 3, $"expected three distinct version ids, got {Py.Repr(new[] { v1, v2, v3 })}");
        Ensure(await Text(s3, src, key) == "three", "v3 must be current");

        // --- The behavior under test: copy the OLDEST version while a newer one is current.
        var copied = await s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = dst, DestinationKey = "restored-v1.txt", SourceBucket = src, SourceKey = key, SourceVersionId = v1 });
        Ensure(
            copied.SourceVersionId == v1,
            $"CopyObject must echo CopySourceVersionId={Py.Repr(v1)} so the caller can confirm which version was read, got {Py.Repr(copied.SourceVersionId)}");
        var body = await Text(s3, dst, "restored-v1.txt");
        Ensure(body == "one", $"copying CopySource versionId={v1} must yield b'one'; got {Py.Repr(body)} — the versionId was ignored and the current version was copied instead");

        // --- The middle version resolves independently, so this is not an off-by-one that happens to
        // land on the first version.
        var copied2 = await s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = dst, DestinationKey = "restored-v2.txt", SourceBucket = src, SourceKey = key, SourceVersionId = v2 });
        Ensure(copied2.SourceVersionId == v2, $"CopyObject must echo CopySourceVersionId={Py.Repr(v2)} for the middle version, got {Py.Repr(copied2.SourceVersionId)}");
        Ensure(await Text(s3, dst, "restored-v2.txt") == "two", "copying the middle version must yield b'two'");

        // --- Control: no versionId means the current version.
        var copied3 = await s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = dst, DestinationKey = "restored-current.txt", SourceBucket = src, SourceKey = key });
        Ensure(copied3.SourceVersionId == v3, $"an unversioned CopySource must resolve to the current version {Py.Repr(v3)}, got {Py.Repr(copied3.SourceVersionId)}");
        var currentBody = await Text(s3, dst, "restored-current.txt");
        Ensure(currentBody == "three", $"copying an unversioned CopySource must yield b'three' (the current version); got {Py.Repr(currentBody)}");

        // --- Break exactly one thing: a well-formed source version id that no longer exists.
        // The CopySource resolution path is a different code path from GetObject in every
        // implementation.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = src, Key = key, VersionId = v2 });
        var exc = await S3Fidelity.ExpectError(
            () => s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = dst, DestinationKey = "restored-gone.txt", SourceBucket = src, SourceKey = key, SourceVersionId = v2 }),
            $"copying from the permanently deleted version {v2} succeeded; the source version id is not being resolved");
        Ensure(exc.Code == "NoSuchVersion", $"expected NoSuchVersion for a deleted CopySource version, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == NoSuchVersionMsg, $"expected message {Py.Repr(NoSuchVersionMsg)}, got {Py.Repr(exc.Message)}");

        // --- Effect absent: the failed copy created no destination object.
        await S3Fidelity.ExpectError(() => s3.GetObjectMetadataAsync(dst, "restored-gone.txt"), "the failed versioned copy created a destination object anyway");
        var listed = (await S3Fidelity.ListedObjects(s3, dst)).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(
            listed.SetEquals(["restored-v1.txt", "restored-v2.txt", "restored-current.txt"]),
            $"unexpected destination contents: {Py.Repr(listed.Order(StringComparer.Ordinal).ToList())}");

        // --- Effect absent on the source, and control still works: deleting v2 and the failed copy
        // left v1 and v3 alone.
        using (var v1Object = await s3.GetObjectAsync(new GetObjectRequest { BucketName = src, Key = key, VersionId = v1 }))
        {
            var v1Body = Encoding.UTF8.GetString(await S3Fidelity.ReadBody(v1Object));
            Ensure(v1Body == "one", $"deleting v2 must not disturb v1's bytes; expected b'one', got {Py.Repr(v1Body)}");
        }

        var srcCurrentBody = await Text(s3, src, key);
        Ensure(srcCurrentBody == "three", $"deleting v2 must not disturb the current version's bytes; expected b'three', got {Py.Repr(srcCurrentBody)}");
        Ensure(await Text(s3, dst, "restored-v2.txt") == "two", "an already-copied object must survive the deletion of its source version");
    }

    private static async Task<string> Text(IAmazonS3 s3, string bucket, string key) =>
        Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
}
