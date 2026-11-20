// Translated from behavioral/s3/check_deleting_the_only_object_under_a_nested_prefix_removes_its_implied_directories.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deleting-the-only-object-under-a-nested-prefix-removes-its-implied-directories</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletingTheOnlyObjectUnderANestedPrefixRemovesItsImpliedDirectories
{
    private const string Check = "deleting-the-only-object-under-a-nested-prefix-removes-its-implied-directories";
    private const string NestedKey = "a/b/c.txt";
    private const string SiblingKey = "z/top.txt";

    /// <summary>
    /// Deleting the only object two levels under a prefix removes every level that prefix implied: a
    /// Delimiter='/' listing of the bucket root no longer names the top-level prefix at all.
    /// </summary>
    [Test]
    [DisplayName("s3.deleting-the-only-object-under-a-nested-prefix-removes-its-implied-directories")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3x-impliedir-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = NestedKey, ContentBody = "nested" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = SiblingKey, ContentBody = "sibling" });

        var beforeRoot = await List(s3, bucket, null);
        Ensure(Prefixes(beforeRoot).SequenceEqual(["a/", "z/"]), $"before the delete, the root listing must roll up to ['a/', 'z/']; got {Py.Repr(Prefixes(beforeRoot))}");
        var beforeA = await List(s3, bucket, "a/");
        Ensure(Prefixes(beforeA).SequenceEqual(["a/b/"]), $"before the delete, a/ must roll up to ['a/b/']; got {Py.Repr(Prefixes(beforeA))}");

        await s3.DeleteObjectAsync(bucket, NestedKey);

        var afterRoot = await List(s3, bucket, null);
        Ensure(
            Prefixes(afterRoot).SequenceEqual(["z/"]),
            $"after deleting the only object under a/b/, the root listing must drop a/ entirely (nothing implies it any more); got {Py.Repr(Prefixes(afterRoot))}");
        Ensure(Keys(afterRoot).Count == 0, $"the root listing must still have no keys of its own; got {Py.Repr(Keys(afterRoot))}");

        var afterA = await List(s3, bucket, "a/");
        Ensure(
            Prefixes(afterA).Count == 0 && Keys(afterA).Count == 0,
            $"nothing must remain under a/ once its only object is gone; got prefixes={Py.Repr(Prefixes(afterA))}, keys={Py.Repr(Keys(afterA))}");

        var error = await FidelityContext.CaptureError(() => s3.GetObjectAsync(bucket, NestedKey))
            ?? throw Fail("expected the call to be refused, but it succeeded");
        Ensure(
            (error.Code, error.Message) == ("NoSuchKey", "The specified key does not exist."),
            $"GetObject on the deleted key returned ({Py.Repr(error.Code)}, {Py.Repr(error.Message)})");

        var siblingBody = await S3Fidelity.GetBytes(s3, bucket, SiblingKey);
        Ensure(siblingBody.AsSpan().SequenceEqual("sibling"u8), $"the untouched sibling now holds {Py.Repr(siblingBody)}");
    }

    private static Task<ListObjectsV2Response> List(IAmazonS3 s3, string bucket, string? prefix) =>
        s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, Delimiter = "/" });

    private static List<string> Prefixes(ListObjectsV2Response resp) => resp.CommonPrefixes ?? [];

    private static List<string> Keys(ListObjectsV2Response resp) => [.. (resp.S3Objects ?? []).Select(o => o.Key)];
}
