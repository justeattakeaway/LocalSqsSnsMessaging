// Translated from behavioral/s3/check_objects_totalling_over_128_mib_across_three_buckets_all_read_back.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.objects-totalling-over-128-mib-across-three-buckets-all-read-back</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectsTotallingOver128MibAcrossThreeBucketsAllReadBack
{
    private const int Mib = 1024 * 1024;
    private const int ObjectSize = 15 * Mib;
    private const int Buckets = 3;
    private static readonly string[] Keys = ["part-a.bin", "part-b.bin", "part-c.bin"];

    /// <summary>
    /// One account stores nine 15 MiB objects over three buckets, more than 128 MiB in all.
    /// Every PutObject succeeds with the body's MD5 as ETag and every object reads back byte-exact.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/UsingObjects.html
    /// </summary>
    [Test]
    [DisplayName("s3.objects-totalling-over-128-mib-across-three-buckets-all-read-back")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var buckets = new List<string>();
        for (var i = 0; i < Buckets; i++)
        {
            buckets.Add(await S3Fidelity.CreateBucket(ctx, $"sim2real-behav-vol{i}-"));
        }

        var objects = buckets.SelectMany(b => Keys.Select(k => (Bucket: b, Key: k))).ToList();
        var sums = await Pool(ctx, objects, o => Put(s3, o.Bucket, o.Key));
        var bad = objects.Zip(sums).Where(p => p.Second.Md5 != p.Second.ETag).ToList();
        Ensure(bad.Count == 0, $"PutObject ETags differ from the bodies' MD5s (MD5, ETag): {Py.Repr(bad.Select(p => (p.First.Bucket, p.First.Key, p.Second.Md5, p.Second.ETag)).ToList())}");

        var got = await Pool(ctx, objects, o => ReadMd5(s3, o.Bucket, o.Key));
        var want = sums.Select(s => ((long)ObjectSize, s.Md5)).ToList();
        Ensure(got.SequenceEqual(want), $"objects read back as (length, MD5) {Py.Repr(got)}, expected {Py.Repr(want)}");
    }

    /// <summary><c>ThreadPoolExecutor(max_workers=len(_KEYS))</c>: runs <paramref name="fn"/> over the objects, three at a time.</summary>
    private static async Task<List<T>> Pool<T>(FidelityContext ctx, List<(string Bucket, string Key)> objects, Func<(string Bucket, string Key), Task<T>> fn)
    {
        using var slots = new SemaphoreSlim(Keys.Length);
        using (ctx.Concurrently())
        {
            return [.. await Task.WhenAll(objects.Select(async o =>
            {
                await slots.WaitAsync();
                try
                {
                    return await fn(o);
                }
                finally
                {
                    slots.Release();
                }
            }))];
        }
    }

    /// <summary>PutObject a fresh random body; (body MD5, returned ETag).</summary>
    private static async Task<(string Md5, string ETag)> Put(IAmazonS3 s3, string bucket, string key)
    {
        var body = RandomNumberGenerator.GetBytes(ObjectSize);
        var etag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) })).ETag.Trim('"');
        return (Convert.ToHexStringLower(MD5.HashData(body)), etag);
    }

    /// <summary>(length, MD5) of the object as GetObject streams it back.</summary>
    private static async Task<(long Length, string Md5)> ReadMd5(IAmazonS3 s3, string bucket, string key)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long length = 0;
        using var got = await s3.GetObjectAsync(bucket, key);
        var buffer = new byte[Mib];
        int read;
        while ((read = await got.ResponseStream.ReadAsync(buffer)) > 0)
        {
            digest.AppendData(buffer, 0, read);
            length += read;
        }

        return (length, Convert.ToHexStringLower(digest.GetHashAndReset()));
    }
}
