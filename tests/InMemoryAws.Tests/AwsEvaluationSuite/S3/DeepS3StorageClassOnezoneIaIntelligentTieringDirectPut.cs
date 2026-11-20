// Translated from behavioral/s3/check_deep_s3_storage_class_onezone_ia_intelligent_tiering_direct_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-onezone-ia-intelligent-tiering-direct-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassOnezoneIaIntelligentTieringDirectPut
{
    /// <summary>
    /// Directly PUT ONEZONE_IA and INTELLIGENT_TIERING objects report their class on Head, List and
    /// Get with bytes intact; an in-place class-only copy changes the class, keeping bytes and ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-onezone-ia-intelligent-tiering-direct-put")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-oz-");
        var objects = new Dictionary<string, (string Class, byte[] Body)>
        {
            ["classes/onezone.bin"] = ("ONEZONE_IA", "one-zone-infrequent-access"u8.ToArray()),
            ["classes/tiering.bin"] = ("INTELLIGENT_TIERING", "intelligent-tiering-bytes"u8.ToArray()),
        };
        var etags = new Dictionary<string, string>();
        foreach (var (key, (sc, body)) in objects)
        {
            etags[key] = (await S3Fidelity.PutWithClass(s3, bucket, key, body, sc)).ETag;
        }

        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "classes/" })).S3Objects ?? []).ToDictionary(o => o.Key);
        Ensure(listed.Keys.ToHashSet().SetEquals(objects.Keys), $"ListObjectsV2 under classes/ returned {Py.Repr(listed.Keys.Order(StringComparer.Ordinal).ToList())}");
        foreach (var (key, (sc, body)) in objects)
        {
            var head = await s3.GetObjectMetadataAsync(bucket, key);
            Ensure(head.StorageClass?.Value == sc, $"HeadObject {key}: StorageClass {Py.Repr(head.StorageClass?.Value)}, expected {Py.Repr(sc)}");
            Ensure(listed[key].StorageClass?.Value == sc, $"ListObjectsV2 {key}: StorageClass {Py.Repr(listed[key].StorageClass?.Value)}, expected {Py.Repr(sc)}");
            using var got = await s3.GetObjectAsync(bucket, key);
            var data = await S3Fidelity.ReadBody(got);
            Ensure(data.SequenceEqual(body), $"GetObject {key} returned {S3Fidelity.Head(data)}, expected {S3Fidelity.Head(body)}");
            Ensure(got.StorageClass?.Value == sc, $"GetObject {key}: StorageClass {Py.Repr(got.StorageClass?.Value)}");
            Ensure(got.ETag == etags[key], $"GetObject {key}: ETag {Py.Repr(got.ETag)} != PutObject {Py.Repr(etags[key])}");
        }

        var tieringHead = await s3.GetObjectMetadataAsync(bucket, "classes/tiering.bin");
        Ensure(tieringHead.ArchiveStatus is null, $"a fresh INTELLIGENT_TIERING object reported ArchiveStatus={Py.Repr(tieringHead.ArchiveStatus?.Value)}");

        // In-place class change: ONEZONE_IA -> INTELLIGENT_TIERING.
        const string moveKey = "classes/onezone.bin";
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket, SourceKey = moveKey, DestinationBucket = bucket, DestinationKey = moveKey,
            StorageClass = S3StorageClass.IntelligentTiering,
        });
        var moved = await s3.GetObjectMetadataAsync(bucket, moveKey);
        Ensure(moved.StorageClass?.Value == "INTELLIGENT_TIERING", $"after in-place copy StorageClass was {Py.Repr(moved.StorageClass?.Value)}, expected INTELLIGENT_TIERING");
        Ensure(moved.ETag == etags[moveKey], $"in-place class change altered ETag {Py.Repr(etags[moveKey])} -> {Py.Repr(moved.ETag)}");
        var movedData = await S3Fidelity.GetBytes(s3, bucket, moveKey);
        Ensure(movedData.SequenceEqual(objects[moveKey].Body), $"in-place class change altered bytes to {S3Fidelity.Head(movedData)}");
        var relisted = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "classes/" })).S3Objects ?? [])
            .ToDictionary(o => o.Key, o => o.StorageClass?.Value);
        Ensure(
            relisted.Count == 2 && relisted.GetValueOrDefault("classes/onezone.bin") == "INTELLIGENT_TIERING" && relisted.GetValueOrDefault("classes/tiering.bin") == "INTELLIGENT_TIERING",
            $"relist after move: {Py.Repr(relisted.Select(r => $"{r.Key}={r.Value}").ToList())}");
    }
}
