// Translated from behavioral/s3/check_virtual_hosted_and_path_style_clients_agree_on_an_objects_full_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.virtual-hosted-and-path-style-clients-agree-on-an-objects-full-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class VirtualHostedAndPathStyleClientsAgreeOnAnObjectsFullLifecycle
{
    private const string Key = "cross-addr/lifecycle.bin";

    /// <summary>
    /// A virtual-hosted client's PutObject, HeadObject, GetObject, ListObjectsV2 and HeadBucket on
    /// one bucket read back identically through a path-style client, and after DeleteObject both
    /// addressing forms agree the key and its listing entry are gone.
    /// </summary>
    [Test]
    [DisplayName("s3.virtual-hosted-and-path-style-clients-agree-on-an-objects-full-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var vhost = ctx.S3Client(pathStyle: false, maxAttempts: 1);
        var pstyle = ctx.S3Client(pathStyle: true, maxAttempts: 1);

        var body = "cross-addressing-style payload"u8.ToArray();
        var put = await vhost.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(body), ContentType = "application/octet-stream" });
        var etag = put.ETag;

        var vhead = await vhost.GetObjectMetadataAsync(bucket, Key);
        Ensure(vhead.ContentLength == body.Length, $"virtual-hosted HeadObject ContentLength {vhead.ContentLength}, wrote {body.Length}");
        Ensure(vhead.ETag == etag, $"virtual-hosted HeadObject ETag {Py.Repr(vhead.ETag)}, expected {Py.Repr(etag)}");
        using (var vgot = await vhost.GetObjectAsync(bucket, Key))
        {
            Ensure((await S3Fidelity.ReadBody(vgot)).SequenceEqual(body), "virtual-hosted GetObject did not return the written bytes");
            Ensure(vgot.ETag == etag, $"virtual-hosted GetObject ETag {Py.Repr(vgot.ETag)}, expected {Py.Repr(etag)}");
        }

        var vlisted = await Listed(vhost, bucket);
        Ensure(vlisted.Select(o => o.Key).SequenceEqual([Key]), $"virtual-hosted ListObjectsV2 listed {Py.Repr(vlisted.Select(o => o.Key).ToList())}");
        Ensure(vlisted[0].ETag == etag, $"virtual-hosted listing ETag {Py.Repr(vlisted[0].ETag)}, expected {Py.Repr(etag)}");
        var vheadbucket = await vhost.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
        Ensure(vheadbucket.HttpStatusCode == HttpStatusCode.OK, $"virtual-hosted HeadBucket answered {(int)vheadbucket.HttpStatusCode}");

        var phead = await pstyle.GetObjectMetadataAsync(bucket, Key);
        Ensure(phead.ContentLength == vhead.ContentLength, $"path-style HeadObject ContentLength {phead.ContentLength}, virtual-hosted saw {vhead.ContentLength}");
        Ensure(phead.ETag == etag, $"path-style HeadObject ETag {Py.Repr(phead.ETag)}, expected {Py.Repr(etag)}");
        using (var pgot = await pstyle.GetObjectAsync(bucket, Key))
        {
            Ensure((await S3Fidelity.ReadBody(pgot)).SequenceEqual(body), "path-style GetObject did not return the written bytes");
            Ensure(pgot.ETag == etag, $"path-style GetObject ETag {Py.Repr(pgot.ETag)}, expected {Py.Repr(etag)}");
        }

        var plisted = await Listed(pstyle, bucket);
        Ensure(plisted.Select(o => o.Key).SequenceEqual([Key]), $"path-style ListObjectsV2 listed {Py.Repr(plisted.Select(o => o.Key).ToList())}");
        Ensure(plisted[0].ETag == etag, $"path-style listing ETag {Py.Repr(plisted[0].ETag)}, expected {Py.Repr(etag)}");
        var pheadbucket = await pstyle.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
        Ensure(pheadbucket.HttpStatusCode == HttpStatusCode.OK, $"path-style HeadBucket answered {(int)pheadbucket.HttpStatusCode}");

        await vhost.DeleteObjectAsync(bucket, Key);

        (string? Code, string? Message) noKey = ("NoSuchKey", "The specified key does not exist.");
        foreach (var (client, label) in new[] { (vhost, "virtual-hosted"), (pstyle, "path-style") })
        {
            var error = await FidelityContext.CaptureError(async () =>
            {
                using var _ = await client.GetObjectAsync(bucket, Key);
            }) ?? throw Fail("expected the call to be refused, but it succeeded");
            var got = (error.Code, error.Message ?? "");
            Ensure(got == noKey, $"{label} GetObject after delete: expected {S3Fidelity.Repr(noKey)}, got {S3Fidelity.Repr(got)}");
            var after = await Listed(client, bucket);
            Ensure(after.Count == 0, $"{label} ListObjectsV2 still lists the deleted key: {Py.Repr(after.Select(o => o.Key).ToList())}");
        }
    }

    /// <summary><c>client.list_objects_v2(Bucket=bucket, Prefix="cross-addr/").get("Contents", [])</c>.</summary>
    private static async Task<List<S3Object>> Listed(IAmazonS3 client, string bucket) =>
        (await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "cross-addr/" })).S3Objects ?? [];
}
